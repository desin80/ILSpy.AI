using System.Threading;

using ICSharpCode.ILSpy.AIChat;

using NUnit.Framework;

using Shouldly;

namespace ICSharpCode.ILSpy.Tests.AIChat
{
	[TestFixture]
	[Apartment(ApartmentState.STA)]
	public class AiChatPaneMarkdownRenderTests
	{
		[Test]
		public void RenderAssistantMarkdown_DoesNotThrow()
		{
			var parser = new AiChatLinkReferenceParser();
			var renderer = new AiChatMarkdownRenderer(parser, _ => { });
			var message = new AiChatMessage {
				Id = 10,
				Role = AiChatMessageRole.Assistant,
				Text = "# Heading\n\n`T:ICSharpCode.ILSpy.AIChat.AiChatPane (line 100)`",
			};

			Should.NotThrow(() => renderer.Render(message));
		}

		[Test]
		public void RenderUserMarkdown_DoesNotThrow()
		{
			var parser = new AiChatLinkReferenceParser();
			var renderer = new AiChatMarkdownRenderer(parser, _ => { });
			var message = new AiChatMessage {
				Id = 11,
				Role = AiChatMessageRole.User,
				Text = "**bold** and `code`",
			};

			Should.NotThrow(() => renderer.Render(message));
		}
	}
}
