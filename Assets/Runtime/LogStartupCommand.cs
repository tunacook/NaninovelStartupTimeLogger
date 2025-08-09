using Naninovel;
using UnityEngine;

namespace NaninovelStartupTimeLogger
{
    [CommandAlias("logStartup")]
	public class LogStartupCommand : Command
	{
		public override UniTask Execute (AsyncToken token = default)
    	{
		    Debug.Log("Naninovel Script Executed");
#if UNITY_EDITOR || DEVELOPMENT_BUILD
	StartupTimeLogger.Log("Naninovel Script Executed");
#endif
		    return UniTask.CompletedTask;
    	}
	}
}
