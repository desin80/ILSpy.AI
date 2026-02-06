using ICSharpCode.ILSpy.AIChat;

using NUnit.Framework;

using Shouldly;

namespace ICSharpCode.ILSpy.Tests.AIChat
{
	[TestFixture]
	public class AiChatCommandParserTests
	{
		[Test]
		public void ParseHelpCommand()
		{
			var result = AiChatParsedCommand.Parse("/help");

			result.Kind.ShouldBe(AiChatCommandKind.Help);
		}

		[Test]
		public void ParsePromptWithoutSlash()
		{
			var result = AiChatParsedCommand.Parse("explain selected method");

			result.Kind.ShouldBe(AiChatCommandKind.Prompt);
			result.Prompt.ShouldBe("explain selected method");
		}

		[Test]
		public void ParseAskCommand()
		{
			var result = AiChatParsedCommand.Parse("/ask summarize this class");

			result.Kind.ShouldBe(AiChatCommandKind.Ask);
			result.Prompt.ShouldBe("summarize this class");
		}

		[Test]
		public void ParseSearchDefaultMode()
		{
			var result = AiChatParsedCommand.Parse("/search myMethod");

			result.Kind.ShouldBe(AiChatCommandKind.Search);
			result.SearchMode.ShouldBe("member");
			result.SearchTerm.ShouldBe("myMethod");
		}

		[Test]
		public void ParseSearchExplicitMode()
		{
			var result = AiChatParsedCommand.Parse("/search method FindAll");

			result.Kind.ShouldBe(AiChatCommandKind.Search);
			result.SearchMode.ShouldBe("method");
			result.SearchTerm.ShouldBe("FindAll");
		}

		[Test]
		public void ParseUnknownCommand()
		{
			var result = AiChatParsedCommand.Parse("/foobar abc");

			result.Kind.ShouldBe(AiChatCommandKind.Unknown);
			result.UnknownCommandName.ShouldBe("foobar");
		}
	}
}
