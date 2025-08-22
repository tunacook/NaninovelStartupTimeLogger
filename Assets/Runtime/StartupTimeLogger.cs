using UnityEngine;
using Naninovel;
using UnityEngine.Scripting;

#if UNITY_EDITOR
using UnityEditor;
#endif

// 衝突回避エイリアス
using SysStopwatch    = System.Diagnostics.Stopwatch;
using UnityEngineDebug = UnityEngine.Debug;

namespace NaninovelStartupTimeLogger
{
    /// <summary>
    /// Splash前(BeforeSplashScreen) → 初回に観測できたスクリプトの終了（停止 or 別スクリプト遷移）までを計測。
    /// 時間は終了時(LogEnd)のみ出力。Editor または Development Build のみ動作。プロジェクト側改変不要。
    /// </summary>
    public sealed class StartupTimeLogger : MonoBehaviour
    {
        // ===== 調整パラメータ =====
        private const string Version           = "0.1.9";
        private const float  HardTimeoutSec    = 30f;  // 最終フェイルセーフ
        private const float  GraceLockSec      = 3f;   // Engine ready 後、この秒数で初回スクリプトが見つからなければ fastpath
        public  static bool  Verbose           = false;

        // ===== 内部状態 =====
        private static readonly SysStopwatch Stopwatch = new SysStopwatch();
        private static bool _armed;

        private IScriptPlayer player;
        private bool wasPlaying, endLogged, sawInitialize;
        private string lastScriptName;
        private string initScriptId;                  // 初回に観測できたスクリプトID（正規化）
        private float readyRealtime;

        private static bool ShouldRun =>
#if UNITY_EDITOR
            true
#else
            UnityEngineDebug.isDebugBuild
#endif
        ;

        // 起点：Splash前でストップウォッチ開始
        [Preserve]
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSplashScreen)]
        private static void BeforeSplash()
        {
            if (!ShouldRun) return;
            ArmOnce("[StartupTimeLogger] armed (BeforeSplashScreen)");
        }

        // 可能な限り早く常駐設置（BeforeSceneLoad）
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
        // ドメインリロード無効でも再生直前に武装
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

        private static void ArmOnce(string msg)
        {
            if (_armed) return;
            _armed = true;
            Stopwatch.Restart();
            UnityEngineDebug.Log($"{msg} [v{Version}]"); // ← ここは時間なし
        }

        private void Start()
        {
            if (!ShouldRun) { enabled = false; return; }
            StartCoroutine(Watch());
        }

        private void OnApplicationQuit()
        {
            // 最後の保険：まだ出していなければここで時間を出す
            if (!endLogged && _armed) LogEnd("init_script_end_on_quit");
        }

        private System.Collections.IEnumerator Watch()
        {
            // Naninovel の IScriptPlayer 取得待ち
            IScriptPlayer local = null;
            while (local == null)
            {
                try { local = Engine.GetService<IScriptPlayer>(); }
                catch { /* 初期化前 */ }
                yield return null;
            }
            player = local;

            UnityEngineDebug.Log("[StartupTimeLogger] Naninovel Engine ready."); // 時間なし
            readyRealtime = Time.realtimeSinceStartup;

            // 監視ループ
            while (!endLogged)
            {
                TickDetection(); // Update 相当

                // EndOfFrame でもう一度チェック（取りこぼし低減）
                yield return new WaitForEndOfFrame();
                if (endLogged) yield break; // すでに終了したら抜ける

                TickDetection(); // Late/EOF相当

                // 次フレームへ
                yield return null;
            }
        }

        private void TickDetection()
        {
            var elapsedSinceReady = Time.realtimeSinceStartup - readyRealtime;

            var currentName = SafeGetPlayedScriptName(player);
            var isPlaying   = SafeGetIsPlaying(player);

            if (Verbose && (currentName != lastScriptName || isPlaying != wasPlaying))
            {
                UnityEngineDebug.Log(
                    $"[StartupTimeLogger] state change: isPlaying={isPlaying}, script='{currentName}' (norm='{Normalize(currentName)}')");
            }

            // 初回に観測できたスクリプトIDをロック（isPlayingに依らず、名前が取れた瞬間）
            if (!sawInitialize && !string.IsNullOrEmpty(currentName))
            {
                initScriptId = Normalize(currentName);
                sawInitialize = true;
                if (Verbose) UnityEngineDebug.Log($"[StartupTimeLogger] initialize locked to '{initScriptId}'");
            }

            // 終了条件：停止 or initScriptId から別スクリプトへ遷移
            bool leftInitialize =
                (sawInitialize && wasPlaying && !isPlaying) ||
                (sawInitialize &&
                 Normalize(lastScriptName) == initScriptId &&
                 Normalize(currentName)    != initScriptId);

            if (leftInitialize)
            {
                LogEnd("init_script_end");
                return;
            }

            // fastpath：Engine ready 後、一定時間ロックできなければ既に終わっていたとみなす
            if (!sawInitialize && elapsedSinceReady >= GraceLockSec)
            {
                LogEnd("init_script_end_fastpath");
                return;
            }

            // timeout：何も起きないまま経過
            if (elapsedSinceReady >= HardTimeoutSec)
            {
                if (Verbose)
                    UnityEngineDebug.LogWarning(
                        $"[StartupTimeLogger] timeout. last='{lastScriptName}', current='{currentName}', isPlaying={isPlaying}, init='{initScriptId}'");
                LogEnd("init_script_end_timeout");
                return;
            }

            lastScriptName = currentName;
            wasPlaying     = isPlaying;
        }

        /// <summary>終了時のみ、経過msを出力。</summary>
        private void LogEnd(string tag)
        {
            if (endLogged) return;
            endLogged = true;
            var totalMs = Stopwatch.Elapsed.TotalMilliseconds;
            UnityEngineDebug.Log($"[StartupTimeLogger] {tag} (t={totalMs:F1} ms)");
        }

        // ===== ヘルパー =====

        // Naninovel 1.20 差異に対応：PlayedScript.ScriptName or PlayedScript.Script.Name を反射で拾う
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
