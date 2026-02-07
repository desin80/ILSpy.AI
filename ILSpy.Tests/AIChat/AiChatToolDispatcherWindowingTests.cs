using System.Linq;

using ICSharpCode.ILSpy.AIChat;

using NUnit.Framework;

using Shouldly;

namespace ICSharpCode.ILSpy.Tests.AIChat
{
	[TestFixture]
	public class AiChatToolDispatcherWindowingTests
	{
		[Test]
		public void SliceTextWindow_ReturnsExpectedFirstWindow()
		{
			var text = string.Join("\n", new[] { "L1", "L2", "L3", "L4", "L5" });

			var slice = AiChatToolDispatcher.SliceTextWindow(text, startLine: 1, lineCount: 2);

			slice.StartLine.ShouldBe(1);
			slice.ReturnedLineCount.ShouldBe(2);
			slice.TotalLines.ShouldBe(5);
			slice.HasMore.ShouldBeTrue();
			slice.NextStartLine.ShouldBe(3);
			slice.Text.ShouldBe("L1" + System.Environment.NewLine + "L2");
		}

		[Test]
		public void SliceTextWindow_NormalizesInvalidStartAndCount()
		{
			var text = string.Join("\n", new[] { "A", "B", "C" });

			var slice = AiChatToolDispatcher.SliceTextWindow(text, startLine: -10, lineCount: 0);

			slice.StartLine.ShouldBe(1);
			slice.ReturnedLineCount.ShouldBe(3);
			slice.TotalLines.ShouldBe(3);
			slice.HasMore.ShouldBeFalse();
			slice.NextStartLine.ShouldBe(-1);
		}

		[Test]
		public void SliceTextWindow_ReturnsEmptyWhenStartBeyondEnd()
		{
			var text = string.Join("\n", new[] { "X", "Y" });

			var slice = AiChatToolDispatcher.SliceTextWindow(text, startLine: 10, lineCount: 5);

			slice.StartLine.ShouldBe(10);
			slice.ReturnedLineCount.ShouldBe(0);
			slice.TotalLines.ShouldBe(2);
			slice.HasMore.ShouldBeFalse();
			slice.NextStartLine.ShouldBe(-1);
			slice.Text.ShouldBe(string.Empty);
		}

		[Test]
		public void SliceTextWindow_OnExactTailWindow_HasNoMore()
		{
			var text = string.Join("\n", new[] { "L1", "L2", "L3", "L4" });

			var slice = AiChatToolDispatcher.SliceTextWindow(text, startLine: 3, lineCount: 2);

			slice.StartLine.ShouldBe(3);
			slice.ReturnedLineCount.ShouldBe(2);
			slice.TotalLines.ShouldBe(4);
			slice.HasMore.ShouldBeFalse();
			slice.NextStartLine.ShouldBe(-1);
			slice.Text.ShouldBe("L3" + System.Environment.NewLine + "L4");
		}

		[Test]
		public void SliceTextWindow_EmptyInput_ReturnsEmptySlice()
		{
			var slice = AiChatToolDispatcher.SliceTextWindow(string.Empty, startLine: 1, lineCount: 100);

			slice.StartLine.ShouldBe(1);
			slice.ReturnedLineCount.ShouldBe(0);
			slice.TotalLines.ShouldBe(0);
			slice.HasMore.ShouldBeFalse();
			slice.NextStartLine.ShouldBe(-1);
			slice.Text.ShouldBe(string.Empty);
		}

		[Test]
		public void SliceTextWindow_LineCountLargerThanRemaining_ReturnsRemainingOnly()
		{
			var text = string.Join("\n", new[] { "A", "B", "C", "D" });

			var slice = AiChatToolDispatcher.SliceTextWindow(text, startLine: 2, lineCount: 999);

			slice.StartLine.ShouldBe(2);
			slice.ReturnedLineCount.ShouldBe(3);
			slice.TotalLines.ShouldBe(4);
			slice.HasMore.ShouldBeFalse();
			slice.NextStartLine.ShouldBe(-1);
			slice.Text.ShouldBe("B" + System.Environment.NewLine + "C" + System.Environment.NewLine + "D");
		}

		[Test]
		public void SliceTextWindow_LargeContent_CanBeReadContinuouslyByWindow()
		{
			const int total = 10000;
			var text = string.Join("\n", Enumerable.Range(1, total).Select(i => "L" + i));

			var start = 1;
			var visited = 0;
			var safety = 0;
			while (true)
			{
				safety++;
				safety.ShouldBeLessThan(1000);

				var slice = AiChatToolDispatcher.SliceTextWindow(text, startLine: start, lineCount: 500);
				visited += slice.ReturnedLineCount;

				if (!slice.HasMore)
				{
					break;
				}

				start = slice.NextStartLine;
			}

			visited.ShouldBe(total);
		}

		[Test]
		public void NormalizeDecompileManyQueryTake_UsesDefaultWhenMissing()
		{
			AiChatToolDispatcher.NormalizeDecompileManyQueryTake(null).ShouldBe(3);
		}

		[Test]
		public void NormalizeDecompileManyQueryTake_ClampsToValidRange()
		{
			AiChatToolDispatcher.NormalizeDecompileManyQueryTake(0).ShouldBe(1);
			AiChatToolDispatcher.NormalizeDecompileManyQueryTake(99).ShouldBe(8);
			AiChatToolDispatcher.NormalizeDecompileManyQueryTake(5).ShouldBe(5);
		}
	}
}
