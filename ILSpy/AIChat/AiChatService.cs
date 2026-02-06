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
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using ICSharpCode.Decompiler;
using ICSharpCode.ILSpy.Analyzers;
using ICSharpCode.ILSpy.AssemblyTree;
using ICSharpCode.ILSpy.Docking;
using ICSharpCode.ILSpy.TreeNodes;
using ICSharpCode.ILSpy.ViewModels;
using ICSharpCode.ILSpyX;

namespace ICSharpCode.ILSpy.AIChat
{
	[Export]
	[Shared]
	public sealed class AiChatService
	{
		private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

		private readonly AssemblyTreeModel assemblyTreeModel;
		private readonly AnalyzerTreeViewModel analyzerTreeViewModel;
		private readonly DockWorkspace dockWorkspace;
		private readonly SettingsService settingsService;
		private readonly AiChatSearchService searchService;
		private readonly HttpClient httpClient;

		public AiChatService(AssemblyTreeModel assemblyTreeModel, AnalyzerTreeViewModel analyzerTreeViewModel, DockWorkspace dockWorkspace, SettingsService settingsService, AiChatSearchService searchService)
		{
			this.assemblyTreeModel = assemblyTreeModel;
			this.analyzerTreeViewModel = analyzerTreeViewModel;
			this.dockWorkspace = dockWorkspace;
			this.settingsService = settingsService;
			this.searchService = searchService;
			httpClient = new HttpClient {
				Timeout = TimeSpan.FromSeconds(90),
			};
		}

		public async Task<string> ExecuteAsync(AiChatParsedCommand command, CancellationToken cancellationToken)
		{
			return command.Kind switch
			{
				AiChatCommandKind.Help => GetHelpText(),
				AiChatCommandKind.Assemblies => await GetAssembliesAsync(cancellationToken),
				AiChatCommandKind.Selected => GetSelectedText(),
				AiChatCommandKind.Decompile => DecompileSelectedNode(),
				AiChatCommandKind.Search => await SearchAsync(command, cancellationToken),
				AiChatCommandKind.Analyze => AnalyzeSelected(),
				AiChatCommandKind.Provider => GetProviderStatusText(),
				AiChatCommandKind.Ask => await AskAsync(command.Prompt ?? string.Empty, cancellationToken),
				AiChatCommandKind.Prompt => await AskAsync(command.Prompt ?? string.Empty, cancellationToken),
				AiChatCommandKind.Unknown => $"Unknown command '/{command.UnknownCommandName}'. Type /help.",
				_ => "Unsupported command.",
			};
		}

		private string GetHelpText()
		{
			return string.Join(Environment.NewLine,
				"AI Chat commands:",
				"/help                 Show this help.",
				"/assemblies           List loaded assemblies.",
				"/selected             Show current selected node(s).",
				"/decompile            Decompile selected node and return text.",
				"/search [mode] <term> Search in assemblies (member/type/method/field/property/event/literal/namespace/assembly).",
				"/analyze              Analyze selected member/type and open Analyzer pane.",
				"/provider             Show provider config status.",
				"/ask <prompt>         Ask configured provider with ILSpy context.",
				"<text>                Same as /ask <text>.");
		}

		private async Task<string> GetAssembliesAsync(CancellationToken cancellationToken)
		{
			var assemblies = await assemblyTreeModel.AssemblyList.GetAllAssemblies();
			if (assemblies.Count == 0)
			{
				return "No assemblies loaded.";
			}

			var lines = assemblies
				.Select((assembly, index) => $"{index + 1}. {assembly.ShortName} ({assembly.FileName}){(assembly.HasLoadError ? " [load error]" : string.Empty)}")
				.ToArray();

			return string.Join(Environment.NewLine, lines);
		}

