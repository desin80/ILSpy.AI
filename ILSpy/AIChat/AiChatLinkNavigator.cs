using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

using ICSharpCode.Decompiler.Documentation;
using ICSharpCode.Decompiler.TypeSystem;
using ICSharpCode.ILSpy.AssemblyTree;
using ICSharpCode.ILSpy.Docking;
using ICSharpCode.ILSpy.Util;
using ICSharpCode.ILSpy.ViewModels;
using ICSharpCode.ILSpyX;
using ICSharpCode.ILSpyX.Search;

namespace ICSharpCode.ILSpy.AIChat
{
	#nullable enable

	internal sealed class AiChatLinkNavigationResult
	{
		public bool Success { get; init; }
		public string Message { get; init; } = string.Empty;
		public IReadOnlyList<AiChatLinkNavigationCandidate> Candidates { get; init; } = Array.Empty<AiChatLinkNavigationCandidate>();
	}

	internal sealed record AiChatLinkNavigationCandidate(string Name, string Location, string Assembly, AiChatLinkReference? Reference)
	{
		public string DisplayText => $"{Name} | {Location} | {Assembly}";
	}

	internal sealed class AiChatLinkNavigator
	{
		private const int SearchMaxResults = 24;

		private readonly Lazy<AssemblyTreeModel> assemblyTreeModel;
		private readonly Lazy<AiChatSearchService> searchService;
		private readonly Lazy<DockWorkspace> dockWorkspace;

		public Action<string>? OpenExternalLink { get; set; }
		public Func<string, IEnumerable<LoadedAssembly>, IEntity?>? ResolveIdStringOverride { get; set; }
		public Func<string?, IReadOnlyList<LoadedAssembly>>? GetRelevantAssembliesOverride { get; set; }
		public Func<string, string, CancellationToken, IReadOnlyList<SearchResult>>? SearchOverride { get; set; }
		public Action<object>? NavigateReferenceOverride { get; set; }
		public Func<int, bool>? ScrollToLineOverride { get; set; }

		public AiChatLinkNavigator()
		{
			assemblyTreeModel = new Lazy<AssemblyTreeModel>(() => App.ExportProvider.GetExportedValue<AssemblyTreeModel>());
			searchService = new Lazy<AiChatSearchService>(() => App.ExportProvider.GetExportedValue<AiChatSearchService>());
			dockWorkspace = new Lazy<DockWorkspace>(() => App.ExportProvider.GetExportedValue<DockWorkspace>());
			OpenExternalLink = link => GlobalUtils.OpenLink(link);
		}

		public AiChatLinkNavigationResult Navigate(AiChatLinkReference reference)
		{
			if (reference.Kind == AiChatLinkReferenceKind.External)
			{
				OpenExternalLink?.Invoke(reference.Target);
				return new AiChatLinkNavigationResult {
					Success = true,
					Message = $"Opened external link: {reference.Target}",
				};
			}

			var symbolSpec = ParseSymbolSpec(reference.Target);
			if (string.IsNullOrWhiteSpace(symbolSpec.Symbol))
			{
				return new AiChatLinkNavigationResult {
					Success = false,
					Message = "Invalid symbol reference.",
				};
			}

			var normalizedSymbol = NormalizeSymbolForSearch(symbolSpec.Symbol);
			if (IsIdString(normalizedSymbol))
			{
				return NavigateByIdString(symbolSpec.AssemblyName, normalizedSymbol, reference.Line);
			}

			return NavigateBySearch(symbolSpec.AssemblyName, normalizedSymbol, reference.Line);
		}

		private AiChatLinkNavigationResult NavigateByIdString(string? assemblyName, string idString, int? line)
		{
			try
			{
				var assemblies = GetRelevantAssemblies(assemblyName);
				if (assemblies.Count == 0)
				{
					return new AiChatLinkNavigationResult {
						Success = false,
						Message = assemblyName == null
							? "No loaded assemblies available."
							: $"Assembly '{assemblyName}' is not loaded.",
					};
				}

				var entity = ResolveIdStringOverride != null
					? ResolveIdStringOverride(idString, assemblies)
					: AssemblyTreeModel.FindEntityInRelevantAssemblies(idString, assemblies);

				if (entity == null)
				{
					return new AiChatLinkNavigationResult {
						Success = false,
						Message = $"Symbol not found: {idString}",
					};
				}

				NavigateToReference(entity);
				TryScrollToLine(line);
				return new AiChatLinkNavigationResult {
					Success = true,
					Message = $"Opened symbol: {idString}",
				};
			}
			catch (Exception ex)
			{
				AiChatLog.Error(ex, "symbol id navigation failed");
				return new AiChatLinkNavigationResult {
					Success = false,
					Message = ex.Message,
				};
			}
		}

