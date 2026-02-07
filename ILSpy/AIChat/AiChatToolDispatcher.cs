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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Composition;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;

using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.TypeSystem;
using ICSharpCode.ILSpy.Analyzers;
using ICSharpCode.ILSpy.AssemblyTree;
using ICSharpCode.ILSpy.Docking;
using ICSharpCode.ILSpy.TreeNodes;
using ICSharpCode.ILSpy.Util;
using ICSharpCode.ILSpy.ViewModels;
using ICSharpCode.ILSpyX.Search;

namespace ICSharpCode.ILSpy.AIChat
{
	#nullable enable

	[Export]
	[Shared]
	public sealed class AiChatToolDispatcher
	{
		private const int DefaultWindowStartLine = 1;
		private const int DefaultWindowLineCount = 500;
		private const int DefaultChunkMaxLines = 1200;
		private const int DefaultChunkMaxChars = 60000;
		private const int MaxContinuationStates = 256;
		private const int SearchMaxResults = 30;
		private const int SearchManyMaxConcurrency = 3;
		private const int DecompileManyMaxConcurrency = 2;
		private const int DecompileManyDefaultQueryTake = 3;
		private const int DecompileManyMaxQueryTake = 8;
		private const int MaxDecompileCacheEntries = 16;
		private const int MaxDecompileCacheTotalChars = 8_000_000;
		private const int SmallTextAutoFullReadLineThreshold = 500;

		private static readonly HashSet<string> ChunkedTools = new(StringComparer.OrdinalIgnoreCase) {
			"assemblies",
			"selected",
			"decompile",
			"read_selected_window",
			"search",
			"search_many",
			"decompile_many",
			"read_result_window",
		};

		private readonly AssemblyTreeModel assemblyTreeModel;
		private readonly AnalyzerTreeViewModel analyzerTreeViewModel;
		private readonly DockWorkspace dockWorkspace;
		private readonly AiChatSearchService searchService;
		private readonly ConcurrentDictionary<string, ContinuationState> continuationStates = new(StringComparer.Ordinal);
		private readonly object decompileCacheLock = new();
		private readonly Dictionary<string, DecompileTextCacheEntry> decompileTextCache = new(StringComparer.Ordinal);

		public AiChatToolDispatcher(AssemblyTreeModel assemblyTreeModel, AnalyzerTreeViewModel analyzerTreeViewModel, DockWorkspace dockWorkspace, AiChatSearchService searchService)
		{
			this.assemblyTreeModel = assemblyTreeModel;
			this.analyzerTreeViewModel = analyzerTreeViewModel;
			this.dockWorkspace = dockWorkspace;
			this.searchService = searchService;
		}

		public string GetToolSpecText()
		{
			return string.Join(Environment.NewLine,
				"Available tools:",
				"- assemblies()",
				"- selected()",
				"- decompile()",
				"- read_selected_window(window?)",
				"- search(mode, term)",
				"- search_many(queries, maxResultsPerQuery?)",
				"- decompile_many(targets, window?)",
				"- read_result_window(index, window?)",
				"- continue_output(continue_token)",
				"- analyze()",
				"- open_result(index)",
				"Notes:",
				"- window = {\"startLine\":1,\"lineCount\":500}",
				"- output budget args (optional on text-heavy tools): {\"maxLines\":1200,\"maxChars\":60000}",
				"- if output is truncated, use continue_output with continue_token",
				"- search_many queries = [{\"mode\":\"type\",\"term\":\"PromoItemChecker\"}]",
				"- decompile_many targets = [{\"kind\":\"search_index\",\"index\":1}] or [{\"kind\":\"query\",\"mode\":\"type\",\"term\":\"PromoItemChecker\"}]",
				"- query targets in decompile_many may include take (default 3, max 8) to auto-decompile first matching candidates",
				"Tool call format:",
				"<tool_call>",
				"{\"name\":\"search\",\"arguments\":{\"mode\":\"method\",\"term\":\"Find\"}}",
				"</tool_call>");
		}

		public Task<AiChatToolResult> ExecuteAsync(string toolName, string? mode, string? term, int? index, CancellationToken cancellationToken)
		{
			return ExecuteAsync(toolName, mode, term, index, null, cancellationToken);
		}

		public async Task<AiChatToolResult> ExecuteAsync(string toolName, string? mode, string? term, int? index, JsonElement? arguments, CancellationToken cancellationToken)
		{
			var stopwatch = Stopwatch.StartNew();
			AiChatLog.Info($"tool start name={toolName} mode={mode ?? "<null>"} termLength={(term?.Length ?? 0)} index={(index?.ToString() ?? "<null>")}");
			try
			{
				AiChatToolResult result;
				if (string.Equals(toolName, "continue_output", StringComparison.OrdinalIgnoreCase))
				{
					result = new() { Success = true, Output = ContinueOutput(arguments) };
					return result;
				}

				switch (toolName)
				{
					case "assemblies":
						result = new() { Success = true, Output = await ListAssembliesAsync(cancellationToken) };
						break;
					case "selected":
						result = new() { Success = true, Output = GetSelectedText() };
						break;
					case "decompile":
						result = new() { Success = true, Output = await Task.Run(() => DecompileSelectedNode(cancellationToken), cancellationToken) };
						break;
					case "read_selected_window":
						result = new() { Success = true, Output = await Task.Run(() => ReadSelectedWindow(arguments, cancellationToken), cancellationToken) };
						break;
					case "search":
						result = new() { Success = true, Output = await SearchAsync(mode ?? "member", term ?? string.Empty, cancellationToken) };
						break;
					case "search_many":
						result = new() { Success = true, Output = await SearchManyAsync(arguments, cancellationToken) };
						break;
					case "decompile_many":
						result = new() { Success = true, Output = await DecompileManyAsync(arguments, cancellationToken) };
						break;
					case "read_result_window":
						result = new() { Success = true, Output = await Task.Run(() => ReadResultWindow(index, arguments, cancellationToken), cancellationToken) };
						break;
					case "analyze":
						result = new() { Success = true, Output = AnalyzeSelected() };
						break;
					case "open_result":
						result = new() { Success = true, Output = OpenSearchResult(index) };
						break;
					default:
						result = new() { Success = false, Output = $"Unknown tool '{toolName}'." };
						break;
				}

				if (result.Success)
				{
					result = new() {
						Success = true,
						Output = ApplyOutputBudgetIfRequested(toolName, result.Output, arguments),
					};
				}

				AiChatLog.Info($"tool finish name={toolName} success={result.Success} outputLength={(result.Output?.Length ?? 0)} elapsedMs={stopwatch.ElapsedMilliseconds}");

				return result;
			}
			catch (Exception ex)
			{
				AiChatLog.Error(ex, $"tool failed name={toolName} elapsedMs={stopwatch.ElapsedMilliseconds}");
				return new() { Success = false, Output = ex.Message };
			}
			finally
			{
				TrimContinuationStates();
				TrimDecompileTextCache();
			}
		}

