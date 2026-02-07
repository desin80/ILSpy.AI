using System.Collections.Generic;

using ICSharpCode.ILSpy.AIChat;

using NUnit.Framework;

namespace ICSharpCode.ILSpy.Tests.AIChat
{
	[TestFixture]
	public class AiChatToolSpecTests
	{
		[Test]
		public void ToolSpec_ContainsBatchAndWindowTools()
		{
			var dispatcher = new AiChatToolDispatcher(null!, null!, null!, null!);
			var spec = dispatcher.GetToolSpecText();

			Assert.That(spec, Does.Contain("- assemblies()"));
			Assert.That(spec, Does.Contain("- selected()"));
			Assert.That(spec, Does.Contain("- decompile()"));
			Assert.That(spec, Does.Contain("- read_selected_window(window?)"));
			Assert.That(spec, Does.Contain("- search_many(queries, maxResultsPerQuery?)"));
			Assert.That(spec, Does.Contain("- decompile_many(targets, window?)"));
			Assert.That(spec, Does.Contain("- read_result_window(index, window?)"));
			Assert.That(spec, Does.Contain("- continue_output(continue_token)"));
			Assert.That(spec, Does.Contain("take"));
			Assert.That(spec, Does.Contain("window ="));
			Assert.That(spec, Does.Contain("maxLines"));
			Assert.That(spec, Does.Contain("maxChars"));
			Assert.That(spec, Does.Contain("continue_token"));
			Assert.That(spec, Does.Contain("1200"));
			Assert.That(spec, Does.Contain("60000"));
		}

		[Test]
		public void ToolSpec_DoesNotContainDuplicateToolEntries()
		{
			var dispatcher = new AiChatToolDispatcher(null!, null!, null!, null!);
			var spec = dispatcher.GetToolSpecText();

			var expectedTools = new[] {
				"assemblies()",
				"selected()",
				"decompile()",
				"read_selected_window(window?)",
				"search(mode, term)",
				"search_many(queries, maxResultsPerQuery?)",
				"decompile_many(targets, window?)",
				"read_result_window(index, window?)",
				"continue_output(continue_token)",
				"analyze()",
				"open_result(index)",
			};

			var duplicates = new List<string>();
			foreach (var tool in expectedTools)
			{
				var marker = $"- {tool}";
				var first = spec.IndexOf(marker, System.StringComparison.Ordinal);
				if (first < 0)
				{
					duplicates.Add($"missing:{tool}");
					continue;
				}

				var second = spec.IndexOf(marker, first + marker.Length, System.StringComparison.Ordinal);
				if (second >= 0)
				{
					duplicates.Add($"duplicate:{tool}");
				}
			}

			Assert.That(duplicates, Is.Empty, string.Join(",", duplicates));
		}
	}
}
