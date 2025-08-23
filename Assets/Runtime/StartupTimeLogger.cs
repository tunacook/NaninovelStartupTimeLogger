using UnityEngine;
using Naninovel;
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
    /// Splash前(BeforeSplashScreen) → 初回に観測できたスクリプト終了（停止 or 遷移）までを計測。
    /// 時間は終了時(LogEnd)のみ出力。Editor または Development Build でのみ動作。
    /// </summary>
    [Preserve] // クラスごと保持（IL2CPP対策）
    public sealed class StartupTimeLogger : MonoBehaviour
    {
        private const float  HardTimeoutSec = 30f; // 最終フェイルセーフ
        private const float  GraceLockSec   = 3f;  // Engine ready 後、ここまでにロックできなければ fastpath

        private static readonly SysStopwatch Stopwatch = new SysStopwatch();
        private static volatile bool _sArmed;

        private IScriptPlayer _player;
        private bool _wasPlaying, _endLogged, _sawInitialize;
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
            ArmOnce("BeforeSplashScreen");
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
                ArmOnce("EditorHook");
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
                try { local = Engine.GetService<IScriptPlayer>(); }
                catch { /* 初期化前 */ }
                yield return null;
            }

            if (local == null) { LogEnd("engine_not_ready_timeout"); yield break; }

            _player         = local;
            _readyRealtime = Time.realtimeSinceStartup;
            UnityEngineDebug.Log("[StartupTimeLogger] ENGINE_READY");

            // 監視ループ（EndOfFrame ダブルチェックで取りこぼし低減）
            while (!_endLogged)
            {
                TickDetection();

                yield return new WaitForEndOfFrame();
                if (_endLogged) yield break;

                TickDetection();
                yield return null;
            }
        }

        private void TickDetection()
        {
            var elapsedSinceReady = Time.realtimeSinceStartup - _readyRealtime;

            var currentName = SafeGetPlayedScriptName(_player);
            var isPlaying   = SafeGetIsPlaying(_player);

            // Verbose: 状態変化のたびに出す（Conditional なのでシンボル未定義ならビルドされない）
            if (currentName != _lastScriptName || isPlaying != _wasPlaying)
                VLog($"[StartupTimeLogger] STATE playing={isPlaying} | script='{currentName}' | norm='{Normalize(currentName)}'");

            // 1) 初回に観測できたスクリプトをロック（isPlaying に依らず）
            if (!_sawInitialize && !string.IsNullOrEmpty(currentName))
            {
                _initScriptId  = Normalize(currentName);
                _sawInitialize = true;
                VLog($"[StartupTimeLogger] INIT_LOCKED id='{_initScriptId}'");
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
                VWarn($"[StartupTimeLogger] TIMEOUT_INFO last='{_lastScriptName}' | current='{currentName}' | playing={isPlaying} | init='{_initScriptId}'");
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

            var ms = Stopwatch.Elapsed.TotalMilliseconds;
            UnityEngineDebug.Log($"[StartupTimeLogger] {tag} (t={ms:F1} ms)");
        }

        // --- ヘルパー（1.20 反射フォールバック） ---
        private static string SafeGetPlayedScriptName(IScriptPlayer p)
        {
            try
            {
                var played = p?.PlayedScript;
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
            try { return p != null && p.Playing; }
            catch { return false; }
        }

        // --- Verbose出力（Conditional で呼び出し自体をビルドから除外）---
        [System.Diagnostics.Conditional("NSTL_VERBOSE")]
        private static void VLog(string message) => UnityEngineDebug.Log(message);

        [System.Diagnostics.Conditional("NSTL_VERBOSE")]
        private static void VWarn(string message) => UnityEngineDebug.LogWarning(message);

        // 武装（1回だけ）
        private static void ArmOnce(string source)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (_sArmed) return;
            _sArmed = true;

            Stopwatch.Restart();
            UnityEngineDebug.Log($"[StartupTimeLogger] ARMED src={source}");

            var logger = UnityEngineDebug.unityLogger;
            VLog($"[StartupTimeLogger] LOGGER enabled={logger.logEnabled} | filter={logger.filterLogType}");
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
