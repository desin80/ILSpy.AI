// Copyright (c) 2026 AlphaSierraPapa for the SharpDevelop Team
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy of this
// software and associated documentation files (the "Software"), to deal in the Software
// without restriction, including without limitation the rights to use, copy, modify, merge,
// publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons
// to whom the Software is furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in all copies or
// substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
// INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
// PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE
// FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
// OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.

using System.Text.Json;
using System.Text.RegularExpressions;

namespace ICSharpCode.ILSpy.AIChat
{
	#nullable enable

	public sealed class AiChatToolCall
	{
		public required string Name { get; init; }

		public string? Mode { get; init; }

		public string? Term { get; init; }

		public int? Index { get; init; }
	}

	public static class AiChatToolCallParser
	{
		private static readonly Regex ToolCallRegex = new("<tool_call>(.*?)</tool_call>", RegexOptions.Singleline | RegexOptions.Compiled | RegexOptions.IgnoreCase);

		public static AiChatToolCall? Parse(string text)
		{
			if (string.IsNullOrWhiteSpace(text))
			{
				return null;
			}

			var match = ToolCallRegex.Match(text);
			if (!match.Success)
			{
				return null;
			}

			var json = match.Groups[1].Value.Trim();
			if (string.IsNullOrWhiteSpace(json))
			{
				return null;
			}

			try
			{
				using var document = JsonDocument.Parse(json);
				var root = document.RootElement;
				if (!root.TryGetProperty("name", out var nameElement))
				{
					return null;
				}

				var name = nameElement.GetString();
				if (string.IsNullOrWhiteSpace(name))
				{
					return null;
				}

				string? mode = null;
				string? term = null;
				int? index = null;

				if (root.TryGetProperty("arguments", out var arguments) && arguments.ValueKind == JsonValueKind.Object)
				{
					if (arguments.TryGetProperty("mode", out var modeElement) && modeElement.ValueKind == JsonValueKind.String)
					{
						mode = modeElement.GetString();
					}

					if (arguments.TryGetProperty("term", out var termElement) && termElement.ValueKind == JsonValueKind.String)
					{
						term = termElement.GetString();
					}

					if (arguments.TryGetProperty("index", out var indexElement) && indexElement.ValueKind == JsonValueKind.Number && indexElement.TryGetInt32(out var parsed))
					{
						index = parsed;
					}
				}

				return new() {
					Name = name,
					Mode = mode,
					Term = term,
					Index = index,
				};
			}
			catch (JsonException)
			{
				return null;
			}
		}

		public static string RemoveToolCallBlock(string text)
		{
			if (string.IsNullOrWhiteSpace(text))
			{
				return string.Empty;
			}

			var stripped = ToolCallRegex.Replace(text, string.Empty);
			return stripped.Trim();
		}
	}
}
