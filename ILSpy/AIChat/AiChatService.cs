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
using System.Threading;
using System.Threading.Tasks;

namespace ICSharpCode.ILSpy.AIChat
{
	#nullable enable

	[Export]
	[Shared]
	public sealed class AiChatService
	{
		private const int MaxAutoSteps = 30;

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

			if (onProgress != null)
			{
				await onProgress("[status] Collecting ILSpy context...\n");
			}

			var context = await toolDispatcher.ExecuteAsync("selected", null, null, null, cancellationToken);
			var assemblyContext = await toolDispatcher.ExecuteAsync("assemblies", null, null, null, cancellationToken);

			var fullPrompt = new StringBuilder();
			fullPrompt.AppendLine("You are an IL analysis assistant running inside ILSpy.");
			fullPrompt.AppendLine("Do not output tool_call blocks in this mode.");
			fullPrompt.AppendLine("Use the supplied context and be explicit when uncertain.");
			fullPrompt.AppendLine();
			fullPrompt.AppendLine("Current selection:");
			fullPrompt.AppendLine(context.Output);
			fullPrompt.AppendLine();
			fullPrompt.AppendLine("Loaded assemblies:");
			fullPrompt.AppendLine(assemblyContext.Output);
			fullPrompt.AppendLine();
			fullPrompt.AppendLine("User question:");
			fullPrompt.AppendLine(prompt);

			if (onProgress != null)
			{
				await onProgress("[status] Sending request to Codex...\n");
			}

			return await codexClient.AskAsync(fullPrompt.ToString(), onProgress, cancellationToken);
		}

		private async Task<string> AutoAsync(string goal, Func<string, Task>? onProgress, CancellationToken cancellationToken)
		{
			if (string.IsNullOrWhiteSpace(goal))
			{
				return "Usage: /auto <goal>";
			}

			var transcript = new StringBuilder();
			var state = string.Empty;

			for (var step = 1; step <= MaxAutoSteps; step++)
			{
				cancellationToken.ThrowIfCancellationRequested();

				if (onProgress != null)
				{
					await onProgress($"[progress] Step {step}/{MaxAutoSteps}: planning next action...\n");
				}

				var prompt = BuildAutoPrompt(goal, state, step);
				var response = await codexClient.AskAsync(prompt, cancellationToken);

				var toolCall = AiChatToolCallParser.Parse(response);
				if (toolCall == null)
				{
					var cleaned = AiChatToolCallParser.RemoveToolCallBlock(response);
					if (onProgress != null)
					{
						await onProgress($"[progress] Step {step}/{MaxAutoSteps}: model produced final answer.\n");
					}
					transcript.AppendLine($"[step {step}] assistant");
					transcript.AppendLine(cleaned);
					return transcript.ToString().Trim();
				}

				if (onProgress != null)
				{
					await onProgress($"[progress] Step {step}/{MaxAutoSteps}: running tool '{toolCall.Name}'...\n");
				}

				var toolResult = await toolDispatcher.ExecuteAsync(toolCall.Name, toolCall.Mode, toolCall.Term, toolCall.Index, cancellationToken);
				if (onProgress != null)
				{
					await onProgress($"[progress] Step {step}/{MaxAutoSteps}: tool '{toolCall.Name}' finished.\n");
				}

				transcript.AppendLine($"[step {step}] tool: {toolCall.Name}");
				transcript.AppendLine(toolResult.Output);

				state += Environment.NewLine + $"Step {step} tool '{toolCall.Name}' result:" + Environment.NewLine + toolResult.Output;
			}

			transcript.AppendLine($"Reached max auto steps ({MaxAutoSteps}). Please refine your goal or continue with another /auto command.");
			return transcript.ToString().Trim();
		}

		private string BuildAutoPrompt(string goal, string state, int step)
		{
			var builder = new StringBuilder();
			builder.AppendLine("You are an autonomous ILSpy analysis agent.");
			builder.AppendLine("Decide your next step. If you need a tool, output exactly one <tool_call> JSON block and nothing else.");
			builder.AppendLine("If you can conclude, output final answer without tool_call block.");
			builder.AppendLine();
			builder.AppendLine(toolDispatcher.GetToolSpecText());
			builder.AppendLine();
			builder.AppendLine($"Goal: {goal}");
			builder.AppendLine($"Current step: {step}");
			if (!string.IsNullOrWhiteSpace(state))
			{
				builder.AppendLine("Previous tool outputs:");
				builder.AppendLine(state);
			}

			return builder.ToString();
		}
	}
}