		private AiChatLinkNavigationResult NavigateBySearch(string? assemblyName, string symbol, int? line)
		{
			try
			{
				var primaryTerms = BuildPrimarySearchTerms(symbol).ToArray();
				var fallbackTerms = BuildFallbackSearchTerms(symbol).ToArray();
				var searchModes = BuildSearchModes(symbol).ToArray();
				var aggregate = new List<SearchResult>();
				var asmModel = SearchOverride == null ? assemblyTreeModel.Value : null;
				var assemblyList = asmModel?.AssemblyList;
				var language = asmModel?.CurrentLanguage;
				var languageVersion = asmModel?.CurrentLanguageVersion;
				if (SearchOverride == null)
				{
					if (languageVersion == null)
					{
						return new AiChatLinkNavigationResult {
							Success = false,
							Message = "Language version is unavailable for symbol navigation.",
						};
					}
				}

				CollectSearchHits(primaryTerms, searchModes, aggregate);
				if (aggregate.Count == 0)
				{
					CollectSearchHits(fallbackTerms, searchModes, aggregate);
				}

				void CollectSearchHits(IEnumerable<string> terms, IEnumerable<string> modes, List<SearchResult> target)
				{
					foreach (var term in terms)
					{
						foreach (var mode in modes)
						{
							IReadOnlyList<SearchResult> hits;
							if (SearchOverride != null)
							{
								hits = SearchOverride(mode, term, CancellationToken.None);
							}
							else
							{
								hits = searchService.Value.SearchAsync(
									assemblyList!,
									language!,
									languageVersion!,
									mode,
									term,
									SearchMaxResults,
									CancellationToken.None).GetAwaiter().GetResult();
							}

							if (!string.IsNullOrWhiteSpace(assemblyName))
							{
								hits = hits.Where(item => ItemMatchesAssembly(item, assemblyName)).ToArray();
							}

							target.AddRange(hits);
						}
					}
				}

				if (aggregate.Count == 0)
				{
					return new AiChatLinkNavigationResult {
						Success = false,
						Message = $"Symbol not found: {symbol}",
					};
				}

				var ordered = aggregate
					.GroupBy(GetReferenceIdentity)
					.Select(group => group.First())
					.OrderByDescending(item => MatchScore(symbol, item))
					.ThenByDescending(item => item.Fitness)
					.ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
					.ToArray();

				var containerPreferred = PreferQualifiedContainerMatches(symbol, ordered);

				var ranked = containerPreferred
					.Select(item => (Item: item, Score: MatchScore(symbol, item)))
					.ToArray();

				if (ShouldDisambiguate(symbol, ranked))
				{
					var candidates = ranked
						.Take(16)
						.Select(item => CreateNavigationCandidate(item.Item))
						.ToArray();

					return new AiChatLinkNavigationResult {
						Success = false,
						Message = $"Multiple symbols matched '{symbol}'.",
						Candidates = candidates,
					};
				}

				var best = ranked[0].Item;
				if (best.Reference == null)
				{
					return new AiChatLinkNavigationResult {
						Success = false,
						Message = $"Cannot navigate to symbol '{symbol}' because reference is unavailable.",
					};
				}

				NavigateToReference(best.Reference);
				TryScrollToLine(line);
				return new AiChatLinkNavigationResult {
					Success = true,
					Message = $"Opened symbol: {best.Name}",
				};
			}
			catch (Exception ex)
			{
				AiChatLog.Error(ex, "symbol search navigation failed");
				return new AiChatLinkNavigationResult {
					Success = false,
					Message = ex.Message,
				};
			}
		}

		private static bool ShouldDisambiguate(string symbol, (SearchResult Item, int Score)[] ranked)
		{
			if (ranked.Length <= 1)
			{
				return false;
			}

			if (!IsQualifiedSymbol(symbol))
			{
				return true;
			}

			var top = ranked[0];
			var second = ranked[1];
			if (top.Score == second.Score)
			{
				return true;
			}

			if (top.Score - second.Score < 35)
			{
				return true;
			}

			return !IsStrongQualifiedMatch(symbol, top.Item);
		}