		private SearchResult[] lastSearchResults = [];

		private string ApplyOutputBudgetIfRequested(string toolName, string output, JsonElement? arguments)
		{
			if (!ChunkedTools.Contains(toolName))
			{
				return output;
			}

			var budget = ParseChunkBudget(arguments);
			var chunk = SliceChunk(output, 0, budget.MaxLines, budget.MaxChars);
			if (!chunk.HasMore)
			{
				return output;
			}

			var token = CreateContinuationToken(output, chunk.NextLineStart, budget);
			var builder = new StringBuilder();
			builder.Append(chunk.Content.TrimEnd());
			builder.AppendLine();
			builder.AppendLine();
			builder.AppendLine($"... [truncated by budget: maxLines={budget.MaxLines}, maxChars={budget.MaxChars}]");
			builder.AppendLine($"continue_token: {token}");
			builder.AppendLine("next: use continue_output({\"continue_token\":\"<token>\"})");
			return builder.ToString().TrimEnd();
		}

		private string ContinueOutput(JsonElement? arguments)
		{
			if (!TryGetContinueToken(arguments, out var token, out var tokenError))
			{
				return tokenError;
			}

			if (!continuationStates.TryGetValue(token, out var state))
			{
				return "Invalid or expired continue_token.";
			}

			state.LastAccessUtc = DateTime.UtcNow;
			var chunk = SliceChunk(state.FullText, state.NextLineStart, state.Budget.MaxLines, state.Budget.MaxChars);
			state.NextLineStart = chunk.NextLineStart;
			if (!chunk.HasMore)
			{
				continuationStates.TryRemove(token, out _);
			}

			var builder = new StringBuilder();
			builder.Append(chunk.Content.TrimEnd());
			if (chunk.HasMore)
			{
				builder.AppendLine();
				builder.AppendLine();
				builder.AppendLine($"... [truncated by budget: maxLines={state.Budget.MaxLines}, maxChars={state.Budget.MaxChars}]");
				builder.AppendLine($"continue_token: {token}");
				builder.AppendLine("next: use continue_output({\"continue_token\":\"<token>\"})");
			}

			return builder.ToString().TrimEnd();
		}

		private string CreateContinuationToken(string fullText, int nextLineStart, ChunkBudget budget)
		{
			var token = $"ct_{Guid.NewGuid():N}";
			continuationStates[token] = new ContinuationState(fullText, nextLineStart, budget);
			return token;
		}

		private static bool TryGetContinueToken(JsonElement? arguments, out string token, out string error)
		{
			token = string.Empty;
			error = string.Empty;

			if (arguments is not { ValueKind: JsonValueKind.Object } argumentObject)
			{
				error = "continue_output requires {\"continue_token\":\"...\"}.";
				return false;
			}

			if (!argumentObject.TryGetProperty("continue_token", out var tokenElement) || tokenElement.ValueKind != JsonValueKind.String)
			{
				error = "continue_output requires string continue_token.";
				return false;
			}

			token = tokenElement.GetString() ?? string.Empty;
			if (string.IsNullOrWhiteSpace(token))
			{
				error = "continue_token must not be empty.";
				return false;
			}

			return true;
		}

		private static ChunkBudget ParseChunkBudget(JsonElement? arguments)
		{
			var maxLines = DefaultChunkMaxLines;
			var maxChars = DefaultChunkMaxChars;

			if (arguments is { ValueKind: JsonValueKind.Object } argumentObject)
			{
				if (argumentObject.TryGetProperty("maxLines", out var maxLinesElement)
					&& maxLinesElement.ValueKind == JsonValueKind.Number
					&& maxLinesElement.TryGetInt32(out var parsedLines))
				{
					maxLines = Math.Max(20, Math.Min(10000, parsedLines));
				}

				if (argumentObject.TryGetProperty("maxChars", out var maxCharsElement)
					&& maxCharsElement.ValueKind == JsonValueKind.Number
					&& maxCharsElement.TryGetInt32(out var parsedChars))
				{
					maxChars = Math.Max(1000, Math.Min(500000, parsedChars));
				}
			}

			return new ChunkBudget(maxLines, maxChars);
		}

		private static ChunkSlice SliceChunk(string text, int startLineIndex, int maxLines, int maxChars)
		{
			var safeText = text ?? string.Empty;
			var lines = safeText.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
			if (lines.Length == 1 && lines[0].Length == 0)
			{
				return new ChunkSlice(string.Empty, false, 0);
			}

			var start = Math.Max(0, startLineIndex);
			if (start >= lines.Length)
			{
				return new ChunkSlice(string.Empty, false, lines.Length);
			}

			var builder = new StringBuilder();
			var count = 0;
			var cursor = start;
			for (; cursor < lines.Length && count < maxLines; cursor++)
			{
				var line = lines[cursor];
				var segment = count == 0 ? line : Environment.NewLine + line;
				if (builder.Length + segment.Length > maxChars)
				{
					break;
				}

				builder.Append(segment);
				count++;
			}

			if (count == 0 && cursor < lines.Length)
			{
				var firstLine = lines[cursor];
				var take = Math.Min(maxChars, firstLine.Length);
				builder.Append(firstLine[..take]);
				cursor++;
			}

			var hasMore = cursor < lines.Length;
			return new ChunkSlice(builder.ToString(), hasMore, cursor);
		}

		private void TrimContinuationStates()
		{
			if (continuationStates.Count <= MaxContinuationStates)
			{
				return;
			}

			var stale = continuationStates
				.OrderBy(pair => pair.Value.LastAccessUtc)
				.Take(continuationStates.Count - MaxContinuationStates)
				.Select(pair => pair.Key)
				.ToArray();

			foreach (var token in stale)
			{
				continuationStates.TryRemove(token, out _);
			}
		}

