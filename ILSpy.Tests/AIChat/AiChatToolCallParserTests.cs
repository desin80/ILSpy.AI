using ICSharpCode.ILSpy.AIChat;

using NUnit.Framework;

using Shouldly;

namespace ICSharpCode.ILSpy.Tests.AIChat
{
	[TestFixture]
	public class AiChatToolCallParserTests
	{
		[Test]
		public void ParseSearchToolCall()
		{
			const string text = "<tool_call>{\"name\":\"search\",\"arguments\":{\"mode\":\"method\",\"term\":\"Find\"}}</tool_call>";

			var result = AiChatToolCallParser.Parse(text);

			result.ShouldNotBeNull();
			result.Name.ShouldBe("search");
			result.Mode.ShouldBe("method");
			result.Term.ShouldBe("Find");
			result.Index.ShouldBeNull();
		}

		[Test]
		public void ParseOpenResultToolCall()
		{
			const string text = "<tool_call>{\"name\":\"open_result\",\"arguments\":{\"index\":2}}</tool_call>";

			var result = AiChatToolCallParser.Parse(text);

			result.ShouldNotBeNull();
			result.Name.ShouldBe("open_result");
			result.Index.ShouldBe(2);
		}

		[Test]
		public void RemoveToolCallBlock()
		{
			const string text = "Before\n<tool_call>{\"name\":\"assemblies\"}</tool_call>\nAfter";

			var result = AiChatToolCallParser.RemoveToolCallBlock(text);

			result.ShouldBe("Before\n\nAfter");
		}
	}
}