		private string GetSelectedText()
		{
			var nodes = assemblyTreeModel.SelectedNodes.ToArray();
			if (nodes.Length == 0)
			{
				return "No selection in assembly tree.";
			}

			var lines = nodes.Select((node, index) => $"{index + 1}. {node.Text} [{node.GetType().Name}]");
			return string.Join(Environment.NewLine, lines);
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

		private async Task<string> SearchAsync(AiChatParsedCommand command, CancellationToken cancellationToken)
		{
			var term = command.SearchTerm ?? string.Empty;
			if (string.IsNullOrWhiteSpace(term))
			{
				return "Usage: /search [mode] <term>";
			}

			var mode = command.SearchMode ?? "member";
			var results = await searchService.SearchAsync(
				assemblyTreeModel.AssemblyList,
				assemblyTreeModel.CurrentLanguage,
				assemblyTreeModel.CurrentLanguageVersion,
				mode,
				term,
				maxResults: 30,
				cancellationToken);

			if (results.Count == 0)
			{
				return $"No results for '{term}' in mode '{mode}'.";
			}

			var lines = results
				.Select((result, index) => $"{index + 1}. {result.Name} | {result.Location} | {result.Assembly}")
				.ToArray();

			return string.Join(Environment.NewLine, lines);
		}

		private string AnalyzeSelected()
		{
			var node = assemblyTreeModel.SelectedNodes.FirstOrDefault();
			if (node is not IMemberTreeNode { Member: { } member })
			{
				return "Please select a type/member node before running /analyze.";
			}

			analyzerTreeViewModel.Analyze(member);
			return $"Analyze started for: {member.FullName}";
		}

		private string GetProviderStatusText()
		{
			var settings = settingsService.GetSettings<AiChatSettings>();
			return settings.Provider switch
			{
				AiChatProviderKind.Disabled => "Provider is disabled. Configure in Options > AI Chat.",
				AiChatProviderKind.OpenAICompatible => $"Provider: OpenAI-compatible{Environment.NewLine}BaseUrl: {settings.OpenAIBaseUrl}{Environment.NewLine}Model: {settings.OpenAIModel}{Environment.NewLine}API key configured: {!string.IsNullOrWhiteSpace(settings.OpenAIApiKey)}",
				AiChatProviderKind.CodexCli => $"Provider: Codex CLI{Environment.NewLine}Path: {settings.CodexCliPath}{Environment.NewLine}Arguments: {settings.CodexCliArguments}",
				_ => "Unknown provider state.",
			};
		}

		private async Task<string> AskAsync(string prompt, CancellationToken cancellationToken)
		{
			if (string.IsNullOrWhiteSpace(prompt))
			{
				return "Prompt is empty.";
			}

			var settings = settingsService.GetSettings<AiChatSettings>();
			var context = await BuildContextAsync(cancellationToken);

			return settings.Provider switch
			{
				AiChatProviderKind.Disabled => "AI provider is disabled. Please configure it in Options > AI Chat.",
				AiChatProviderKind.OpenAICompatible => await AskOpenAICompatibleAsync(settings, prompt, context, cancellationToken),
				AiChatProviderKind.CodexCli => await AskCodexCliAsync(settings, prompt, context, cancellationToken),
				_ => "Unknown provider.",
			};
		}

		private async Task<string> BuildContextAsync(CancellationToken cancellationToken)
		{
			var sb = new StringBuilder();
			sb.AppendLine("ILSpy context:");
			sb.AppendLine("Loaded assemblies:");
			var assemblies = await assemblyTreeModel.AssemblyList.GetAllAssemblies();
			foreach (var assembly in assemblies.Take(20))
			{
				cancellationToken.ThrowIfCancellationRequested();
				sb.AppendLine($"- {assembly.ShortName}");
			}

			var selectedNode = assemblyTreeModel.SelectedNodes.FirstOrDefault();
			if (selectedNode != null)
			{
				sb.AppendLine($"Selected node: {selectedNode.Text} ({selectedNode.GetType().Name})");
				try
				{
					var output = new PlainTextOutput();
					var options = dockWorkspace.ActiveTabPage.CreateDecompilationOptions();
					options.FullDecompilation = false;
					selectedNode.Decompile(assemblyTreeModel.CurrentLanguage, output, options);

					var text = output.ToString();
					if (text.Length > 8000)
					{
						text = text.Substring(0, 8000) + Environment.NewLine + "... [truncated]";
					}

					sb.AppendLine("Selected node decompilation:");
					sb.AppendLine(text);
				}
				catch (Exception ex)
				{
					sb.AppendLine($"Decompilation failed: {ex.Message}");
				}
			}

			return sb.ToString();
		}

		private async Task<string> AskOpenAICompatibleAsync(AiChatSettings settings, string prompt, string context, CancellationToken cancellationToken)
		{
			if (string.IsNullOrWhiteSpace(settings.OpenAIApiKey))
			{
				return "OpenAI-compatible provider selected but API key is empty.";
			}

			var endpoint = settings.OpenAIBaseUrl?.TrimEnd('/') + "/chat/completions";
			if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
			{
				return "Invalid OpenAI-compatible base URL.";
			}

			var body = new {
				model = string.IsNullOrWhiteSpace(settings.OpenAIModel) ? "gpt-4.1-mini" : settings.OpenAIModel,
				messages = new object[] {
					new {
						role = "system",
						content = "You are an IL analysis assistant running inside ILSpy. Use provided context and be explicit when uncertain.",
					},
					new {
						role = "user",
						content = $"{context}{Environment.NewLine}{Environment.NewLine}User question:{Environment.NewLine}{prompt}",
					}
				},
				temperature = 0.2,
			};

			using var request = new HttpRequestMessage(HttpMethod.Post, uri);
			request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.OpenAIApiKey);
			request.Content = new StringContent(JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json");

			using var response = await httpClient.SendAsync(request, cancellationToken);
			var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
			if (!response.IsSuccessStatusCode)
			{
				return $"Provider error: {(int)response.StatusCode} {response.ReasonPhrase}{Environment.NewLine}{responseText}";
			}

			try
			{
				using var doc = JsonDocument.Parse(responseText);
				var choices = doc.RootElement.GetProperty("choices");
				if (choices.GetArrayLength() == 0)
				{
					return "Provider returned no choices.";
				}

				var content = choices[0].GetProperty("message").GetProperty("content").GetString();
				return string.IsNullOrWhiteSpace(content) ? "Provider returned empty response." : content;
			}
			catch (Exception ex)
			{
				return $"Failed to parse provider response: {ex.Message}{Environment.NewLine}{responseText}";
			}
		}

		private async Task<string> AskCodexCliAsync(AiChatSettings settings, string prompt, string context, CancellationToken cancellationToken)
		{
			var executable = string.IsNullOrWhiteSpace(settings.CodexCliPath) ? "codex" : settings.CodexCliPath;
			var customArgs = settings.CodexCliArguments ?? string.Empty;

			var fullPrompt = $"{context}{Environment.NewLine}{Environment.NewLine}User question:{Environment.NewLine}{prompt}";

			var psi = new ProcessStartInfo {
				FileName = executable,
				Arguments = $"{customArgs} \"{fullPrompt.Replace("\"", "\\\"") }\"",
				UseShellExecute = false,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				CreateNoWindow = true,
			};

			using var process = new Process { StartInfo = psi };
			try
			{
				process.Start();
			}
			catch (Exception ex)
			{
				return $"Failed to start Codex CLI: {ex.Message}";
			}

			var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
			var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
			await process.WaitForExitAsync(cancellationToken);
			await Task.WhenAll(outputTask, errorTask);
			var output = outputTask.Result;
			var error = errorTask.Result;

			if (process.ExitCode != 0)
			{
				return $"Codex CLI failed with exit code {process.ExitCode}.{Environment.NewLine}{error}";
			}

			if (!string.IsNullOrWhiteSpace(output))
			{
				return output.Trim();
			}

			return string.IsNullOrWhiteSpace(error) ? "Codex CLI returned no output." : error.Trim();
		}
	}
}
