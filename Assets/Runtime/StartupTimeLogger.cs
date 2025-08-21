using UnityEngine;
using Naninovel;
using UnityEngine.Scripting;

#if UNITY_EDITOR
using UnityEditor;
#endif

// 衝突回避エイリアス
using SysStopwatch = System.Diagnostics.Stopwatch;
using UnityEngineDebug = UnityEngine.Debug;

namespace NaninovelStartupTimeLogger
{
    /// <summary>
    /// Splash前(BeforeSplashScreen) → 「最初に再生されたスクリプト」終了までの時間を計測。
    /// 経過時間の表示は終了時(LogEnd)のみ。Editor または Development Build でのみ動作。
    /// プロジェクト側の改変は不要。
    /// </summary>
    public sealed class StartupTimeLogger : MonoBehaviour
    {
        private const float HardTimeoutSeconds = 180f; // フェイルセーフ
        private static readonly SysStopwatch Stopwatch = new SysStopwatch();

        // 状態
        private IScriptPlayer player;
        private bool wasPlaying, endLogged, sawInitialize;
        private string lastScriptName;
        private float elapsed;

        // 「初回に再生されたスクリプトID（正規化済み）」を initialize 扱いとしてロック
        private string initScriptId;

        /// <summary>診断用詳細ログ（時間は出さない）</summary>
        public static bool Verbose = false;

        private static bool _armed;

        private static bool ShouldRun =>
#if UNITY_EDITOR
            true
#else
            UnityEngineDebug.isDebugBuild
#endif
        ;

        // ───────── 起点（ドメインリロード有り環境で呼ばれる） ─────────
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
            UnityEngineDebug.Log(msg); // ← 時間はここでは出さない
        }

        private void Start()
        {
            if (!ShouldRun) { enabled = false; return; }
            StartCoroutine(Watch());
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

            UnityEngineDebug.Log("[StartupTimeLogger] Naninovel Engine ready."); // ← 時間は出さない

            while (!endLogged)
            {
                elapsed += Time.unscaledDeltaTime;

                var currentName = SafeGetPlayedScriptName(player);
                var isPlaying   = SafeGetIsPlaying(player);

                // 状態変化の可視化（Verbose時のみ・時間は出さない）
                if (Verbose && (currentName != lastScriptName || isPlaying != wasPlaying))
                {
                    UnityEngineDebug.Log(
                        $"[StartupTimeLogger] state change: isPlaying={isPlaying}, script='{currentName}' (norm='{NormalizeScriptId(currentName)}')"
                    );
                }

                // 初回に再生されたスクリプト名を「initialize扱い」としてロック
                if (!sawInitialize && isPlaying && !string.IsNullOrEmpty(currentName))
                {
                    initScriptId = NormalizeScriptId(currentName);
                    sawInitialize = true;
                    if (Verbose) UnityEngineDebug.Log($"[StartupTimeLogger] initialize locked to '{initScriptId}'");
                }

                // 終了条件：
                //  1) 再生が停止した（wasPlaying → !isPlaying）
                //  2) initScriptId から別スクリプトへ切り替わった
                bool leftInitialize =
                    (sawInitialize && wasPlaying && !isPlaying) ||
                    (sawInitialize && isPlaying &&
                     !string.IsNullOrEmpty(initScriptId) &&
                     NormalizeScriptId(lastScriptName) == initScriptId &&
                     NormalizeScriptId(currentName)   != initScriptId);

                if (leftInitialize)
                {
                    LogEnd("init_script_end");
                    yield break;
                }

                // タイムアウト（フェイルセーフ）
                if (elapsed >= HardTimeoutSeconds)
                {
                    if (Verbose)
                        UnityEngineDebug.LogWarning(
                            $"[StartupTimeLogger] timeout before detecting initialize end. last='{lastScriptName}', current='{currentName}', isPlaying={isPlaying}, init='{initScriptId}'");
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

                // 1) PlayedScript.ScriptName があれば使用（反射で安全に）
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
    }
}
