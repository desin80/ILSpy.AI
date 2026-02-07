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
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;

namespace ICSharpCode.ILSpy.AIChat
{
	#nullable enable

	[Export]
	[Shared]
	public sealed class AiChatService
	{
		private const int AutoStateMaxChunkLength = 2500;
		private const int AskSnapshotLineCount = 500;
		private const int AskAssemblySummaryLimit = 12;

		private readonly AiChatToolDispatcher toolDispatcher;
		private readonly AiChatCodexClient codexClient;

		public AiChatService(AiChatToolDispatcher toolDispatcher, AiChatCodexClient codexClient)
		{
			this.toolDispatcher = toolDispatcher;
			this.codexClient = codexClient;
		}

		public Task<string> ExecuteAsync(AiChatParsedCommand command, CancellationToken cancellationToken)
		{
			return ExecuteCoreAsync(command, null, cancellationToken);
		}

		public Task<string> ExecuteAsync(AiChatParsedCommand command, Func<string, Task>? onProgress, CancellationToken cancellationToken)
		{
			return ExecuteCoreAsync(command, onProgress, cancellationToken);
		}

		private async Task<string> ExecuteCoreAsync(AiChatParsedCommand command, Func<string, Task>? onProgress, CancellationToken cancellationToken)
		{
			var stopwatch = Stopwatch.StartNew();
			AiChatLog.Info($"service execute command={command.Kind}");

			try
			{
				switch (command.Kind)
				{
					case AiChatCommandKind.Help:
						return GetHelpText();
					case AiChatCommandKind.CodexTest:
						return await codexClient.ConnectivityTestAsync(cancellationToken);
					case AiChatCommandKind.Assemblies:
						return (await toolDispatcher.ExecuteAsync("assemblies", null, null, null, cancellationToken)).Output;
				case AiChatCommandKind.Selected:
					return (await toolDispatcher.ExecuteAsync("selected", null, null, null, cancellationToken)).Output;
				case AiChatCommandKind.Decompile:
					return (await toolDispatcher.ExecuteAsync("decompile", null, null, null, cancellationToken)).Output;
				case AiChatCommandKind.Search:
					return (await toolDispatcher.ExecuteAsync("search", command.SearchMode, command.SearchTerm, null, cancellationToken)).Output;
				case AiChatCommandKind.Analyze:
					return (await toolDispatcher.ExecuteAsync("analyze", null, null, null, cancellationToken)).Output;
				case AiChatCommandKind.Provider:
					return GetProviderStatusText();
					case AiChatCommandKind.Auto:
						return await AutoAsync(command.Prompt ?? string.Empty, onProgress, cancellationToken);
					case AiChatCommandKind.Ask:
					case AiChatCommandKind.Prompt:
						return await AskAsync(command.Prompt ?? string.Empty, onProgress, cancellationToken);
				case AiChatCommandKind.Unknown:
					return $"Unknown command '/{command.UnknownCommandName}'. Type /help.";
				default:
					return "Unsupported command.";
			}
			}
			catch (Exception ex)
			{
				AiChatLog.Error(ex, $"service command={command.Kind} failed elapsedMs={stopwatch.ElapsedMilliseconds}");
				throw;
			}
			finally
			{
				AiChatLog.Info($"service command={command.Kind} finished elapsedMs={stopwatch.ElapsedMilliseconds}");
			}
		}

		private string GetHelpText()
		{
			return string.Join(Environment.NewLine,
				"AI Chat commands:",
				"/help                 Show this help.",
				"/codex-test           Test Codex CLI connectivity.",
				"/assemblies           List loaded assemblies.",
				"/selected             Show current selected node(s).",
				"/decompile            Decompile selected node and return text.",
				"/search [mode] <term> Search in assemblies (member/type/method/field/property/event/literal/namespace/assembly).",
				"/analyze              Analyze selected member/type and open Analyzer pane.",
				"/provider             Show Codex CLI integration status.",
				"/ask <prompt>         Ask Codex once with current ILSpy context.",
				"/auto <goal>          Autonomous workflow loop with tool calling.",
				"<text>                Same as /ask <text>.");
		}

		private static string GetProviderStatusText()
		{
			var executable = Environment.GetEnvironmentVariable("ILSPY_CODEX_CLI");
			if (string.IsNullOrWhiteSpace(executable))
			{
				executable = "codex";
			}

			var extraArgs = Environment.GetEnvironmentVariable("ILSPY_CODEX_ARGS") ?? string.Empty;
			return string.Join(Environment.NewLine,
				"Provider: External Codex CLI",
				$"Executable: {executable}",
				$"Extra args: {(string.IsNullOrWhiteSpace(extraArgs) ? "<none>" : extraArgs)}",
				"Configuration source: your existing Codex CLI login/config files.");
		}

