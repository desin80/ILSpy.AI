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
using System.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using ICSharpCode.Decompiler;
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
		private readonly AssemblyTreeModel assemblyTreeModel;
		private readonly AnalyzerTreeViewModel analyzerTreeViewModel;
		private readonly DockWorkspace dockWorkspace;
		private readonly AiChatSearchService searchService;

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
				"- search(mode, term)",
				"- analyze()",
				"- open_result(index)",
				"Tool call format:",
				"<tool_call>",
				"{\"name\":\"search\",\"arguments\":{\"mode\":\"method\",\"term\":\"Find\"}}",
				"</tool_call>");
		}

		public async Task<AiChatToolResult> ExecuteAsync(string toolName, string? mode, string? term, int? index, CancellationToken cancellationToken)
		{
			try
			{
				switch (toolName)
				{
					case "assemblies":
						return new() { Success = true, Output = await ListAssembliesAsync(cancellationToken) };
					case "selected":
						return new() { Success = true, Output = GetSelectedText() };
					case "decompile":
						return new() { Success = true, Output = DecompileSelectedNode() };
					case "search":
						return new() { Success = true, Output = await SearchAsync(mode ?? "member", term ?? string.Empty, cancellationToken) };
					case "analyze":
						return new() { Success = true, Output = AnalyzeSelected() };
					case "open_result":
						return new() { Success = true, Output = OpenSearchResult(index) };
					default:
						return new() { Success = false, Output = $"Unknown tool '{toolName}'." };
				}
			}
			catch (Exception ex)
			{
				return new() { Success = false, Output = ex.Message };
			}
		}

		private SearchResult[] lastSearchResults = [];

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

		private string DecompileSelectedNode()
		{
			var node = assemblyTreeModel.SelectedNodes.FirstOrDefault();
			if (node == null)
			{
				return "No selected node to decompile.";
			}

			var output = new PlainTextOutput();
			var options = dockWorkspace.ActiveTabPage.CreateDecompilationOptions();
			options.FullDecompilation = false;
			node.Decompile(assemblyTreeModel.CurrentLanguage, output, options);

			const int maxLength = 12_000;
			var text = output.ToString();
			if (text.Length > maxLength)
			{
				text = text.Substring(0, maxLength) + Environment.NewLine + "... [truncated]";
			}

			return text;
		}

		private async Task<string> SearchAsync(string mode, string term, CancellationToken cancellationToken)
		{
			if (string.IsNullOrWhiteSpace(term))
			{
				lastSearchResults = [];
				return "Usage: search(mode, term).";
			}

			var languageVersion = assemblyTreeModel.CurrentLanguageVersion;
			if (languageVersion == null)
			{
				return "Current language version is unavailable.";
			}

			var results = await searchService.SearchAsync(
				assemblyTreeModel.AssemblyList,
				assemblyTreeModel.CurrentLanguage,
				languageVersion,
				mode,
				term,
				maxResults: 30,
				cancellationToken);

			lastSearchResults = results.ToArray();
			if (lastSearchResults.Length == 0)
			{
				return $"No results for '{term}' in mode '{mode}'.";
			}

			return string.Join(Environment.NewLine, lastSearchResults.Select((result, i) => $"{i + 1}. {result.Name} | {result.Location} | {result.Assembly}"));
		}

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
