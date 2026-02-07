using System;
using System.Diagnostics;
using System.Threading;

namespace ICSharpCode.ILSpy.AIChat
{
	#nullable enable

	internal static class AiChatLog
	{
		private static long requestId;

		internal static long NextRequestId()
		{
			return Interlocked.Increment(ref requestId);
		}

		internal static void Info(string message)
		{
			Trace.TraceInformation("[AIChat] " + message);
		}

		internal static void Warn(string message)
		{
			Trace.TraceWarning("[AIChat] " + message);
		}

		internal static void Error(Exception exception, string context)
		{
			Trace.TraceError("[AIChat] " + context + Environment.NewLine + exception);
		}
	}
}
