using System.Linq;
using UnityEngine;
using Naninovel;
using Naninovel.UI;
using UnityEngine.Scripting;

#if UNITY_EDITOR
using UnityEditor;
#endif

// 衝突回避エイリアス
using SysStopwatch     = System.Diagnostics.Stopwatch;
using UnityEngineDebug = UnityEngine.Debug;

namespace NaninovelStartupTimeLogger
{
    /// <summary>
    /// Splash前(BeforeSplashScreen) → 初回に観測できたスクリプト終了（停止 or 遷移）・タイトル表示までを計測。
    /// 途中の内訳（Naninovel のサービスの初期化・後処理タスク）も 1 つずつ区切って出す。
    /// 各行の t は起点からの経過ミリ秒、+ は前の行からの経過。Editor または Development Build でのみ動作。
    /// </summary>
    [Preserve] // クラスごと保持（IL2CPP対策）
    public sealed class StartupTimeLogger : MonoBehaviour
    {
        private const float  HardTimeoutSec = 30f; // 最終フェイルセーフ
        private const float  GraceLockSec   = 3f;  // Engine ready 後、ここまでにロックできなければ fastpath
        public  static bool  Verbose        = false;

        private static readonly SysStopwatch Stopwatch = new SysStopwatch();
        private static volatile bool _sArmed;
        private static double _sLastMs;

        private IScriptPlayer _player;
        private bool _wasPlaying, _endLogged, _sawInitialize, _titleDone;
        private string _lastScriptName;
        private string _initScriptId; // 正規化済みの初回スクリプトID
        private float  _readyRealtime;

        // ★ 実行条件は“コンパイル時”に固定（Devビルドで確実にtrue）
        private static bool ShouldRun =>
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            true
#else
            false
#endif
        ;

        // ---- 起点：Splash前でストップウォッチ開始 ----
        [Preserve]
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSplashScreen)]
        private static void BeforeSplash()
        {
            if (!ShouldRun) return;
            ArmOnce("[StartupTimeLogger] armed (BeforeSplashScreen)");
        }

        // ---- できるだけ早く常駐設置（BeforeSceneLoad）----
        [Preserve]
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Install()
        {
            if (!ShouldRun) return;
            var go = new GameObject("[StartupTimeLogger]");
            Object.DontDestroyOnLoad(go);
            go.AddComponent<StartupTimeLogger>();
        }

#if UNITY_EDITOR
        // Editor: ドメインリロード無効でも再生直前に武装
        [InitializeOnLoadMethod]
        private static void EditorHook()
        {
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }
        private static void OnPlayModeStateChanged(PlayModeStateChange change)
        {
            if (change == PlayModeStateChange.ExitingEditMode)
            {
                if (!ShouldRun) return;
                ArmOnce("[StartupTimeLogger] armed (Editor hook)");
            }
        }
