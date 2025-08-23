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
        public  static bool  Verbose        = false;

        private static readonly SysStopwatch Stopwatch = new SysStopwatch();
        private static volatile bool sArmed = false;

        private IScriptPlayer player;
        private bool wasPlaying, endLogged, sawInitialize;
        private string lastScriptName;
        private string initScriptId; // 正規化済みの初回スクリプトID
        private float  readyRealtime;

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
            if (!endLogged && sArmed) LogEnd("init_script_end_on_quit");
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

            player        = local;
            readyRealtime = Time.realtimeSinceStartup;
            UnityEngineDebug.Log("[StartupTimeLogger] Naninovel Engine ready."); // 時間は出さない

            // 監視ループ（EndOfFrame ダブルチェックで取りこぼし低減）
            while (!endLogged)
            {
                TickDetection();

                yield return new WaitForEndOfFrame();
                if (endLogged) yield break;

                TickDetection();
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

            // 1) 初回に観測できたスクリプトをロック（isPlaying に依らず）
            if (!sawInitialize && !string.IsNullOrEmpty(currentName))
            {
                initScriptId  = Normalize(currentName);
                sawInitialize = true;
                if (Verbose) UnityEngineDebug.Log($"[StartupTimeLogger] initialize locked to '{initScriptId}'");
            }

            // 2) 終了：停止 or initScriptId から別スクリプトへ遷移
            bool leftInitialize =
                (sawInitialize && wasPlaying && !isPlaying) ||
                (sawInitialize &&
                 Normalize(lastScriptName) == initScriptId &&
                 Normalize(currentName)    != initScriptId);

            if (leftInitialize) { LogEnd("init_script_end"); return; }

            // 3) fastpath：Engine ready 後、一定時間ロックできない＝既に終わっていた
            if (!sawInitialize && elapsedSinceReady >= GraceLockSec) { LogEnd("init_script_end_fastpath"); return; }

            // 4) timeout：どれでもない
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

        // Devビルドで一時的にinfoを開放する
        private static void ForceInfoLoggingForDev()
        {
#if DEVELOPMENT_BUILD && !UNITY_EDITOR
    // Infoログが抑止されていても、このフレームだけは通す
    UnityEngineDebug.unityLogger.logEnabled = true;
    UnityEngineDebug.unityLogger.filterLogType = LogType.Log; // すべて許可
#endif
        }

        // ArmOnce の最後でロガー状態を Warning で可視化（デバッグ用）
        private static void ArmOnce(string source)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            // 既に武装済みなら何もしない（多重ログ防止）
            if (sArmed) return;
            sArmed = true;

            Stopwatch.Restart();
            UnityEngineDebug.Log($"[StartupTimeLogger] armed ({source})");

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
