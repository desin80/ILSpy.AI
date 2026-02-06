using System.Threading;

using ICSharpCode.ILSpy.AIChat;

using NUnit.Framework;

namespace ICSharpCode.ILSpy.Tests.AIChat
{
	[TestFixture]
	[Apartment(ApartmentState.STA)]
	public class AiChatPaneXamlTests
	{
		[Test]
		public void CreateAiChatPane_DoesNotThrow()
		{
			Assert.DoesNotThrow(() => _ = new AiChatPane());
		}
	}
}
