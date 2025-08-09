using UnityEngine;
using System.Diagnostics;
using Naninovel;
using Cysharp.Threading.Tasks;

namespace NaninovelStartupTimeLogger
{
    [InitializeAt(999)]
    public class StartupTimeLogger : IEngineService
    {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
    private static Stopwatch stopwatch = new Stopwatch();
    private static bool isFirstLog = true;
#endif

        public UniTask InitializeService()
        {
            // サービスが初期化されたタイミングでログを出す
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        Debug.Log("Naninovel Engine Initialized");
#endif
            return UniTask.CompletedTask;
        }

        public void ResetService() { }
        public void DestroyService() { }

        public static void Log(string message)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        if (isFirstLog)
        {
            stopwatch.Start();
            isFirstLog = false;
            Debug.Log($"[StartupTime] Stopwatch started.");
        }
        Debug.Log($"[StartupTime] {message} at {stopwatch.Elapsed.TotalSeconds:F3} seconds.");
#endif
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSplashScreen)]
        private static void OnBeforeSplashScreen()
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        Debug.Log("Application Startup");
#endif
        }
    }
}
