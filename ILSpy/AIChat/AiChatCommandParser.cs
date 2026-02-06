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

using System;
using System.Linq;

using ICSharpCode.ILSpy.AppEnv;

namespace ICSharpCode.ILSpy.AIChat
{
	#nullable enable

	public enum AiChatCommandKind
	{
		Prompt,
		Ask,
		Help,
		Assemblies,
		Selected,
		Decompile,
		Search,
		Analyze,
		Provider,
		Unknown,
	}

	public sealed class AiChatParsedCommand
	{
		public required AiChatCommandKind Kind { get; init; }

		public string? Prompt { get; init; }

		public string? SearchMode { get; init; }

		public string? SearchTerm { get; init; }

		public string? UnknownCommandName { get; init; }

		public static AiChatParsedCommand Parse(string? input)
		{
			if (string.IsNullOrWhiteSpace(input))
			{
				return new() { Kind = AiChatCommandKind.Help };
			}

			var trimmed = input.Trim();
			if (!trimmed.StartsWith("/", StringComparison.Ordinal))
			{
				return new() {
					Kind = AiChatCommandKind.Prompt,
					Prompt = trimmed,
				};
			}

			string[] parts;
			try
			{
				parts = CommandLineTools.CommandLineToArgumentArray(trimmed.Substring(1));
			}
			catch (Exception)
			{
				return new() {
					Kind = AiChatCommandKind.Prompt,
					Prompt = trimmed,
				};
			}

			if (parts.Length == 0)
			{
				return new() { Kind = AiChatCommandKind.Help };
			}

			var command = parts[0].Trim().ToLowerInvariant();

			return command switch
			{
				"help" or "h" or "?" => new() { Kind = AiChatCommandKind.Help },
				"assemblies" or "ls" => new() { Kind = AiChatCommandKind.Assemblies },
				"selected" or "sel" => new() { Kind = AiChatCommandKind.Selected },
				"decompile" or "dec" => new() { Kind = AiChatCommandKind.Decompile },
				"analyze" or "ana" => new() { Kind = AiChatCommandKind.Analyze },
				"provider" or "config" => new() { Kind = AiChatCommandKind.Provider },
				"ask" => new() {
					Kind = AiChatCommandKind.Ask,
					Prompt = string.Join(" ", parts.Skip(1)).Trim(),
				},
				"search" or "find" => ParseSearch(parts),
				_ => new() {
					Kind = AiChatCommandKind.Unknown,
					UnknownCommandName = command,
				}
			};
		}

		private static AiChatParsedCommand ParseSearch(string[] parts)
		{
			if (parts.Length == 1)
			{
				return new() {
					Kind = AiChatCommandKind.Search,
					SearchMode = "member",
					SearchTerm = string.Empty,
				};
			}

			if (parts.Length == 2)
			{
				return new() {
					Kind = AiChatCommandKind.Search,
					SearchMode = "member",
					SearchTerm = parts[1],
				};
			}

			return new() {
				Kind = AiChatCommandKind.Search,
				SearchMode = parts[1],
				SearchTerm = string.Join(" ", parts.Skip(2)),
			};
		}
	}
}
