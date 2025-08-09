using UnityEngine;
using System.Diagnostics;
using Naninovel;
using Debug = UnityEngine.Debug;

namespace NaninovelStartupTimeLogger
{
    [InitializeAtRuntime]
    public class StartupTimeLogger : IEngineService
    {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
    private static Stopwatch stopwatch = new Stopwatch();
    private static bool isFirstLog = true;
#endif

          public UniTask InitializeService ()
          {
#if UNITY_EDITOR || DEVELOPMENT_BUILD

              stopwatch.Start();
#endif
              return UniTask.CompletedTask;
          }

         public void ResetService () { }
         public void DestroyService () { }
         public static void Log (string message)
         {
             if (isFirstLog)
             {
#if UNITY_EDITOR || DEVELOPMENT_BUILD

                 stopwatch.Stop();
                 Debug.Log($"[StartupTime] {message} at {stopwatch.Elapsed.TotalSeconds:F3} seconds.");
#endif
             }
             isFirstLog = false;
         }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSplashScreen)]
        private static void OnBeforeSplashScreen ()
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        Debug.Log("Application Startup");
#endif
        }
    }
}
