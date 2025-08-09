using Naninovel;
using Cysharp.Threading.Tasks;

namespace NaninovelStartupTimeLogger
{
    public class LogStartupCommand : Command
    {
		public UniTask ExecuteAsync(AsyncToken asyncToken = default)
    	{
			#if UNITY_EDITOR || DEVELOPMENT_BUILD
        	// 開発中のみ、ロガーのメソッドを呼び出す
        	StartupTimeLogger.Log("First Naninovel Script Executed");
			#endif
        	return UniTask.CompletedTask;
    	}
    }
}
