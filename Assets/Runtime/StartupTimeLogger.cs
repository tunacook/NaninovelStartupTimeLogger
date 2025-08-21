using UnityEngine;
using Naninovel;
using UnityEngine.Scripting;

#if UNITY_EDITOR
using UnityEditor;
#endif

using SysStopwatch = System.Diagnostics.Stopwatch;
using UnityEngineDebug = UnityEngine.Debug;

namespace NaninovelStartupTimeLogger
{
    /// <summary>
    /// Splash前(BeforeSplashScreen) → initialize.nani 終了までを測定。
    /// 経過時間は終了時(LogEnd)のみ出力。Editor/Development Build のみ動作。
    /// </summary>
    public sealed class StartupTimeLogger : MonoBehaviour
    {
        private static readonly SysStopwatch Stopwatch = new SysStopwatch();
        private const float HardTimeoutSeconds = 180f;

        private IScriptPlayer player;
        private bool sawInitialize, wasPlaying, endLogged;
        private string lastScriptName;
        private float elapsed;
        private string initScriptId; // 正規化済みの初回スクリプトIDを保持

        /// <summary>診断の詳細ログ（時間は出さない）</summary>
        public static bool Verbose = false;

        private static bool _armed;

        private static bool ShouldRun =>
#if UNITY_EDITOR
            true
#else
            UnityEngineDebug.isDebugBuild
#endif
        ;

        // ───────── 起点（ドメインリロード有りで呼ばれる） ─────────
        [Preserve]
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSplashScreen)]
        private static void BeforeSplash()
        {
            if (!ShouldRun) return;
            ArmOnce("[StartupTimeLogger] armed (BeforeSplashScreen)");
        }

        // ───────── 常駐設置 ─────────
        [Preserve]
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
            if (!ShouldRun) return;
            var go = new GameObject("[StartupTimeLogger]");
            Object.DontDestroyOnLoad(go);
            go.AddComponent<StartupTimeLogger>();
        }

#if UNITY_EDITOR
        // ───────── Editor: ドメインリロード無効でも再生直前に武装 ─────────
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
            UnityEngineDebug.Log(msg); // ← ここでは時間を出さない
        }

        private void Start()
        {
            if (!ShouldRun) { enabled = false; return; }
            StartCoroutine(Watch());
        }

        private System.Collections.IEnumerator Watch()
        {
            // Naninovel IScriptPlayer 取得待ち
            IScriptPlayer local = null;
            while (local == null)
            {
                try { local = Engine.GetService<IScriptPlayer>(); }
                catch { /* 初期化前 */ }
                yield return null;
            }
            player = local;

            UnityEngineDebug.Log("[StartupTimeLogger] Naninovel Engine ready."); // 時間は出さない

            while (!endLogged)
            {
                elapsed += Time.unscaledDeltaTime;

                var currentName = SafeGetPlayedScriptName(player);
                var isPlaying   = SafeGetIsPlaying(player);

                if (!sawInitialize && isPlaying && !string.IsNullOrEmpty(currentName))
                {
                    initScriptId = NormalizeScriptId(currentName);
                    sawInitialize = true;
                    if (Verbose) UnityEngineDebug.Log($"[StartupTimeLogger] initialize locked to '{initScriptId}'");
                }

                bool leftInitialize =
                    (sawInitialize && wasPlaying && !isPlaying) ||
                    (sawInitialize && isPlaying && NormalizeScriptId(lastScriptName) == initScriptId && NormalizeScriptId(currentName) != initScriptId);

                if (leftInitialize)
                {
                    LogEnd("init_script_end");
                    yield break;
                }

                if (elapsed >= HardTimeoutSeconds)
                {
                    if (Verbose)
                        UnityEngineDebug.LogWarning($"[StartupTimeLogger] timeout before detecting initialize end. last='{lastScriptName}', current='{currentName}', isPlaying={isPlaying}");
                    LogEnd("init_script_end_timeout");
                    yield break;
                }

                lastScriptName = currentName;
                wasPlaying     = isPlaying;
                yield return null;
            }
        }

        /// <summary>終了時のみ、経過msを出力。</summary>
        private void LogEnd(string tag)
        {
            if (endLogged) return;
            endLogged = true;
            var totalMs = Stopwatch.Elapsed.TotalMilliseconds;
            UnityEngineDebug.Log($"[StartupTimeLogger] {tag} (t={totalMs:F1} ms)");
        }

        // ───────── ヘルパー：スクリプト名取得（1.20差異に対応） ─────────
        private static string SafeGetPlayedScriptName(IScriptPlayer p)
        {
            try
            {
                var played = p?.PlayedScript;
                if (played == null) return null;

                // 1) PlayedScript.ScriptName があれば使う（実行時反射）
                var t = played.GetType();
                var propScriptName = t.GetProperty("ScriptName");
                if (propScriptName != null)
                {
                    var s = propScriptName.GetValue(played) as string;
                    if (!string.IsNullOrEmpty(s)) return s;
                }

                // 2) PlayedScript.Script.Name を試す
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

        private static string NormalizeScriptId(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            var s = raw.Replace("\\", "/");
            var last = s.Contains("/") ? s[(s.LastIndexOf('/') + 1)..] : s;
            var dot = last.LastIndexOf('.');
            if (dot >= 0) last = last.Substring(0, dot);
            return last.ToLowerInvariant();
        }

        private static bool IsInitialize(string raw) => NormalizeScriptId(raw) == "initialize";
    }
}
