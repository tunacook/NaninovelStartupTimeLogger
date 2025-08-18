using Naninovel;

namespace NaninovelStartupTimeLogger
{
    [CommandAlias("logStartup")]
    public class LogStartupCommand : Command
    {
        public override UniTask Execute (AsyncToken token = default)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            StartupTimeLogger.Log("[StartupTime] Naninovel Script Executed");
#endif
            return UniTask.CompletedTask;
        }
    }
}