		private static bool IsQualifiedSymbol(string symbol)
		{
			if (string.IsNullOrWhiteSpace(symbol))
			{
				return false;
			}

			return symbol.Contains("::", StringComparison.Ordinal)
				|| symbol.Contains('.', StringComparison.Ordinal)
				|| IsIdString(symbol);
		}

		private static bool IsStrongQualifiedMatch(string symbol, SearchResult item)
		{
			var trimmed = TrimMethodSignature(symbol).Trim();
			var separator = trimmed.LastIndexOf('.');
			if (separator <= 0 || separator + 1 >= trimmed.Length)
			{
				return false;
			}

			var container = trimmed[..separator];
			var memberName = trimmed[(separator + 1)..];
			if (string.IsNullOrWhiteSpace(container) || string.IsNullOrWhiteSpace(memberName))
			{
				return false;
			}

			var locationExact = string.Equals(item.Location, container, StringComparison.OrdinalIgnoreCase);
			var nameExact = string.Equals(item.Name, memberName, StringComparison.OrdinalIgnoreCase)
				|| item.Name.StartsWith(memberName + "(", StringComparison.OrdinalIgnoreCase);
			return locationExact && nameExact;
		}

		private static bool IsIdString(string symbol)
		{
			return symbol.StartsWith("T:", StringComparison.Ordinal)
				|| symbol.StartsWith("M:", StringComparison.Ordinal)
				|| symbol.StartsWith("P:", StringComparison.Ordinal)
				|| symbol.StartsWith("F:", StringComparison.Ordinal)
				|| symbol.StartsWith("E:", StringComparison.Ordinal);
		}

		private static (string? AssemblyName, string Symbol) ParseSymbolSpec(string raw)
		{
			var normalized = raw.Trim();
			var separator = normalized.IndexOf("::", StringComparison.Ordinal);
			if (separator > 0)
			{
				var assemblyName = normalized[..separator].Trim();
				var symbol = normalized[(separator + 2)..].Trim();
				return (string.IsNullOrWhiteSpace(assemblyName) ? null : assemblyName, symbol);
			}

			return (null, normalized);
		}

