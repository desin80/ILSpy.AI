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
using System.Threading;
using System.Threading.Tasks;

using ICSharpCode.Decompiler;
using ICSharpCode.ILSpy.Search;
using ICSharpCode.ILSpyX;
using ICSharpCode.ILSpyX.Abstractions;
using ICSharpCode.ILSpyX.Search;

namespace ICSharpCode.ILSpy.AIChat
{
	#nullable enable

	[Export]
	[Shared]
	public sealed class AiChatSearchService
	{
		private readonly ITreeNodeFactory treeNodeFactory;
		private readonly SettingsService settingsService;

		public AiChatSearchService(ITreeNodeFactory treeNodeFactory, SettingsService settingsService)
		{
			this.treeNodeFactory = treeNodeFactory;
			this.settingsService = settingsService;
		}

		public async Task<IReadOnlyList<SearchResult>> SearchAsync(AssemblyList assemblyList, Language language, LanguageVersion languageVersion, string modeText, string term, int maxResults, CancellationToken cancellationToken)
		{
			if (string.IsNullOrWhiteSpace(term))
			{
				return [];
			}

			var request = new SearchRequest {
				Keywords = [term],
				Mode = ToSearchMode(modeText),
				MemberSearchKind = ToMemberSearchKind(modeText),
				SearchResultFactory = new SearchResultFactory(language),
				TreeNodeFactory = treeNodeFactory,
				DecompilerSettings = CloneDecompilerSettings(languageVersion),
			};

			var queue = new ConcurrentQueue<SearchResult>();
			var strategy = CreateSearchStrategy(request, queue, settingsService.SessionSettings.LanguageSettings.ShowApiLevel, language);
			if (strategy == null)
			{
				return [];
			}

			var assemblies = await assemblyList.GetAllAssemblies();
			foreach (var loadedAssembly in assemblies)
			{
				cancellationToken.ThrowIfCancellationRequested();
				var module = loadedAssembly.GetMetadataFileOrNull();
				if (module == null)
					continue;

				strategy.Search(module, cancellationToken);
			}

			return queue
				.OrderByDescending(r => r.Fitness)
				.ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
				.Take(maxResults)
				.ToArray();
		}

		private DecompilerSettings CloneDecompilerSettings(LanguageVersion languageVersion)
		{
			var decompilerSettings = settingsService.DecompilerSettings.Clone();
			if (!Enum.TryParse(languageVersion?.Version, out Decompiler.CSharp.LanguageVersion value))
			{
				value = Decompiler.CSharp.LanguageVersion.Latest;
			}

			decompilerSettings.SetLanguageVersion(value);
			return decompilerSettings;
		}

		private static SearchMode ToSearchMode(string mode)
		{
			return mode?.Trim().ToLowerInvariant() switch
			{
				"assembly" or "a" => SearchMode.Assembly,
				"namespace" or "ns" or "n" => SearchMode.Namespace,
				"type" or "t" => SearchMode.Type,
				"method" or "m" => SearchMode.Method,
				"field" or "f" => SearchMode.Field,
				"property" or "p" => SearchMode.Property,
				"event" or "e" => SearchMode.Event,
				"literal" or "string" or "const" or "c" => SearchMode.Literal,
				"resource" or "r" => SearchMode.Resource,
				"token" or "@" => SearchMode.Token,
				_ => SearchMode.Member,
			};
		}

		private static MemberSearchKind ToMemberSearchKind(string mode)
		{
			return mode?.Trim().ToLowerInvariant() switch
			{
				"type" or "t" => MemberSearchKind.Type,
				"method" or "m" => MemberSearchKind.Method,
				"field" or "f" => MemberSearchKind.Field,
				"property" or "p" => MemberSearchKind.Property,
				"event" or "e" => MemberSearchKind.Event,
				_ => MemberSearchKind.Member,
			};
		}

		private static AbstractSearchStrategy? CreateSearchStrategy(SearchRequest request, IProducerConsumerCollection<SearchResult> queue, ApiVisibility apiVisibility, Language language)
		{
			if ((request.Keywords?.Length ?? 0) == 0 && request.RegEx == null)
			{
				return null;
			}

			return request.Mode switch
			{
				SearchMode.TypeAndMember => new MemberSearchStrategy(language, apiVisibility, request, queue),
				SearchMode.Type => new MemberSearchStrategy(language, apiVisibility, request, queue, MemberSearchKind.Type),
				SearchMode.Member => new MemberSearchStrategy(language, apiVisibility, request, queue, request.MemberSearchKind),
				SearchMode.Method => new MemberSearchStrategy(language, apiVisibility, request, queue, MemberSearchKind.Method),
				SearchMode.Field => new MemberSearchStrategy(language, apiVisibility, request, queue, MemberSearchKind.Field),
				SearchMode.Property => new MemberSearchStrategy(language, apiVisibility, request, queue, MemberSearchKind.Property),
				SearchMode.Event => new MemberSearchStrategy(language, apiVisibility, request, queue, MemberSearchKind.Event),
				SearchMode.Literal => new LiteralSearchStrategy(language, apiVisibility, request, queue),
				SearchMode.Token => new MetadataTokenSearchStrategy(language, apiVisibility, request, queue),
				SearchMode.Resource => new ResourceSearchStrategy(apiVisibility, request, queue),
				SearchMode.Assembly => new AssemblySearchStrategy(request, queue, AssemblySearchKind.NameOrFileName),
				SearchMode.Namespace => new NamespaceSearchStrategy(request, queue),
				_ => null,
			};
		}
	}
}
