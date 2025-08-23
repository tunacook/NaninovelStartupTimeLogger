using UnityEngine;
using UnityEngine.Scripting;

namespace NaninovelStartupTimeLogger
{
    [Preserve]
    public static class StartupTimeLoggerSmoke
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSplashScreen)]
        private static void Ping()
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            Debug.LogWarning("[StartupTimeLogger] SMOKE (BeforeSplashScreen)");
#endif
        }
    }
}