		private static string NormalizeSymbolForSearch(string symbol)
		{
			if (string.IsNullOrWhiteSpace(symbol))
			{
				return symbol;
			}

			var normalized = symbol.Trim().Replace('/', '.').Replace('\\', '.');
			if (normalized.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
			{
				normalized = normalized[..^3];
			}

			if (normalized.EndsWith(".decompiled", StringComparison.OrdinalIgnoreCase))
			{
				normalized = normalized[..^11];
			}

			while (normalized.Contains("..", StringComparison.Ordinal))
			{
				normalized = normalized.Replace("..", ".", StringComparison.Ordinal);
			}

			return normalized.Trim('.');
		}

		private IReadOnlyList<LoadedAssembly> GetRelevantAssemblies(string? assemblyName)
		{
			if (GetRelevantAssembliesOverride != null)
			{
				return GetRelevantAssembliesOverride(assemblyName);
			}

			var all = assemblyTreeModel.Value.AssemblyList.GetAssemblies();
			if (string.IsNullOrWhiteSpace(assemblyName))
			{
				return all;
			}

			var matches = all
				.Where(asm => string.Equals(asm.ShortName, assemblyName, StringComparison.OrdinalIgnoreCase))
				.ToArray();
			if (matches.Length > 0)
			{
				return matches;
			}

			return all
				.Where(asm => asm.Text.Contains(assemblyName, StringComparison.OrdinalIgnoreCase))
				.ToArray();
		}

		private static IEnumerable<string> BuildPrimarySearchTerms(string symbol)
		{
			var trimmed = symbol.Trim();
			if (string.IsNullOrWhiteSpace(trimmed))
			{
				yield break;
			}

			var withoutSignature = TrimMethodSignature(trimmed);
			var dedupe = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (var term in new[] { trimmed, withoutSignature })
			{
				if (!string.IsNullOrWhiteSpace(term) && dedupe.Add(term))
				{
					yield return term;
				}
			}
		}

		private static IEnumerable<string> BuildFallbackSearchTerms(string symbol)
		{
			var trimmed = TrimMethodSignature(symbol.Trim());
			if (string.IsNullOrWhiteSpace(trimmed))
			{
				yield break;
			}

			var separator = trimmed.LastIndexOf('.');
			if (separator >= 0 && separator + 1 < trimmed.Length)
			{
				yield return trimmed[(separator + 1)..];
			}
		}

		private static IEnumerable<string> BuildSearchModes(string symbol)
		{
			if (IsLikelyTypeSymbol(symbol))
			{
				yield return "type";
				yield return "member";
				yield break;
			}

			if (symbol.Contains('('))
			{
				yield return "method";
				yield return "member";
				yield break;
			}

			yield return "member";
		}

		private static string TrimMethodSignature(string symbol)
		{
			var index = symbol.IndexOf('(');
			return index > 0 ? symbol[..index].Trim() : symbol;
		}

		private static bool ItemMatchesAssembly(SearchResult item, string assemblyName)
		{
			return item.Assembly.Contains(assemblyName, StringComparison.OrdinalIgnoreCase);
		}

		private static SearchResult[] PreferQualifiedContainerMatches(string symbol, SearchResult[] items)
		{
			if (items.Length == 0)
			{
				return items;
			}

			var withoutSignature = TrimMethodSignature(symbol);
			var separator = withoutSignature.LastIndexOf('.');
			if (separator <= 0 || separator + 1 >= withoutSignature.Length)
			{
				return items;
			}

			var container = withoutSignature[..separator];
			if (string.IsNullOrWhiteSpace(container))
			{
				return items;
			}

			var filtered = items
				.Where(item =>
					string.Equals(item.Location, container, StringComparison.OrdinalIgnoreCase)
					|| item.Location.Contains(container + ".", StringComparison.OrdinalIgnoreCase)
					|| item.Name.Contains(container + ".", StringComparison.OrdinalIgnoreCase))
				.ToArray();

			return filtered.Length > 0 ? filtered : items;
		}

		private static int MatchScore(string symbol, SearchResult item)
		{
			var score = 0;
			var withoutSignature = TrimMethodSignature(symbol);
			var separator = withoutSignature.LastIndexOf('.');
			var memberName = withoutSignature[(separator + 1)..];
			var containerName = separator > 0 ? withoutSignature[..separator] : string.Empty;
			var typeLike = IsLikelyTypeSymbol(symbol);

			if (string.Equals(item.Name, memberName, StringComparison.OrdinalIgnoreCase)
				|| item.Name.StartsWith(memberName + "(", StringComparison.OrdinalIgnoreCase))
			{
				score += 120;
			}

			if (!string.IsNullOrWhiteSpace(containerName) && string.Equals(item.Location, containerName, StringComparison.OrdinalIgnoreCase))
			{
				score += 180;
			}

			if (!string.IsNullOrWhiteSpace(containerName) && item.Location.Contains(containerName, StringComparison.OrdinalIgnoreCase))
			{
				score += 60;
			}

			if (item.Location.Contains(withoutSignature, StringComparison.OrdinalIgnoreCase)
				|| item.Name.Contains(withoutSignature, StringComparison.OrdinalIgnoreCase))
			{
				score += 50;
			}

			if (item.Location.Contains(symbol, StringComparison.OrdinalIgnoreCase)
				|| item.Name.Contains(symbol, StringComparison.OrdinalIgnoreCase))
			{
				score += 25;
			}

			if (typeLike)
			{
				if (item.Name.Contains('('))
				{
					score -= 100;
				}

				if (IsTypeSearchResult(item))
				{
					score += 120;
				}
			}

			score += Math.Clamp((int)(item.Fitness * 10), 0, 20);
			return score;
		}

		private static bool IsLikelyTypeSymbol(string symbol)
		{
			if (string.IsNullOrWhiteSpace(symbol) || symbol.Contains('('))
			{
				return false;
			}

			var trimmed = TrimMethodSignature(symbol.Trim());
			var separator = trimmed.LastIndexOf('.');
			if (separator <= 0 || separator + 1 >= trimmed.Length)
			{
				return false;
			}

			var tail = trimmed[(separator + 1)..];
			return tail.Length > 0 && char.IsUpper(tail[0]);
		}

		private static bool IsTypeSearchResult(SearchResult result)
		{
			return result.Reference is IEntity entity && entity.SymbolKind == SymbolKind.TypeDefinition;
		}

		private static string GetReferenceIdentity(SearchResult result)
		{
			if (result.Reference is IEntity entity)
			{
				return entity.GetIdString();
			}

			return $"{result.Assembly}|{result.Location}|{result.Name}";
		}

		private static string FormatResultCandidate(SearchResult result)
		{
			return $"{result.Name} | {result.Location} | {result.Assembly}";
		}

		private static AiChatLinkNavigationCandidate CreateNavigationCandidate(SearchResult result)
		{
			var name = string.IsNullOrWhiteSpace(result.Name) ? "<unknown>" : result.Name;
			var location = string.IsNullOrWhiteSpace(result.Location) ? "<unknown>" : result.Location;
			var assemblyDisplay = string.IsNullOrWhiteSpace(result.Assembly) ? "<unknown>" : result.Assembly;
			if (result.Reference is IEntity entity)
			{
				var assembly = ExtractAssemblyShortName(result.Assembly);
				var symbol = string.IsNullOrWhiteSpace(assembly)
					? entity.GetIdString()
					: $"{assembly}::{entity.GetIdString()}";
				return new AiChatLinkNavigationCandidate(name, location, assemblyDisplay, AiChatLinkReference.CreateSymbol(symbol));
			}

			var fallback = BuildFallbackCandidateSymbol(result);
			if (!string.IsNullOrWhiteSpace(fallback))
			{
				return new AiChatLinkNavigationCandidate(name, location, assemblyDisplay, AiChatLinkReference.CreateSymbol(fallback));
			}

			return new AiChatLinkNavigationCandidate(name, location, assemblyDisplay, null);
		}

		private static string BuildFallbackCandidateSymbol(SearchResult result)
		{
			var assembly = ExtractAssemblyShortName(result.Assembly);
			var name = TrimMethodSignature(result.Name);
			if (string.IsNullOrWhiteSpace(name))
			{
				return string.Empty;
			}

			var location = result.Location?.Trim();
			if (string.IsNullOrWhiteSpace(location))
			{
				return string.IsNullOrWhiteSpace(assembly) ? name : $"{assembly}::{name}";
			}

			var symbol = location.EndsWith("." + name, StringComparison.OrdinalIgnoreCase)
				? location
				: location + "." + name;

			return string.IsNullOrWhiteSpace(assembly) ? symbol : $"{assembly}::{symbol}";
		}

		private static string ExtractAssemblyShortName(string assemblyDisplay)
		{
			if (string.IsNullOrWhiteSpace(assemblyDisplay))
			{
				return string.Empty;
			}

			var separator = assemblyDisplay.IndexOf(',');
			return separator > 0 ? assemblyDisplay[..separator].Trim() : assemblyDisplay.Trim();
		}

		private void NavigateToReference(object reference)
		{
			if (NavigateReferenceOverride != null)
			{
				NavigateReferenceOverride(reference);
				return;
			}

			MessageBus.Send(this, new NavigateToReferenceEventArgs(reference));
		}

		private void TryScrollToLine(int? line)
		{
			if (!line.HasValue || line.Value <= 0)
			{
				return;
			}

			if (ScrollToLineOverride != null)
			{
				_ = ScrollToLineOverride(line.Value);
				return;
			}

			var dispatcher = Application.Current?.Dispatcher;
			if (dispatcher == null)
			{
				return;
			}

			var attempts = 0;
			var bestEffortTargetLine = line.Value;
			var timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) {
				Interval = TimeSpan.FromMilliseconds(180),
			};
			void AttemptScroll()
			{
				attempts++;
				var ok = false;
				try
				{
					dockWorkspace.Value.ActiveTabPage.ShowTextView(textView => {
						var documentLineCount = textView.LineCount;
						var target = documentLineCount > 0 ? Math.Clamp(bestEffortTargetLine, 1, documentLineCount) : bestEffortTargetLine;
						ok = textView.ScrollToLine(target, unfold: true);
						if (!ok && documentLineCount > 0)
						{
							ok = textView.ScrollToLine(documentLineCount, unfold: true);
						}
					});
				}
				catch
				{
					ok = false;
				}

				if (ok || attempts >= 18)
				{
					timer.Stop();
				}
			}

			timer.Tick += (_, _) => AttemptScroll();
			dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, (Action)(() => {
				AttemptScroll();
				if (attempts == 1)
				{
					timer.Start();
				}
			}));
		}
	}
}
