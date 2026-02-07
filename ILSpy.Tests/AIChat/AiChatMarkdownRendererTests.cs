using System.Linq;
using System.Threading;
using System.Windows.Documents;

using ICSharpCode.ILSpy.AIChat;

using NUnit.Framework;

using Shouldly;

namespace ICSharpCode.ILSpy.Tests.AIChat
{
	[TestFixture]
	[Apartment(ApartmentState.STA)]
	public class AiChatMarkdownRendererTests
	{
		[Test]
		public void Render_Markdown_WithHeadingAndList_ProducesMultipleBlocks()
		{
			var parser = new AiChatLinkReferenceParser();
			var renderer = new AiChatMarkdownRenderer(parser, _ => { });
			var message = new AiChatMessage {
				Id = 1,
				Role = AiChatMessageRole.Assistant,
				Text = "# Title\n\n- one\n- two\n\n`inline`",
			};

			var blocks = renderer.Render(message);

			blocks.Count.ShouldBeGreaterThanOrEqualTo(3);
			blocks[0].ShouldBeOfType<Paragraph>();
			blocks.OfType<System.Windows.Documents.List>().Any().ShouldBeTrue();
		}

		[Test]
		public void Render_WithSymbolReference_CreatesHyperlink()
		{
			var parser = new AiChatLinkReferenceParser();
			var renderer = new AiChatMarkdownRenderer(parser, _ => { });
			var message = new AiChatMessage {
				Id = 2,
				Role = AiChatMessageRole.Assistant,
				Text = "See `T:ICSharpCode.ILSpy.AIChat.AiChatToolDispatcher (line 56)`",
			};

			var blocks = renderer.Render(message);
			var body = blocks.Skip(1).OfType<Paragraph>().FirstOrDefault();

			body.ShouldNotBeNull();
			body!.Inlines.OfType<Hyperlink>().Any().ShouldBeTrue();
		}

		[Test]
		public void Render_FencedCodeBlock_UsesCodeBlockContainer()
		{
			var parser = new AiChatLinkReferenceParser();
			var renderer = new AiChatMarkdownRenderer(parser, _ => { });
			var message = new AiChatMessage {
				Id = 3,
				Role = AiChatMessageRole.Assistant,
				Text = "```csharp\npublic class A {}\n```",
			};

			var blocks = renderer.Render(message);

			blocks.Skip(1).Any(block => block is BlockUIContainer).ShouldBeTrue();
		}
	}
}
