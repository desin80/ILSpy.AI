using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace ICSharpCode.ILSpy.AIChat
{
	#nullable enable

	internal enum AiChatLinkReferenceKind
	{
		Symbol,
		External,
	}

	internal sealed record AiChatLinkReference(AiChatLinkReferenceKind Kind, string Target, int? Line, int? Column)
	{
		public static AiChatLinkReference CreateExternal(string url)
		{
			return new AiChatLinkReference(AiChatLinkReferenceKind.External, url, null, null);
		}

		public static AiChatLinkReference CreateSymbol(string symbol, int? line = null, int? column = null)
		{
			return new AiChatLinkReference(AiChatLinkReferenceKind.Symbol, symbol, line, column);
		}
	}

	internal readonly record struct AiChatLinkReferenceMatch(int Start, int Length, AiChatLinkReference Reference);

	internal sealed partial class AiChatLinkReferenceParser
	{
		private static readonly Regex InlineReferenceRegex = BuildInlineReferenceRegex();

		private static readonly Regex IdStringRegex = new(
			@"^(?<symbol>[MTPFE]:[^\s]+)$",
			RegexOptions.Compiled | RegexOptions.IgnoreCase);

		private static readonly Regex AssemblyQualifiedIdStringRegex = new(
			@"^(?<symbol>[A-Za-z0-9_.-]+::[MTPFE]:[^\s]+)$",
			RegexOptions.Compiled | RegexOptions.IgnoreCase);

		private static readonly Regex SymbolRegex = new(
			@"^(?<symbol>(?:[A-Za-z0-9_.-]+::)?[A-Za-z_][A-Za-z0-9_`]*(?:[./\\+][A-Za-z_][A-Za-z0-9_`+]*)+(?:\([^\r\n)]{0,240}\))?)$",
			RegexOptions.Compiled);

		private static readonly Regex SymbolWithLineRegex = new(
			@"^(?<symbol>(?:[A-Za-z0-9_.-]+::)?(?:[MTPFE]:[^\s]+|[A-Za-z_][A-Za-z0-9_`]*(?:[./\\+][A-Za-z_][A-Za-z0-9_`+]*)+(?:\([^\r\n)]{0,240}\))?))\s*\(\s*line\s*(?<line>\d+)\s*\)$",
			RegexOptions.Compiled | RegexOptions.IgnoreCase);

		private static readonly Regex SymbolWithColonLineRegex = new(
			@"^(?<symbol>(?:[A-Za-z0-9_.-]+::)?(?:[MTPFE]:.+?|.+?)):(?<line>\d+)(?::(?<column>\d+))?$",
			RegexOptions.Compiled);

		public bool TryParse(string? text, out AiChatLinkReference? reference)
		{
			reference = null;
			if (string.IsNullOrWhiteSpace(text))
				return false;

			var trimmed = text.Trim();
			trimmed = TrimWrapper(trimmed);
			if (string.IsNullOrWhiteSpace(trimmed))
				return false;

			if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
				&& (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
					|| uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
			{
				reference = AiChatLinkReference.CreateExternal(trimmed);
				return true;
			}

			if (TryParseWithRegex(SymbolWithLineRegex, trimmed, out reference)
				|| TryParseWithRegex(SymbolWithColonLineRegex, trimmed, out reference)
				|| TryParseWithRegex(AssemblyQualifiedIdStringRegex, trimmed, out reference)
				|| TryParseWithRegex(IdStringRegex, trimmed, out reference)
				|| TryParseWithRegex(SymbolRegex, trimmed, out reference)
				|| TryParseLegacyFileAsSymbol(trimmed, out reference))
			{
				return true;
			}

			return false;
		}

		public IReadOnlyList<AiChatLinkReferenceMatch> FindMatches(string text)
		{
			if (string.IsNullOrEmpty(text))
				return [];

			var matches = InlineReferenceRegex.Matches(text);
			if (matches.Count == 0)
				return [];

			var result = new List<AiChatLinkReferenceMatch>(matches.Count);
			foreach (Match match in matches)
			{
				if (!match.Success || match.Length == 0)
					continue;

				if (!TryNormalizeCandidate(match.Value, out var normalized, out var leadingTrim))
					continue;

				if (!TryParse(normalized, out var reference) || reference == null)
					continue;

				result.Add(new AiChatLinkReferenceMatch(match.Index + leadingTrim, normalized.Length, reference));
			}

			return result;
		}

		private static bool TryNormalizeCandidate(string raw, out string normalized, out int leadingTrim)
		{
			normalized = string.Empty;
			leadingTrim = 0;
			if (string.IsNullOrWhiteSpace(raw))
				return false;

			var start = 0;
			var end = raw.Length - 1;

			while (start <= end && IsLeadingWrapper(raw[start]))
			{
				start++;
			}

			while (end >= start && IsTrailingWrapper(raw[end]))
			{
				end--;
			}

			if (end < start)
				return false;

			normalized = raw.Substring(start, end - start + 1);
			leadingTrim = start;
			return true;
		}

		private static bool TryParseWithRegex(Regex regex, string text, out AiChatLinkReference? reference)
		{
			reference = null;
			var match = regex.Match(text);
			if (!match.Success)
				return false;

			var symbol = match.Groups["symbol"].Value.Trim();
			if (string.IsNullOrWhiteSpace(symbol))
				return false;

			if (regex == SymbolWithColonLineRegex)
			{
				symbol = symbol.TrimEnd();
				if (!LooksLikeSymbolOrId(symbol))
				{
					return false;
				}
			}

			int? line = null;
			if (match.Groups["line"].Success)
			{
				if (!int.TryParse(match.Groups["line"].Value, out var parsedLine) || parsedLine <= 0)
					return false;

				line = parsedLine;
			}

			int? column = null;
			if (match.Groups["column"].Success && int.TryParse(match.Groups["column"].Value, out var parsedColumn) && parsedColumn > 0)
			{
				column = parsedColumn;
			}

			symbol = NormalizeLegacyFileStyleSymbol(symbol);

			reference = AiChatLinkReference.CreateSymbol(symbol, line, column);
			return true;
		}

		private static string NormalizeLegacyFileStyleSymbol(string symbol)
		{
			if (!symbol.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
				&& !symbol.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
			{
				return symbol;
			}

			var fileName = Path.GetFileNameWithoutExtension(symbol.Replace('\\', '/'));
			if (!string.IsNullOrWhiteSpace(fileName) && IsLikelyTypeName(fileName))
			{
				return fileName;
			}

			return symbol;
		}

		private static bool TryParseLegacyFileAsSymbol(string text, out AiChatLinkReference? reference)
		{
			reference = null;
			if (!text.Contains(".cs", StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}

			var line = default(int?);
			var column = default(int?);
			var symbolText = text;

			var colonMatch = SymbolWithColonLineRegex.Match(text);
			if (colonMatch.Success)
			{
				symbolText = colonMatch.Groups["symbol"].Value.Trim();
				if (int.TryParse(colonMatch.Groups["line"].Value, out var parsedLine) && parsedLine > 0)
				{
					line = parsedLine;
				}
				if (int.TryParse(colonMatch.Groups["column"].Value, out var parsedColumn) && parsedColumn > 0)
				{
					column = parsedColumn;
				}
			}

			var fileName = Path.GetFileNameWithoutExtension(symbolText.Replace('\\', '/'));
			if (string.IsNullOrWhiteSpace(fileName) || !IsLikelyTypeName(fileName))
			{
				return false;
			}

			reference = AiChatLinkReference.CreateSymbol(fileName, line, column);
			return true;
		}

		private static bool LooksLikeSymbolOrId(string text)
		{
			if (string.IsNullOrWhiteSpace(text))
			{
				return false;
			}

			return IdStringRegex.IsMatch(text)
				|| AssemblyQualifiedIdStringRegex.IsMatch(text)
				|| SymbolRegex.IsMatch(text)
				|| IsLikelyTypeName(text);
		}

		private static bool IsLikelyTypeName(string text)
		{
			if (string.IsNullOrWhiteSpace(text))
			{
				return false;
			}

			if (!char.IsLetter(text[0]) && text[0] != '_')
			{
				return false;
			}

			return text.All(ch => char.IsLetterOrDigit(ch) || ch is '_' or '`' or '+' or '.');
		}

		private static string TrimWrapper(string value)
		{
			var trimmed = value.Trim();
			if (trimmed.Length >= 2)
			{
				var first = trimmed[0];
				var last = trimmed[^1];
				if ((first == '`' && last == '`')
					|| (first == '"' && last == '"')
					|| (first == '\'' && last == '\''))
				{
					trimmed = trimmed[1..^1].Trim();
				}
			}

			return trimmed;
		}

		private static bool IsLeadingWrapper(char ch)
		{
			return ch is '`' or '(' or '[' or '{' or '<' or '"' or '\'';
		}

		private static bool IsTrailingWrapper(char ch)
		{
			return ch is '`' or ')' or ']' or '}' or '>' or '"' or '\'' or '.' or ',' or ';';
		}

		[GeneratedRegex(@"https?://[^\s<>()]+|(?:[A-Za-z0-9_.-]+::)?[MTPFE]:[^\s<>()`]+(?::\d+(?::\d+)?)?(?:\s*\(\s*line\s*\d+\s*\))?|(?:[A-Za-z_][A-Za-z0-9_`]*(?:[./\\+][A-Za-z_][A-Za-z0-9_`+]*)+)(?:\([^\r\n)]{0,240}\))?(?::\d+(?::\d+)?|\s*\(\s*line\s*\d+\s*\))?", RegexOptions.IgnoreCase)]
		private static partial Regex BuildInlineReferenceRegex();
	}
}
