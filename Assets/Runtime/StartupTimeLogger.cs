using UnityEngine;
using System.Diagnostics;
using Naninovel;
using Debug = UnityEngine.Debug;

namespace NaninovelStartupTimeLogger
{
    [InitializeAtRuntime]
    public class StartupTimeLogger : IEngineService
    {
        private static readonly Stopwatch Stopwatch = new Stopwatch();
        private static bool _isFirstLog = true;
        public UniTask InitializeService ()
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD

            Stopwatch.Start();
#endif
            return UniTask.CompletedTask;
        }

        public void ResetService () { }
        public void DestroyService () { }
        public static void Log (string message)
        {
            if (_isFirstLog)
            {
#if UNITY_EDITOR || DEVELOPMENT_BUILD

                Stopwatch.Stop();
                Debug.Log($"[StartupTime] {message} at {Stopwatch.Elapsed.TotalSeconds:F3} seconds.");
#endif
            }
            _isFirstLog = false;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSplashScreen)]
        private static void OnBeforeSplashScreen ()
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            Debug.Log("[StartupTime] Application Startup");
#endif
        }
    }
}