#endif

        private void Start()
        {
            if (!ShouldRun) { enabled = false; return; }
            StartCoroutine(Watch());
        }

        private void OnApplicationQuit()
        {
            // 最後の保険：まだ出していなければここで時間を出す
            if (!_endLogged && _sArmed) LogEnd("init_script_end_on_quit");
        }

        private System.Collections.IEnumerator Watch()
        {
            // Naninovel IScriptPlayer 取得待ち（ここで止まる可能性に備え、HardTimeoutSec 到達時は終わらせる）
            IScriptPlayer local = null;
            float waitStart = Time.realtimeSinceStartup;
            while (local == null && (Time.realtimeSinceStartup - waitStart) < HardTimeoutSec)
            {
                // サービスは初期化の途中から取れるが、ready（fastpath の起点）は初期化（UI の生成など）の完了後にする。
                // 初期化スクリプトは初期化の完了後に再生されるので、これより前に始まることはない
                try { local = Engine.Initialized ? Engine.GetService<IScriptPlayer>() : null; }
                catch { /* 初期化前 */ }
                yield return null;
            }

            if (local == null) { LogEnd("engine_not_ready_timeout"); yield break; }

            _player        = local;
            _readyRealtime = Time.realtimeSinceStartup;
            Mark("engine_ready");

            // 監視ループ（EndOfFrame ダブルチェックで取りこぼし低減）
            while (!_endLogged || !_titleDone)
            {
                TickDetection();
                TickTitle();

                yield return new WaitForEndOfFrame();
                if (_endLogged && _titleDone) yield break;

                TickDetection();
                TickTitle();
                yield return null;
            }
        }

        private void TickTitle()
        {
            if (_titleDone || !Engine.Initialized) return;

            var title = Engine.GetService<IUIManager>()?.GetUI<ITitleUI>();
            if (title is { Visible: true })
            {
                _titleDone = true;
                Mark("title_ui_visible");
            }
            // タイトルを経ずに始まる経路（初期化スクリプトから本編へ進むなど）では見張り続けない
            else if (Time.realtimeSinceStartup - _readyRealtime >= HardTimeoutSec)
            {
                _titleDone = true;
                Mark("title_ui_not_shown_timeout");
            }
        }

        private void TickDetection()
        {
            if (_endLogged) return;

            var elapsedSinceReady = Time.realtimeSinceStartup - _readyRealtime;

            var currentName = SafeGetPlayedScriptName(_player);
            var isPlaying   = SafeGetIsPlaying(_player);

            if (Verbose && (currentName != _lastScriptName || isPlaying != _wasPlaying))
            {
                UnityEngineDebug.Log(
                    $"[StartupTimeLogger] state change: isPlaying={isPlaying}, script='{currentName}' (norm='{Normalize(currentName)}')");
            }

            // 1) 初回に観測できたスクリプトをロック（isPlaying に依らず）
            if (!_sawInitialize && !string.IsNullOrEmpty(currentName))
            {
                _initScriptId  = Normalize(currentName);
                _sawInitialize = true;
                if (Verbose) UnityEngineDebug.Log($"[StartupTimeLogger] initialize locked to '{_initScriptId}'");
            }

            // 2) 終了：停止 or initScriptId から別スクリプトへ遷移
            bool leftInitialize =
                (_sawInitialize && _wasPlaying && !isPlaying) ||
                (_sawInitialize &&
                 Normalize(_lastScriptName) == _initScriptId &&
                 Normalize(currentName)    != _initScriptId);

            if (leftInitialize) { LogEnd("init_script_end"); return; }

            // 3) fastpath：Engine ready 後、一定時間ロックできない＝既に終わっていた
            if (!_sawInitialize && elapsedSinceReady >= GraceLockSec) { LogEnd("init_script_end_fastpath"); return; }

            // 4) timeout：どれでもない
            if (elapsedSinceReady >= HardTimeoutSec)
            {
                if (Verbose)
                    UnityEngineDebug.LogWarning(
                        $"[StartupTimeLogger] timeout. last='{_lastScriptName}', current='{currentName}', isPlaying={isPlaying}, init='{_initScriptId}'");
                LogEnd("init_script_end_timeout");
                return;
            }

            _lastScriptName = currentName;
            _wasPlaying     = isPlaying;
        }

        /// <summary>終了時のみ、経過msを出力。</summary>
        private void LogEnd(string tag)
        {
            if (_endLogged) return;
            _endLogged = true;
            Mark(tag);
        }

        /// <summary>起点からの経過ms（t）と前の行からの経過ms（+）を付けて出力。</summary>
        private static void Mark(string label)
        {
            var ms = Stopwatch.Elapsed.TotalMilliseconds;
            UnityEngineDebug.Log($"[StartupTimeLogger] t={ms,8:F1}ms (+{ms - _sLastMs,7:F1}) {label}");
            _sLastMs = ms;
        }

        // ---- Naninovel のエンジン初期化の内訳 ----
        private static void HandleInitializationStarted() => Mark("engine_initialize_start");

        private static void HandleInitializationProgress(float progress)
        {
            // Engine.Initialize の進捗: 0〜.25 前処理、.25〜.75 サービスの InitializeService、.75〜1 後処理タスク（UI の生成など）
            if (progress < .25f) Mark($"pre_init_task (progress {progress:F3})");
            else if (progress < .75f)
            {
                var services = Engine.Services;
                var index = Mathf.RoundToInt((progress - .25f) / .5f * services.Count);
                var name = index < services.Count ? services.ElementAt(index).GetType().Name : "?";
                Mark($"initialize_service {name}");
            }
            else Mark($"post_init_task (progress {progress:F3})");
        }

        private static void HandleInitializationFinished() => Mark("engine_initialize_finished");

        // --- ヘルパー（1.20 反射フォールバック） ---
        private static string SafeGetPlayedScriptName(IScriptPlayer p)
        {
            try
            {
                var played = p;
                if (played == null) return null;

                var t = played.GetType();

                var propScriptName = t.GetProperty("ScriptName");
                if (propScriptName != null)
                {
                    var s = propScriptName.GetValue(played) as string;
                    if (!string.IsNullOrEmpty(s)) return s;
                }

                var propScript = t.GetProperty("Script");
                var scriptObj  = propScript?.GetValue(played);
                if (scriptObj != null)
                {
                    var propName = scriptObj.GetType().GetProperty("Name");
                    var s = propName?.GetValue(scriptObj) as string;
                    if (!string.IsNullOrEmpty(s)) return s;
                }

                return null;
            }
            catch { return null; }
        }

        private static bool SafeGetIsPlaying(IScriptPlayer p)
        {
            try { return p is { Playing: true }; }
            catch { return false; }
        }

        // ArmOnce の最後でロガー状態を Warning で可視化（デバッグ用）
        private static void ArmOnce(string source)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            // 既に武装済みなら何もしない（多重ログ防止）
            if (_sArmed) return;
            _sArmed = true;

            Stopwatch.Restart();
            _sLastMs = 0;
            UnityEngineDebug.Log($"[StartupTimeLogger] armed ({source})");

            // ドメインリロード無効の Editor でも二重に購読しないよう、外してから付ける
            Engine.OnInitializationStarted  -= HandleInitializationStarted;
            Engine.OnInitializationStarted  += HandleInitializationStarted;
            Engine.OnInitializationProgress -= HandleInitializationProgress;
            Engine.OnInitializationProgress += HandleInitializationProgress;
            Engine.OnInitializationFinished -= HandleInitializationFinished;
            Engine.OnInitializationFinished += HandleInitializationFinished;

            var logger = UnityEngineDebug.unityLogger;
            UnityEngineDebug.Log($"[StartupTimeLogger] logger state: enabled={logger.logEnabled}, filter={logger.filterLogType}");
#endif
        }

        private static string Normalize(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            var s = raw.Replace("\\", "/");
            var last = s.Contains("/") ? s[(s.LastIndexOf('/') + 1)..] : s;
            var dot = last.LastIndexOf('.');
            if (dot >= 0) last = last.Substring(0, dot);
            return last.ToLowerInvariant();
        }
    }
}