		private async Task<string> ListAssembliesAsync(CancellationToken cancellationToken)
		{
			var assemblies = await assemblyTreeModel.AssemblyList.GetAllAssemblies();
			if (assemblies.Count == 0)
			{
				return "No assemblies loaded.";
			}

			return string.Join(Environment.NewLine,
				assemblies.Select((assembly, i) => $"{i + 1}. {assembly.ShortName} ({assembly.FileName}){(assembly.HasLoadError ? " [load error]" : string.Empty)}"));
		}

		private string GetSelectedText()
		{
			var nodes = assemblyTreeModel.SelectedNodes.ToArray();
			if (nodes.Length == 0)
			{
				return "No selection in assembly tree.";
			}

			return string.Join(Environment.NewLine, nodes.Select((node, i) => $"{i + 1}. {node.Text} [{node.GetType().Name}]"));
		}

		private string DecompileSelectedNode(CancellationToken cancellationToken)
		{
			const int maxLength = 12_000;
			var text = DecompileSelectedNodeText(cancellationToken);
			if (text.StartsWith("No selected node to decompile.", StringComparison.Ordinal))
			{
				return text;
			}

			if (text.Length > maxLength)
			{
				text = text.Substring(0, maxLength) + Environment.NewLine + "... [truncated]";
			}

			return text;
		}

		private string DecompileSelectedNodeText(CancellationToken cancellationToken)
		{
			var node = assemblyTreeModel.SelectedNodes.FirstOrDefault();
			if (node == null)
			{
				return "No selected node to decompile.";
			}

			if (node is IMemberTreeNode { Member: IEntity selectedEntity })
			{
				return DecompileEntity(selectedEntity, cancellationToken);
			}

			var output = new PlainTextOutput();
			var options = dockWorkspace.ActiveTabPage.CreateDecompilationOptions();
			options.FullDecompilation = false;
			options.CancellationToken = cancellationToken;
			node.Decompile(assemblyTreeModel.CurrentLanguage, output, options);
			return output.ToString();
		}

		private async Task<string> SearchAsync(string mode, string term, CancellationToken cancellationToken)
		{
			if (string.IsNullOrWhiteSpace(term))
			{
				lastSearchResults = [];
				return "Usage: search(mode, term).";
			}

			lastSearchResults = await SearchCoreAsync(mode, term, SearchMaxResults, cancellationToken);
			if (lastSearchResults.Length == 0)
			{
				return $"No results for '{term}' in mode '{mode}'.";
			}

			return string.Join(Environment.NewLine, lastSearchResults.Select((result, i) => $"{i + 1}. {result.Name} | {result.Location} | {result.Assembly}"));
		}

		private async Task<SearchResult[]> SearchCoreAsync(string mode, string term, int maxResults, CancellationToken cancellationToken)
		{
			var languageVersion = assemblyTreeModel.CurrentLanguageVersion;
			if (languageVersion == null)
			{
				return [];
			}

			var requestedTerm = term?.Trim() ?? string.Empty;
			var normalizedQualifiedTerm = NormalizeQualifiedSearchTerm(requestedTerm);
			var isQualifiedQuery = LooksLikeQualifiedQuery(normalizedQualifiedTerm);

			var results = await searchService.SearchAsync(
				assemblyTreeModel.AssemblyList,
				assemblyTreeModel.CurrentLanguage,
				languageVersion,
				mode,
				requestedTerm,
				maxResults,
				cancellationToken);

			if (!isQualifiedQuery)
			{
				return results.ToArray();
			}

			var strictMatches = FilterQualifiedSearchResults(results, mode, normalizedQualifiedTerm);
			if (strictMatches.Length > 0)
			{
				return strictMatches.Take(maxResults).ToArray();
			}

			var fallbackTerm = BuildQualifiedFallbackTerm(mode, normalizedQualifiedTerm);
			if (string.IsNullOrWhiteSpace(fallbackTerm) || string.Equals(fallbackTerm, requestedTerm, StringComparison.OrdinalIgnoreCase))
			{
				return [];
			}

			var fallbackResults = await searchService.SearchAsync(
				assemblyTreeModel.AssemblyList,
				assemblyTreeModel.CurrentLanguage,
				languageVersion,
				mode,
				fallbackTerm,
				maxResults,
				cancellationToken);

			var fallbackStrictMatches = FilterQualifiedSearchResults(fallbackResults, mode, normalizedQualifiedTerm);
			return fallbackStrictMatches.Take(maxResults).ToArray();
		}

		private static bool LooksLikeQualifiedQuery(string normalizedTerm)
		{
			return !string.IsNullOrWhiteSpace(normalizedTerm)
				&& normalizedTerm.Contains('.', StringComparison.Ordinal)
				&& !normalizedTerm.StartsWith("M:", StringComparison.Ordinal)
				&& !normalizedTerm.StartsWith("T:", StringComparison.Ordinal)
				&& !normalizedTerm.StartsWith("P:", StringComparison.Ordinal)
				&& !normalizedTerm.StartsWith("F:", StringComparison.Ordinal)
				&& !normalizedTerm.StartsWith("E:", StringComparison.Ordinal);
		}

