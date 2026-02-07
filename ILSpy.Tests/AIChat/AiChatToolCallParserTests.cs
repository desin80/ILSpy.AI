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
			result.Arguments.ShouldNotBeNull();
		}

		[Test]
		public void ParseOpenResultToolCall()
		{
			const string text = "<tool_call>{\"name\":\"open_result\",\"arguments\":{\"index\":2}}</tool_call>";

			var result = AiChatToolCallParser.Parse(text);

			result.ShouldNotBeNull();
			result.Name.ShouldBe("open_result");
			result.Index.ShouldBe(2);
			result.Arguments.ShouldNotBeNull();
		}

		[Test]
		public void ParseReadResultWindowToolCall_WithIndexInArguments()
		{
			const string text = "<tool_call>{\"name\":\"read_result_window\",\"arguments\":{\"index\":5,\"window\":{\"startLine\":201,\"lineCount\":100}}}</tool_call>";

			var result = AiChatToolCallParser.Parse(text);

			result.ShouldNotBeNull();
			result.Name.ShouldBe("read_result_window");
			result.Index.ShouldBe(5);
			result.Arguments.ShouldNotBeNull();
			result.Arguments.Value.TryGetProperty("window", out _).ShouldBeTrue();
		}

		[Test]
		public void ParseToolCallWithoutArguments()
		{
			const string text = "<tool_call>{\"name\":\"assemblies\"}</tool_call>";

			var result = AiChatToolCallParser.Parse(text);

			result.ShouldNotBeNull();
			result.Name.ShouldBe("assemblies");
			result.Mode.ShouldBeNull();
			result.Term.ShouldBeNull();
			result.Index.ShouldBeNull();
			result.Arguments.ShouldBeNull();
		}

		[Test]
		public void RemoveToolCallBlock()
		{
			const string text = "Before\n<tool_call>{\"name\":\"assemblies\"}</tool_call>\nAfter";

			var result = AiChatToolCallParser.RemoveToolCallBlock(text);

			result.ShouldBe("Before\n\nAfter");
		}

		[Test]
		public void ParseToolCallWithStructuredArguments()
		{
			const string text = "<tool_call>{\"name\":\"decompile_many\",\"arguments\":{\"targets\":[{\"kind\":\"query\",\"mode\":\"type\",\"term\":\"String\"}],\"window\":{\"startLine\":101,\"lineCount\":120}}}</tool_call>";

			var result = AiChatToolCallParser.Parse(text);

			result.ShouldNotBeNull();
			result.Name.ShouldBe("decompile_many");
			result.Mode.ShouldBeNull();
			result.Term.ShouldBeNull();
			result.Index.ShouldBeNull();
			result.Arguments.ShouldNotBeNull();
			result.Arguments.Value.TryGetProperty("targets", out _).ShouldBeTrue();
			result.Arguments.Value.TryGetProperty("window", out _).ShouldBeTrue();
		}

		[Test]
		public void ParseToolCallWithArrayArguments_StoresRawArguments()
		{
			const string text = "<tool_call>{\"name\":\"bulk\",\"arguments\":[1,2,3]}</tool_call>";

			var result = AiChatToolCallParser.Parse(text);

			result.ShouldNotBeNull();
			result.Name.ShouldBe("bulk");
			result.Arguments.ShouldNotBeNull();
			result.Arguments.Value.ValueKind.ShouldBe(System.Text.Json.JsonValueKind.Array);
		}

		[Test]
		public void ParseMalformedJson_ReturnsNull()
		{
			const string text = "<tool_call>{\"name\":\"search\",\"arguments\":</tool_call>";

			var result = AiChatToolCallParser.Parse(text);

			result.ShouldBeNull();
		}

		[Test]
		public void ParseWithoutToolCallBlock_ReturnsNull()
		{
			const string text = "no tool call block here";

			var result = AiChatToolCallParser.Parse(text);

			result.ShouldBeNull();
		}

		[Test]
		public void RemoveToolCallBlock_RemovesMultipleBlocks()
		{
			const string text = "A\n<tool_call>{\"name\":\"selected\"}</tool_call>\nB\n<tool_call>{\"name\":\"decompile\"}</tool_call>\nC";

			var result = AiChatToolCallParser.RemoveToolCallBlock(text);

			result.ShouldBe("A\n\nB\n\nC");
		}
	}
}