		private async Task<string> AskAsync(string prompt, Func<string, Task>? onProgress, CancellationToken cancellationToken)
		{
			if (string.IsNullOrWhiteSpace(prompt))
			{
				return "Prompt is empty.";
			}

			AiChatLog.Info($"ask mode begin promptLength={prompt.Length}");
			if (onProgress != null)
			{
				await onProgress("Collecting ILSpy context...\n");
			}

			var context = await toolDispatcher.ExecuteAsync("selected", null, null, null, cancellationToken);
			var decompileWindowContext = await toolDispatcher.ExecuteAsync("read_selected_window", null, null, null, CreateWindowArguments(startLine: 1, lineCount: AskSnapshotLineCount), cancellationToken);
			var assemblyContext = await toolDispatcher.ExecuteAsync("assemblies", null, null, null, cancellationToken);
			var sparseAssemblyContext = CreateSparseAssembliesSummary(assemblyContext.Output, AskAssemblySummaryLimit);

			var fullPrompt = new StringBuilder();
			fullPrompt.AppendLine("You are an IL analysis assistant running inside ILSpy.");
			fullPrompt.AppendLine("Do not output tool_call blocks in this mode.");
			fullPrompt.AppendLine("Use the supplied context and be explicit when uncertain.");
			fullPrompt.AppendLine();
			fullPrompt.AppendLine("Current selection:");
			fullPrompt.AppendLine(context.Output);
			fullPrompt.AppendLine();
			fullPrompt.AppendLine("Selected decompilation window (if available):");
			fullPrompt.AppendLine(decompileWindowContext.Output);
			fullPrompt.AppendLine();
			fullPrompt.AppendLine("Loaded assemblies summary:");
			fullPrompt.AppendLine(sparseAssemblyContext);
			fullPrompt.AppendLine();
			fullPrompt.AppendLine("User question:");
			fullPrompt.AppendLine(prompt);

			if (onProgress != null)
			{
				await onProgress("Sending request to Codex...\n");
			}

			var response = await codexClient.AskAsync(fullPrompt.ToString(), onProgress, cancellationToken);
			AiChatLog.Info($"ask mode finished responseLength={(response?.Length ?? 0)}");
			return response ?? string.Empty;
		}

		private async Task<string> AutoAsync(string goal, Func<string, Task>? onProgress, CancellationToken cancellationToken)
		{
			if (string.IsNullOrWhiteSpace(goal))
			{
				return "Usage: /auto <goal>";
			}
			AiChatLog.Info($"auto mode begin goalLength={goal.Length}");
			var state = string.Empty;
			var step = 0;
			var initialContext = await BuildInitialAutoContextSnapshotAsync(cancellationToken);

			while (true)
			{
				step++;
				cancellationToken.ThrowIfCancellationRequested();

				if (onProgress != null)
				{
					await onProgress($"\n=== Step {step} ===\n");
				}

				var prompt = BuildAutoPrompt(goal, state, step, step == 1 ? initialContext : null);
				var response = await codexClient.AskAsync(prompt, cancellationToken);
				var modelText = AiChatToolCallParser.RemoveToolCallBlock(response);
				if (onProgress != null && !string.IsNullOrWhiteSpace(modelText))
				{
					await onProgress($"Thinking: {ToSingleLine(modelText, 260)}\n");
				}

				var toolCall = AiChatToolCallParser.Parse(response);
				if (toolCall == null)
				{
					var cleaned = modelText;
				if (onProgress != null)
				{
					await onProgress("Final answer generated.\n");
				}

					var finalText = string.IsNullOrWhiteSpace(cleaned)
						? "Auto mode finished without a final response."
						: cleaned.Trim();
					AiChatLog.Info($"auto mode finished step={step} finalLength={finalText.Length}");
					return finalText;
				}

				if (onProgress != null)
				{
					await onProgress($"Tool call: {toolCall.Name}\n");
				}

				var toolResult = await toolDispatcher.ExecuteAsync(toolCall.Name, toolCall.Mode, toolCall.Term, toolCall.Index, toolCall.Arguments, cancellationToken);
				AiChatLog.Info($"auto step={step} tool={toolCall.Name} success={toolResult.Success} outputLength={(toolResult.Output?.Length ?? 0)}");
				if (onProgress != null)
				{
					await onProgress($"Tool result ({toolCall.Name}):\n");
					await onProgress($"{(string.IsNullOrWhiteSpace(toolResult.Output) ? "<empty>" : toolResult.Output)}\n");
				}

				if (!toolResult.Success)
				{
					return $"Tool '{toolCall.Name}' failed: {toolResult.Output}";
				}

				var toolOutputForState = toolResult.Output ?? string.Empty;
				if (toolOutputForState.Length > AutoStateMaxChunkLength)
				{
					toolOutputForState = toolOutputForState.Substring(0, AutoStateMaxChunkLength) + Environment.NewLine + "... [truncated for context budget]";
				}

				state += Environment.NewLine + $"Step {step} tool '{toolCall.Name}' result:" + Environment.NewLine + toolOutputForState;
			}
		}