		private static string NormalizeQualifiedSearchTerm(string term)
		{
			if (string.IsNullOrWhiteSpace(term))
			{
				return string.Empty;
			}

			var normalized = term.Trim();
			var assemblySeparator = normalized.IndexOf("::", StringComparison.Ordinal);
			if (assemblySeparator > 0 && assemblySeparator + 2 < normalized.Length)
			{
				normalized = normalized[(assemblySeparator + 2)..];
			}

			normalized = normalized.Replace('/', '.').Replace('\\', '.');
			if (normalized.EndsWith(".decompiled", StringComparison.OrdinalIgnoreCase))
			{
				normalized = normalized[..^11];
			}

			if (normalized.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
			{
				normalized = normalized[..^3];
			}

			var parameterStart = normalized.IndexOf('(');
			if (parameterStart > 0)
			{
				normalized = normalized[..parameterStart];
			}

			while (normalized.Contains("..", StringComparison.Ordinal))
			{
				normalized = normalized.Replace("..", ".", StringComparison.Ordinal);
			}

			return normalized.Trim();
		}

		private static string BuildQualifiedFallbackTerm(string mode, string normalizedQualifiedTerm)
		{
			var separator = normalizedQualifiedTerm.LastIndexOf('.');
			if (separator < 0 || separator + 1 >= normalizedQualifiedTerm.Length)
			{
				return normalizedQualifiedTerm;
			}

			var tail = normalizedQualifiedTerm[(separator + 1)..];
			if (string.Equals(mode?.Trim(), "type", StringComparison.OrdinalIgnoreCase) && separator > 0)
			{
				return tail;
			}

			return tail;
		}

		private static SearchResult[] FilterQualifiedSearchResults(IEnumerable<SearchResult> candidates, string mode, string normalizedQualifiedTerm)
		{
			var separator = normalizedQualifiedTerm.LastIndexOf('.');
			if (separator < 0 || separator + 1 >= normalizedQualifiedTerm.Length)
			{
				return [];
			}

			var expectedContainer = normalizedQualifiedTerm[..separator];
			var expectedName = normalizedQualifiedTerm[(separator + 1)..];
			if (string.IsNullOrWhiteSpace(expectedContainer) || string.IsNullOrWhiteSpace(expectedName))
			{
				return [];
			}

			var expectTypeMode = string.Equals(mode?.Trim(), "type", StringComparison.OrdinalIgnoreCase);
			if (expectTypeMode)
			{
				var typeContainerSeparator = expectedContainer.LastIndexOf('.');
				if (typeContainerSeparator >= 0)
				{
					expectedName = expectedContainer[(typeContainerSeparator + 1)..];
					expectedContainer = expectedContainer[..typeContainerSeparator];
				}
			}

			var results = candidates.Where(result => {
				var location = (result.Location ?? string.Empty).Trim();
				var simpleName = ExtractSimpleResultName(result.Name);
				if (!string.Equals(simpleName, expectedName, StringComparison.OrdinalIgnoreCase))
				{
					return false;
				}

				if (string.Equals(location, expectedContainer, StringComparison.OrdinalIgnoreCase))
				{
					return true;
				}

				if (location.EndsWith("." + expectedContainer, StringComparison.OrdinalIgnoreCase))
				{
					return true;
				}

				var fullFromLocation = string.IsNullOrWhiteSpace(location) ? simpleName : location + "." + simpleName;
				return string.Equals(fullFromLocation, normalizedQualifiedTerm, StringComparison.OrdinalIgnoreCase);
			}).ToArray();

			return results;
		}

		private static string ExtractSimpleResultName(string? name)
		{
			if (string.IsNullOrWhiteSpace(name))
			{
				return string.Empty;
			}

			var simple = name.Trim();
			var signatureSeparator = simple.IndexOf(" :", StringComparison.Ordinal);
			if (signatureSeparator > 0)
			{
				simple = simple[..signatureSeparator];
			}

			var parameterSeparator = simple.IndexOf('(');
			if (parameterSeparator > 0)
			{
				simple = simple[..parameterSeparator];
			}

			var dotSeparator = simple.LastIndexOf('.');
			if (dotSeparator >= 0 && dotSeparator + 1 < simple.Length)
			{
				simple = simple[(dotSeparator + 1)..];
			}

			return simple.Trim();
		}

		private string ReadSelectedWindow(JsonElement? arguments, CancellationToken cancellationToken)
		{
			if (!TryParseWindowArguments(arguments, out var window, out var parseError))
			{
				return parseError;
			}

			var selected = GetSelectedText();
			var rawText = DecompileSelectedNodeText(cancellationToken);
			if (rawText.StartsWith("No selected node to decompile.", StringComparison.Ordinal))
			{
				return rawText;
			}

			var slice = SliceTextWindow(rawText, window.StartLine, window.LineCount);
			slice = ExpandToFullWindowForSmallText(rawText, slice);
			var builder = new StringBuilder();
			builder.AppendLine("Selected summary:");
			builder.AppendLine(selected);
			builder.AppendLine();
			builder.AppendLine("Selected content window:");
			builder.Append(FormatWindow(slice));
			return builder.ToString();
		}

		private async Task<string> SearchManyAsync(JsonElement? arguments, CancellationToken cancellationToken)
		{
			if (!TryParseSearchManyArguments(arguments, out var queries, out var maxResultsPerQuery, out var parseError))
			{
				return parseError;
			}

			var semaphore = new SemaphoreSlim(SearchManyMaxConcurrency);
			var tasks = queries
				.Select((query, queryIndex) => ExecuteSearchManyQueryAsync(query, queryIndex, maxResultsPerQuery, semaphore, cancellationToken))
				.ToArray();

			var queryResults = await Task.WhenAll(tasks);
			Array.Sort(queryResults, static (left, right) => left.QueryIndex.CompareTo(right.QueryIndex));

			lastSearchResults = queryResults.SelectMany(item => item.Results).ToArray();

			var builder = new StringBuilder();
			var globalIndex = 1;
			foreach (var result in queryResults)
			{
				builder.AppendLine($"[query {result.QueryIndex + 1}] mode='{result.Query.Mode}' term='{result.Query.Term}'");
				if (!string.IsNullOrWhiteSpace(result.Error))
				{
					builder.AppendLine($"error: {result.Error}");
					builder.AppendLine();
					continue;
				}

				if (result.Results.Length == 0)
				{
					builder.AppendLine("hits: 0");
					builder.AppendLine();
					continue;
				}

				builder.AppendLine($"hits: {result.Results.Length}");
				foreach (var (searchResult, localIndex) in result.Results.Select((item, idx) => (item, idx + 1)))
				{
					builder.AppendLine($"- {localIndex}. [global {globalIndex}] {searchResult.Name} | {searchResult.Location} | {searchResult.Assembly}");
					globalIndex++;
				}

				builder.AppendLine();
			}

			if (lastSearchResults.Length == 0)
			{
				builder.AppendLine("No results in search_many.");
			}

			return builder.ToString().TrimEnd();
		}

		private async Task<string> DecompileManyAsync(JsonElement? arguments, CancellationToken cancellationToken)
		{
			if (!TryParseDecompileManyArguments(arguments, out var requests, out var window, out var parseError))
			{
				return parseError;
			}

			var directJobs = new List<DecompileJob>();
			var immediateMessages = new List<string>();
			var querySearchResults = new List<SearchResult>();

			for (var i = 0; i < requests.Count; i++)
			{
				var request = requests[i];
				if (request.Kind == "search_index")
				{
					if (request.Index is not >= 1)
					{
						immediateMessages.Add($"target {i + 1}: invalid search_index (must be >= 1).");
						continue;
					}

					var searchIndex = request.Index.Value - 1;
					if (searchIndex < 0 || searchIndex >= lastSearchResults.Length)
					{
						immediateMessages.Add($"target {i + 1}: search_index {request.Index.Value} out of range (current: {lastSearchResults.Length}).");
						continue;
					}

					if (!TryResolveEntity(lastSearchResults[searchIndex], out var entity, out var resolveError))
					{
						immediateMessages.Add($"target {i + 1}: {resolveError}");
						continue;
					}

					directJobs.Add(new($"target {i + 1} (search_index={request.Index.Value})", entity));
					continue;
				}

				if (request.Kind == "query")
				{
					if (string.IsNullOrWhiteSpace(request.Mode) || string.IsNullOrWhiteSpace(request.Term))
					{
						immediateMessages.Add($"target {i + 1}: query requires mode and term.");
						continue;
					}

					var hits = await SearchCoreAsync(request.Mode, request.Term, SearchMaxResults, cancellationToken);
					if (hits.Length == 0)
					{
						immediateMessages.Add($"target {i + 1}: query mode='{request.Mode}' term='{request.Term}' returned 0 hits.");
						continue;
					}

					querySearchResults.AddRange(hits);

					var decompilableHits = new List<(SearchResult Result, IEntity Entity)>();
					foreach (var hit in hits)
					{
						if (TryResolveEntity(hit, out var entityCandidate, out _))
						{
							decompilableHits.Add((hit, entityCandidate));
						}
					}

					if (decompilableHits.Count == 0)
					{
						var preview = hits.Take(8).Select((item, idx) => $"  - {idx + 1}. {item.Name} | {item.Location} | {item.Assembly}");
						immediateMessages.Add(string.Join(Environment.NewLine, new[] {
							$"target {i + 1}: query mode='{request.Mode}' term='{request.Term}' returned {hits.Length} hits, but none are decompilable entities.",
							"candidates:",
						}.Concat(preview)));
						continue;
					}

					var candidateTake = hits.Length > 1
						? Math.Min(NormalizeDecompileManyQueryTake(request.Take), decompilableHits.Count)
						: 1;

					if (hits.Length > 1)
					{
						var preview = hits.Take(8).Select((item, idx) => $"  - {idx + 1}. {item.Name} | {item.Location} | {item.Assembly}");
						immediateMessages.Add(string.Join(Environment.NewLine, new[] {
							$"target {i + 1}: query mode='{request.Mode}' term='{request.Term}' has {hits.Length} hits; auto-decompiling first {candidateTake} decompilable candidate(s).",
							"candidates:",
						}.Concat(preview)));
					}

					for (var candidateIndex = 0; candidateIndex < candidateTake; candidateIndex++)
					{
						var candidate = decompilableHits[candidateIndex];
						var label = hits.Length > 1
							? $"target {i + 1} (query mode='{request.Mode}' term='{request.Term}', candidate {candidateIndex + 1}/{decompilableHits.Count})"
							: $"target {i + 1} (query mode='{request.Mode}' term='{request.Term}')";
						directJobs.Add(new(label, candidate.Entity));
					}

					continue;
				}

				immediateMessages.Add($"target {i + 1}: unsupported kind '{request.Kind}'.");
			}

			if (querySearchResults.Count > 0)
			{
				lastSearchResults = querySearchResults.ToArray();
			}

			var decompiledResults = await ExecuteDecompileJobsAsync(directJobs, window, cancellationToken);

			var builder = new StringBuilder();
			if (immediateMessages.Count > 0)
			{
				builder.AppendLine("Resolution summary:");
				foreach (var message in immediateMessages)
				{
					builder.AppendLine($"- {message}");
				}
				builder.AppendLine();
			}

			if (decompiledResults.FallbackMessage != null)
			{
				builder.AppendLine(decompiledResults.FallbackMessage);
				builder.AppendLine();
			}

			if (decompiledResults.Items.Count == 0)
			{
				builder.AppendLine("No decompilation output produced.");
				return builder.ToString().TrimEnd();
			}

			builder.AppendLine("Decompile windows:");
			for (var i = 0; i < decompiledResults.Items.Count; i++)
			{
				var item = decompiledResults.Items[i];
				builder.AppendLine($"[{i + 1}] {item.Label}");
				builder.AppendLine($"entity: {item.EntityDisplayName}");
				builder.Append(FormatWindow(item.Slice));
				if (i < decompiledResults.Items.Count - 1)
				{
					builder.AppendLine();
					builder.AppendLine();
				}
			}

			return builder.ToString().TrimEnd();
		}

		private string ReadResultWindow(int? index, JsonElement? arguments, CancellationToken cancellationToken)
		{
			if (!TryParseWindowArguments(arguments, out var window, out var parseWindowError))
			{
				return parseWindowError;
			}

			var resolvedIndex = ResolveSearchResultIndex(index, arguments);
			if (resolvedIndex is not >= 1)
			{
				return "read_result_window(index, window?): index must be >= 1.";
			}

			var searchResultIndex = resolvedIndex.Value - 1;
			if (searchResultIndex < 0 || searchResultIndex >= lastSearchResults.Length)
			{
				return $"Search result index out of range. Current results: {lastSearchResults.Length}.";
			}

			var result = lastSearchResults[searchResultIndex];
			if (!TryResolveEntity(result, out var entity, out var resolveError))
			{
				return resolveError;
			}

			var text = DecompileEntity(entity, cancellationToken);
			var slice = SliceTextWindow(text, window.StartLine, window.LineCount);
			slice = ExpandToFullWindowForSmallText(text, slice);

			var builder = new StringBuilder();
			builder.AppendLine($"result #{resolvedIndex}: {result.Name} | {result.Location} | {result.Assembly}");
			builder.AppendLine($"entity: {entity.FullName}");
			builder.Append(FormatWindow(slice));

			if (slice.HasMore)
			{
				builder.AppendLine();
				builder.AppendLine($"next suggestion: read_result_window(index={resolvedIndex}, window={{\"startLine\":{slice.NextStartLine},\"lineCount\":{window.LineCount}}})");
			}

			return builder.ToString().TrimEnd();
		}

		private async Task<SearchManyQueryResult> ExecuteSearchManyQueryAsync(SearchQuery query, int queryIndex, int maxResultsPerQuery, SemaphoreSlim semaphore, CancellationToken cancellationToken)
		{
			await semaphore.WaitAsync(cancellationToken);
			try
			{
				var results = await SearchCoreAsync(query.Mode, query.Term, maxResultsPerQuery, cancellationToken);
				return new(queryIndex, query, results, null);
			}
			catch (Exception ex)
			{
				return new(queryIndex, query, [], ex.Message);
			}
			finally
			{
				semaphore.Release();
			}
		}

		private async Task<DecompileExecutionResult> ExecuteDecompileJobsAsync(List<DecompileJob> jobs, WindowRequest window, CancellationToken cancellationToken)
		{
			if (jobs.Count == 0)
			{
				return new(new List<DecompileWindowResult>(), null);
			}

			if (jobs.Count == 1)
			{
				var singleItem = await Task.Run(() => DecompileJobToResult(jobs[0], 0, window, cancellationToken), cancellationToken);
				return new(new List<DecompileWindowResult> { singleItem }, null);
			}

			try
			{
				var parallelItems = await ExecuteDecompileJobsParallelAsync(jobs, window, cancellationToken);
				return new(parallelItems, null);
			}
			catch (Exception ex)
			{
				var fallbackItems = await ExecuteDecompileJobsSerialAsync(jobs, window, cancellationToken);
				return new(fallbackItems, $"Parallel decompile failed, fell back to serial: {ex.Message}");
			}
		}

		private async Task<List<DecompileWindowResult>> ExecuteDecompileJobsParallelAsync(List<DecompileJob> jobs, WindowRequest window, CancellationToken cancellationToken)
		{
			var semaphore = new SemaphoreSlim(DecompileManyMaxConcurrency);
			var tasks = jobs
				.Select(async (job, index) => {
					await semaphore.WaitAsync(cancellationToken);
					try
					{
						return await Task.Run(() => DecompileJobToResult(job, index, window, cancellationToken), cancellationToken);
					}
					finally
					{
						semaphore.Release();
					}
				})
				.ToArray();

			var results = await Task.WhenAll(tasks);
			return results.OrderBy(item => item.Order).ToList();
		}

		private async Task<List<DecompileWindowResult>> ExecuteDecompileJobsSerialAsync(List<DecompileJob> jobs, WindowRequest window, CancellationToken cancellationToken)
		{
			var results = new List<DecompileWindowResult>(jobs.Count);
			for (var i = 0; i < jobs.Count; i++)
			{
				cancellationToken.ThrowIfCancellationRequested();
				var item = await Task.Run(() => DecompileJobToResult(jobs[i], i, window, cancellationToken), cancellationToken);
				results.Add(item);
			}

			return results;
		}

		private DecompileWindowResult DecompileJobToResult(DecompileJob job, int order, WindowRequest window, CancellationToken cancellationToken)
		{
			var text = DecompileEntity(job.Entity, cancellationToken);
			var slice = SliceTextWindow(text, window.StartLine, window.LineCount);
			slice = ExpandToFullWindowForSmallText(text, slice);
			return new(order, job.Label, job.Entity.FullName, slice);
		}

		private string DecompileEntity(IEntity entity, CancellationToken cancellationToken)
		{
			var cacheKey = BuildDecompileCacheKey(entity);
			if (TryGetCachedDecompileText(cacheKey, out var cachedText))
			{
				AiChatLog.Info($"decompile cache hit keyHash={cacheKey.GetHashCode()} length={cachedText.Length}");
				return cachedText;
			}

			AiChatLog.Info($"decompile cache miss keyHash={cacheKey.GetHashCode()}");
			var options = dockWorkspace.ActiveTabPage?.CreateDecompilationOptions();
			if (options == null)
			{
				throw new InvalidOperationException("No active tab page for decompilation options.");
			}

			options.FullDecompilation = false;
			options.CancellationToken = cancellationToken;

			var output = new PlainTextOutput();
			var language = assemblyTreeModel.CurrentLanguage;
			switch (entity)
			{
				case ITypeDefinition type:
					language.DecompileType(type, output, options);
					break;
				case IMethod method:
					language.DecompileMethod(method, output, options);
					break;
				case IField field:
					language.DecompileField(field, output, options);
					break;
				case IProperty property:
					language.DecompileProperty(property, output, options);
					break;
				case IEvent @event:
					language.DecompileEvent(@event, output, options);
					break;
				default:
					throw new InvalidOperationException($"Unsupported entity type '{entity.GetType().Name}' for decompilation.");
			}

			var text = output.ToString();
			StoreCachedDecompileText(cacheKey, text);
			return text;
		}

		private string BuildDecompileCacheKey(IEntity entity)
		{
			var moduleName = entity.ParentModule?.Name ?? "<unknown-module>";
			var languageName = assemblyTreeModel.CurrentLanguage?.Name ?? "<unknown-language>";
			return moduleName + "|" + languageName + "|" + entity.FullName;
		}

		private bool TryGetCachedDecompileText(string cacheKey, out string text)
		{
			lock (decompileCacheLock)
			{
				if (decompileTextCache.TryGetValue(cacheKey, out var entry))
				{
					entry.LastAccessUtc = DateTime.UtcNow;
					text = entry.Text;
					return true;
				}
			}

			text = string.Empty;
			return false;
		}

		private void StoreCachedDecompileText(string cacheKey, string text)
		{
			if (string.IsNullOrEmpty(text))
			{
				return;
			}

			lock (decompileCacheLock)
			{
				decompileTextCache[cacheKey] = new(text, DateTime.UtcNow);
			}
		}

		private void TrimDecompileTextCache()
		{
			lock (decompileCacheLock)
			{
				if (decompileTextCache.Count == 0)
				{
					return;
				}

				var totalChars = decompileTextCache.Values.Sum(entry => entry.Text.Length);
				if (decompileTextCache.Count <= MaxDecompileCacheEntries && totalChars <= MaxDecompileCacheTotalChars)
				{
					return;
				}

				foreach (var key in decompileTextCache.OrderBy(item => item.Value.LastAccessUtc).Select(item => item.Key).ToArray())
				{
					if (decompileTextCache.Count <= MaxDecompileCacheEntries && totalChars <= MaxDecompileCacheTotalChars)
					{
						break;
					}

					if (!decompileTextCache.TryGetValue(key, out var entry))
					{
						continue;
					}

					decompileTextCache.Remove(key);
					totalChars -= entry.Text.Length;
				}
			}
		}

		private static bool TryResolveEntity(SearchResult searchResult, out IEntity entity, out string error)
		{
			if (searchResult.Reference is IEntity typedEntity)
			{
				entity = typedEntity;
				error = string.Empty;
				return true;
			}

			entity = null!;
			error = $"result '{searchResult.Name}' is not an entity and cannot be decompiled in batch mode.";
			return false;
		}

		private static int? ResolveSearchResultIndex(int? index, JsonElement? arguments)
		{
			if (index is >= 1)
			{
				return index;
			}

			if (arguments is not { ValueKind: JsonValueKind.Object } argumentObject)
			{
				return index;
			}

			if (argumentObject.TryGetProperty("index", out var indexElement)
				&& indexElement.ValueKind == JsonValueKind.Number
				&& indexElement.TryGetInt32(out var parsedIndex))
			{
				return parsedIndex;
			}

			return index;
		}

		private static string FormatWindow(TextWindowSlice slice)
		{
			var nextStartLineText = slice.HasMore ? slice.NextStartLine.ToString() : "<none>";
			var builder = new StringBuilder();
			builder.AppendLine($"window: startLine={slice.StartLine}, returnedLineCount={slice.ReturnedLineCount}, totalLines={slice.TotalLines}, hasMore={slice.HasMore.ToString().ToLowerInvariant()}, nextStartLine={nextStartLineText}");
			builder.AppendLine("content:");
			builder.Append(FormatWindowContentWithLineNumbers(slice));
			return builder.ToString();
		}

		private static string FormatWindowContentWithLineNumbers(TextWindowSlice slice)
		{
			if (string.IsNullOrEmpty(slice.Text))
			{
				return "<empty>";
			}

			var builder = new StringBuilder();
			using var reader = new System.IO.StringReader(slice.Text);
			string? line;
			var currentLine = slice.StartLine;
			var first = true;
			while ((line = reader.ReadLine()) != null)
			{
				if (!first)
				{
					builder.AppendLine();
				}

				builder.Append(currentLine.ToString().PadLeft(5));
				builder.Append(": ");
				builder.Append(line);
				currentLine++;
				first = false;
			}

			return builder.ToString();
		}

		private static TextWindowSlice ExpandToFullWindowForSmallText(string fullText, TextWindowSlice slice)
		{
			if (slice.TotalLines <= 0 || slice.TotalLines > SmallTextAutoFullReadLineThreshold)
			{
				return slice;
			}

			if (slice.StartLine == 1 && slice.ReturnedLineCount >= slice.TotalLines)
			{
				return slice;
			}

			return SliceTextWindow(fullText, 1, slice.TotalLines);
		}

		internal static TextWindowSlice SliceTextWindow(string text, int startLine, int lineCount)
		{
			var normalizedStartLine = startLine < 1 ? DefaultWindowStartLine : startLine;
			var normalizedLineCount = lineCount < 1 ? DefaultWindowLineCount : lineCount;

			var lines = new List<string>();
			using (var reader = new System.IO.StringReader(text ?? string.Empty))
			{
				string? line;
				while ((line = reader.ReadLine()) != null)
				{
					lines.Add(line);
				}
			}

			var totalLines = lines.Count;
			if (totalLines == 0)
			{
				return new(normalizedStartLine, 0, 0, false, -1, string.Empty);
			}

			var startIndex = normalizedStartLine - 1;
			if (startIndex >= totalLines)
			{
				return new(normalizedStartLine, 0, totalLines, false, -1, string.Empty);
			}

			var returnedLineCount = Math.Min(normalizedLineCount, totalLines - startIndex);
			var windowLines = lines.Skip(startIndex).Take(returnedLineCount);
			var windowText = string.Join(Environment.NewLine, windowLines);
			var hasMore = startIndex + returnedLineCount < totalLines;
			var nextStartLine = hasMore ? normalizedStartLine + returnedLineCount : -1;

			return new(normalizedStartLine, returnedLineCount, totalLines, hasMore, nextStartLine, windowText);
		}

		private static bool TryParseWindowArguments(JsonElement? arguments, out WindowRequest window, out string error)
		{
			window = new(DefaultWindowStartLine, DefaultWindowLineCount);
			error = string.Empty;

			if (arguments is not { ValueKind: JsonValueKind.Object } argumentObject)
			{
				return true;
			}

			var source = argumentObject;
			if (argumentObject.TryGetProperty("window", out var windowElement))
			{
				if (windowElement.ValueKind != JsonValueKind.Object)
				{
					error = "window must be an object with startLine/lineCount.";
					return false;
				}

				source = windowElement;
			}

			if (source.TryGetProperty("startLine", out var startLineElement))
			{
				if (startLineElement.ValueKind != JsonValueKind.Number || !startLineElement.TryGetInt32(out var startLineValue))
				{
					error = "startLine must be an integer.";
					return false;
				}

				window = window with { StartLine = Math.Max(DefaultWindowStartLine, startLineValue) };
			}

			if (source.TryGetProperty("lineCount", out var lineCountElement))
			{
				if (lineCountElement.ValueKind != JsonValueKind.Number || !lineCountElement.TryGetInt32(out var lineCountValue))
				{
					error = "lineCount must be an integer.";
					return false;
				}

				window = window with { LineCount = Math.Max(1, lineCountValue) };
			}

			return true;
		}

		private static bool TryParseSearchManyArguments(JsonElement? arguments, out List<SearchQuery> queries, out int maxResultsPerQuery, out string error)
		{
			queries = [];
			maxResultsPerQuery = SearchMaxResults;
			error = string.Empty;

			if (arguments is not { ValueKind: JsonValueKind.Object } argumentObject)
			{
				error = "Usage: search_many(queries, maxResultsPerQuery?).";
				return false;
			}

			if (!argumentObject.TryGetProperty("queries", out var queriesElement) || queriesElement.ValueKind != JsonValueKind.Array)
			{
				error = "search_many requires queries array.";
				return false;
			}

			foreach (var queryElement in queriesElement.EnumerateArray())
			{
				if (queryElement.ValueKind != JsonValueKind.Object)
				{
					error = "Each query must be an object with mode and term.";
					return false;
				}

				if (!queryElement.TryGetProperty("term", out var termElement) || termElement.ValueKind != JsonValueKind.String)
				{
					error = "Each query requires string term.";
					return false;
				}

				var mode = queryElement.TryGetProperty("mode", out var modeElement) && modeElement.ValueKind == JsonValueKind.String
					? modeElement.GetString() ?? "member"
					: "member";

				var term = termElement.GetString() ?? string.Empty;
				if (string.IsNullOrWhiteSpace(term))
				{
					error = "Each query term must be non-empty.";
					return false;
				}

				queries.Add(new(mode, term));
			}

			if (queries.Count == 0)
			{
				error = "queries must not be empty.";
				return false;
			}

			if (argumentObject.TryGetProperty("maxResultsPerQuery", out var maxElement))
			{
				if (maxElement.ValueKind != JsonValueKind.Number || !maxElement.TryGetInt32(out var parsedMax))
				{
					error = "maxResultsPerQuery must be an integer.";
					return false;
				}

				maxResultsPerQuery = Math.Max(1, parsedMax);
			}

			return true;
		}

		private static bool TryParseDecompileManyArguments(JsonElement? arguments, out List<DecompileTargetRequest> targets, out WindowRequest window, out string error)
		{
			targets = [];
			window = new(DefaultWindowStartLine, DefaultWindowLineCount);
			error = string.Empty;

			if (arguments is not { ValueKind: JsonValueKind.Object } argumentObject)
			{
				error = "Usage: decompile_many(targets, window?).";
				return false;
			}

			if (!argumentObject.TryGetProperty("targets", out var targetsElement) || targetsElement.ValueKind != JsonValueKind.Array)
			{
				error = "decompile_many requires targets array.";
				return false;
			}

			if (!TryParseWindowArguments(arguments, out window, out error))
			{
				return false;
			}

			foreach (var targetElement in targetsElement.EnumerateArray())
			{
				if (targetElement.ValueKind != JsonValueKind.Object)
				{
					error = "Each target must be an object.";
					return false;
				}

				if (!targetElement.TryGetProperty("kind", out var kindElement) || kindElement.ValueKind != JsonValueKind.String)
				{
					error = "Each target requires kind ('search_index' or 'query').";
					return false;
				}

				var kind = kindElement.GetString() ?? string.Empty;
				int? index = null;
				string? mode = null;
				string? term = null;
				int? take = null;

				if (targetElement.TryGetProperty("index", out var indexElement) && indexElement.ValueKind == JsonValueKind.Number && indexElement.TryGetInt32(out var parsedIndex))
				{
					index = parsedIndex;
				}

				if (targetElement.TryGetProperty("mode", out var modeElement) && modeElement.ValueKind == JsonValueKind.String)
				{
					mode = modeElement.GetString();
				}

				if (targetElement.TryGetProperty("term", out var termElement) && termElement.ValueKind == JsonValueKind.String)
				{
					term = termElement.GetString();
				}

				if (targetElement.TryGetProperty("take", out var takeElement))
				{
					if (takeElement.ValueKind != JsonValueKind.Number || !takeElement.TryGetInt32(out var parsedTake))
					{
						error = "target.take must be an integer.";
						return false;
					}

					take = parsedTake;
				}

				targets.Add(new(kind, index, mode, term, take));
			}

			if (targets.Count == 0)
			{
				error = "targets must not be empty.";
				return false;
			}

			return true;
		}

		internal static int NormalizeDecompileManyQueryTake(int? requestedTake)
		{
			if (!requestedTake.HasValue)
			{
				return DecompileManyDefaultQueryTake;
			}

			return Math.Clamp(requestedTake.Value, 1, DecompileManyMaxQueryTake);
		}

		private sealed record SearchQuery(string Mode, string Term);

		private sealed record SearchManyQueryResult(int QueryIndex, SearchQuery Query, SearchResult[] Results, string? Error);

		private sealed record WindowRequest(int StartLine, int LineCount);

		private sealed record ChunkBudget(int MaxLines, int MaxChars);

		private sealed record ChunkSlice(string Content, bool HasMore, int NextLineStart);

		private sealed class ContinuationState
		{
			public ContinuationState(string fullText, int nextLineStart, ChunkBudget budget)
			{
				FullText = fullText;
				NextLineStart = nextLineStart;
				Budget = budget;
				LastAccessUtc = DateTime.UtcNow;
			}

			public string FullText { get; }

			public int NextLineStart { get; set; }

			public ChunkBudget Budget { get; }

			public DateTime LastAccessUtc { get; set; }
		}

		private sealed record DecompileTargetRequest(string Kind, int? Index, string? Mode, string? Term, int? Take);

		private sealed record DecompileJob(string Label, IEntity Entity);

		private sealed record DecompileWindowResult(int Order, string Label, string EntityDisplayName, TextWindowSlice Slice);

		private sealed record DecompileExecutionResult(List<DecompileWindowResult> Items, string? FallbackMessage);

		private sealed class DecompileTextCacheEntry
		{
			public DecompileTextCacheEntry(string text, DateTime lastAccessUtc)
			{
				Text = text;
				LastAccessUtc = lastAccessUtc;
			}

			public string Text { get; }

			public DateTime LastAccessUtc { get; set; }
		}

		internal readonly record struct TextWindowSlice(int StartLine, int ReturnedLineCount, int TotalLines, bool HasMore, int NextStartLine, string Text);

		private string AnalyzeSelected()
		{
			var node = assemblyTreeModel.SelectedNodes.FirstOrDefault();
			if (node is not IMemberTreeNode { Member: { } member })
			{
				return "Please select a type/member node before analyze().";
			}

			analyzerTreeViewModel.Analyze(member);
			return $"Analyze started for: {member.FullName}";
		}

		private string OpenSearchResult(int? index)
		{
			if (index is not >= 1)
			{
				return "open_result(index): index must be >= 1.";
			}

			var resolved = index.Value - 1;
			if (resolved < 0 || resolved >= lastSearchResults.Length)
			{
				return $"Search result index out of range. Current results: {lastSearchResults.Length}.";
			}

			var reference = lastSearchResults[resolved].Reference;
			if (reference == null)
			{
				return "Selected search result has no navigable reference.";
			}

			MessageBus.Send(this, new NavigateToReferenceEventArgs(reference));
			return $"Opened result #{index}.";
		}
	}
}
