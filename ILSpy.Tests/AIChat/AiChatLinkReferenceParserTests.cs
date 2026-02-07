using ICSharpCode.ILSpy.AIChat;
using System.Threading;

using NUnit.Framework;

using Shouldly;

namespace ICSharpCode.ILSpy.Tests.AIChat
{
	[TestFixture]
	[Apartment(ApartmentState.STA)]
	public class AiChatLinkReferenceParserTests
	{
		private readonly AiChatLinkReferenceParser parser = new();

		[Test]
		public void TryParse_LineParenthesisFormat_ReturnsSymbolReference()
		{
			var ok = parser.TryParse("T:ICSharpCode.ILSpy.AIChat.AiChatToolDispatcher (line 56)", out var reference);

			ok.ShouldBeTrue();
			reference.ShouldNotBeNull();
			reference!.Kind.ShouldBe(AiChatLinkReferenceKind.Symbol);
			reference.Target.ShouldBe("T:ICSharpCode.ILSpy.AIChat.AiChatToolDispatcher");
			reference.Line.ShouldBe(56);
			reference.Column.ShouldBeNull();
		}

		[Test]
		public void TryParse_ColonLineColumnFormat_ReturnsSymbolReference()
		{
			var ok = parser.TryParse("ICSharpCode.ILSpy.AIChat.AiChatToolDispatcher:56:9", out var reference);

			ok.ShouldBeTrue();
			reference.ShouldNotBeNull();
			reference!.Kind.ShouldBe(AiChatLinkReferenceKind.Symbol);
			reference.Target.ShouldBe("ICSharpCode.ILSpy.AIChat.AiChatToolDispatcher");
			reference.Line.ShouldBe(56);
			reference.Column.ShouldBe(9);
		}

		[Test]
		public void TryParse_LegacyFilePath_MapsToTypeSymbol()
		{
			var ok = parser.TryParse("ILSpy/AIChat/AiChatToolDispatcher.cs:56", out var reference);

			ok.ShouldBeTrue();
			reference.ShouldNotBeNull();
			reference!.Kind.ShouldBe(AiChatLinkReferenceKind.Symbol);
			reference.Target.ShouldBe("AiChatToolDispatcher");
			reference.Line.ShouldBe(56);
		}

		[Test]
		public void TryParse_ExternalLink_ReturnsExternalReference()
		{
			var ok = parser.TryParse("https://example.com/docs", out var reference);

			ok.ShouldBeTrue();
			reference.ShouldNotBeNull();
			reference!.Kind.ShouldBe(AiChatLinkReferenceKind.External);
			reference.Target.ShouldBe("https://example.com/docs");
		}

		[Test]
		public void FindMatches_FindsInlineSymbolLinks()
		{
			var text = "See T:ICSharpCode.ILSpy.AIChat.AiChatToolDispatcher (line 56) and M:Terraria.NPC.AI:120.";

			var matches = parser.FindMatches(text);

			matches.Count.ShouldBe(2);
			matches[0].Reference.Kind.ShouldBe(AiChatLinkReferenceKind.Symbol);
			matches[0].Reference.Line.ShouldBe(56);
			matches[1].Reference.Kind.ShouldBe(AiChatLinkReferenceKind.Symbol);
			matches[1].Reference.Line.ShouldBe(120);
		}

		[Test]
		public void TryParse_InvalidText_ReturnsFalse()
		{
			var ok = parser.TryParse("this is not a link", out var reference);

			ok.ShouldBeFalse();
			reference.ShouldBeNull();
		}
	}
}