		private static string ToSingleLine(string text, int maxLength)
		{
			if (string.IsNullOrWhiteSpace(text))
				return "<empty>";

			var normalized = text.Replace("\r", " ").Replace("\n", " ").Trim();
			if (normalized.Length <= maxLength)
				return normalized;

			return normalized.Substring(0, maxLength) + "...";
		}

		private string BuildAutoPrompt(string goal, string state, int step)
		{
			return BuildAutoPrompt(goal, state, step, null);
		}

		private string BuildAutoPrompt(string goal, string state, int step, string? initialContext)
		{
			var builder = new StringBuilder();
			builder.AppendLine("You are an autonomous ILSpy analysis agent.");
			builder.AppendLine("Decide your next step. If you need a tool, output exactly one <tool_call> JSON block and nothing else.");
			builder.AppendLine("If you can conclude, output final answer without tool_call block.");
			builder.AppendLine("Prefer batch/window tools (search_many, decompile_many, read_selected_window) to reduce round-trips.");
			builder.AppendLine("For very large outputs, prefer larger maxLines/maxChars and keep calling continue_output(continue_token) until complete.");
			builder.AppendLine();
			builder.AppendLine(toolDispatcher.GetToolSpecText());
			builder.AppendLine();
			builder.AppendLine($"Goal: {goal}");
			builder.AppendLine($"Current step: {step}");
			if (!string.IsNullOrWhiteSpace(initialContext))
			{
				builder.AppendLine("Initial ILSpy context snapshot:");
				builder.AppendLine(initialContext);
			}

			if (!string.IsNullOrWhiteSpace(state))
			{
				builder.AppendLine("Previous tool outputs:");
				builder.AppendLine(state);
			}

			return builder.ToString();
		}

		private async Task<string> BuildInitialAutoContextSnapshotAsync(CancellationToken cancellationToken)
		{
			var selectedContext = await toolDispatcher.ExecuteAsync("selected", null, null, null, cancellationToken);
			var windowContext = await toolDispatcher.ExecuteAsync("read_selected_window", null, null, null, CreateWindowArguments(startLine: 1, lineCount: AskSnapshotLineCount), cancellationToken);
			var assembliesContext = await toolDispatcher.ExecuteAsync("assemblies", null, null, null, cancellationToken);

			var builder = new StringBuilder();
			builder.AppendLine("Current selection:");
			builder.AppendLine(selectedContext.Output);
			builder.AppendLine();
			builder.AppendLine("Selected window:");
			builder.AppendLine(windowContext.Output);
			builder.AppendLine();
			builder.AppendLine("Assemblies summary:");
			builder.AppendLine(CreateSparseAssembliesSummary(assembliesContext.Output, AskAssemblySummaryLimit));
			return builder.ToString().TrimEnd();
		}

		private static JsonElement? CreateWindowArguments(int startLine, int lineCount)
		{
			using var document = JsonDocument.Parse($"{{\"window\":{{\"startLine\":{startLine},\"lineCount\":{lineCount}}}}}");
			return document.RootElement.Clone();
		}

		private static string CreateSparseAssembliesSummary(string assemblyOutput, int maxLines)
		{
			if (string.IsNullOrWhiteSpace(assemblyOutput))
			{
				return "<empty>";
			}

			var lines = assemblyOutput
				.Split(["\r\n", "\n"], StringSplitOptions.None)
				.Where(line => !string.IsNullOrWhiteSpace(line))
				.ToArray();

			if (lines.Length <= maxLines)
			{
				return assemblyOutput;
			}

			return string.Join(Environment.NewLine, lines.Take(maxLines))
				+ Environment.NewLine
				+ $"... ({lines.Length - maxLines} more assemblies omitted)";
		}
	}
}
