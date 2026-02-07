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
using System.Collections.Generic;
using System.Composition;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ICSharpCode.ILSpy.AIChat
{
	#nullable enable

	[Export]
	[Shared]
	public sealed class AiChatCodexClient
	{
		private static TimeSpan AskTimeout {
			get {
				var configured = Environment.GetEnvironmentVariable("ILSPY_CODEX_TIMEOUT_SECONDS");
				if (int.TryParse(configured, out var seconds) && seconds > 0)
				{
					return TimeSpan.FromSeconds(seconds);
				}

				return TimeSpan.FromMinutes(3);
			}
		}

		public async Task<string> ConnectivityTestAsync(CancellationToken cancellationToken)
		{
			var prompt = "Return exactly this text and nothing else: CODEx_OK";
			var response = await AskAsync(prompt, cancellationToken);

			if (string.IsNullOrWhiteSpace(response))
			{
				return "Codex test failed: empty response.";
			}

			if (response.IndexOf("codex_ok", StringComparison.OrdinalIgnoreCase) >= 0)
			{
				return "Codex connectivity: OK";
			}

			return "Codex connectivity: reachable (response received, probe token not matched).\n" + response;
		}

		public async Task<string> AskAsync(string prompt, CancellationToken cancellationToken)
		{
			return await AskAsync(prompt, onPartialResponse: null, cancellationToken);
		}

		public async Task<string> AskAsync(string prompt, Func<string, Task>? onPartialResponse, CancellationToken cancellationToken)
		{
			if (string.IsNullOrWhiteSpace(prompt))
			{
				return "Prompt is empty.";
			}

			Process? process = null;
			string? outputLastMessagePath = null;
			var askTimeout = AskTimeout;
			var stopwatch = Stopwatch.StartNew();
			try
			{
				AiChatLog.Info($"codex ask start promptLength={prompt.Length} timeoutSec={(int)askTimeout.TotalSeconds}");
				using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
				timeoutCts.CancelAfter(askTimeout);
				var token = timeoutCts.Token;

				var executable = GetCodexExecutable();
				outputLastMessagePath = Path.Combine(Path.GetTempPath(), $"ilspy-codex-last-{Guid.NewGuid():N}.txt");
				var arguments = BuildArguments(outputLastMessagePath);

				var psi = new ProcessStartInfo {
					FileName = executable,
					Arguments = arguments,
					UseShellExecute = false,
					RedirectStandardInput = true,
					RedirectStandardOutput = true,
					RedirectStandardError = true,
					StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
					StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
					StandardErrorEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
					CreateNoWindow = true,
				};

				process = new Process { StartInfo = psi };
				try
				{
					process.Start();
					AiChatLog.Info($"codex process started file='{executable}' pid={process.Id}");
				}
				catch (Exception ex)
				{
					AiChatLog.Error(ex, "codex process start failed");
					return $"Failed to start Codex CLI '{executable}': {ex.Message}";
				}

				await process.StandardInput.WriteAsync(prompt.AsMemory(), token);
				await process.StandardInput.FlushAsync();
				process.StandardInput.Close();

				var outputBuilder = new StringBuilder();
				var errorTask = process.StandardError.ReadToEndAsync(token);
				var hasReceivedOutput = 0;

				using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(token);
				Task? heartbeatTask = null;
				if (onPartialResponse != null)
				{
					heartbeatTask = Task.Run(async () =>
					{
						try
						{
							var frame = 0;
							var frames = new[] { "waiting for response.", "waiting for response..", "waiting for response..." };
							while (!heartbeatCts.Token.IsCancellationRequested && Interlocked.CompareExchange(ref hasReceivedOutput, 0, 0) == 0)
							{
								await Task.Delay(TimeSpan.FromSeconds(2), heartbeatCts.Token);
								if (heartbeatCts.Token.IsCancellationRequested || Interlocked.CompareExchange(ref hasReceivedOutput, 0, 0) != 0)
									break;

								await onPartialResponse($"{frames[frame]}\r");
								frame = (frame + 1) % frames.Length;
							}
						}
						catch (OperationCanceledException)
						{
							// expected when request completes or gets cancelled
						}
					}, CancellationToken.None);
				}

				if (onPartialResponse == null)
				{
					outputBuilder.Append(await process.StandardOutput.ReadToEndAsync(token));
				}
				else
				{
					char[] buffer = new char[1024];
					while (true)
					{
						var count = await process.StandardOutput.ReadAsync(buffer.AsMemory(0, buffer.Length), token);
						if (count == 0)
						{
							break;
						}

						Interlocked.Exchange(ref hasReceivedOutput, 1);
						outputBuilder.Append(buffer, 0, count);
						// Do not stream raw Codex CLI stdout to UI.
						// The CLI may output runtime transcript/debug lines (workdir/model/tokens used)
						// that should never be shown as assistant content.
					}
				}

				heartbeatCts.Cancel();
				if (heartbeatTask != null)
				{
					await heartbeatTask;
				}

				await process.WaitForExitAsync(token);
				var error = (await errorTask)?.Trim() ?? string.Empty;
				var output = outputBuilder.ToString().Trim();

				if (process.ExitCode != 0)
				{
					AiChatLog.Warn($"codex process exited with code={process.ExitCode} stderrLength={error.Length} elapsedMs={stopwatch.ElapsedMilliseconds}");
					return string.IsNullOrWhiteSpace(error)
						? $"Codex CLI failed with exit code {process.ExitCode}."
						: $"Codex CLI failed with exit code {process.ExitCode}.{Environment.NewLine}{error}";
				}

				var lastMessage = TryReadOutputLastMessage(outputLastMessagePath);
				if (!string.IsNullOrWhiteSpace(lastMessage))
				{
					var normalizedFromOutfile = NormalizeCodexResponsePayload(lastMessage);
					if (!string.IsNullOrWhiteSpace(normalizedFromOutfile))
					{
						AiChatLog.Info($"codex ask success source=outfile length={normalizedFromOutfile.Length} elapsedMs={stopwatch.ElapsedMilliseconds}");
						return normalizedFromOutfile;
					}

					AiChatLog.Warn($"codex outfile contained no assistant payload length={lastMessage.Length} elapsedMs={stopwatch.ElapsedMilliseconds}");
				}

				if (!string.IsNullOrWhiteSpace(output))
				{
					var structured = ExtractAssistantMessageFromJsonEvents(output);
					if (!string.IsNullOrWhiteSpace(structured))
					{
						var structuredNormalized = NormalizeCodexResponsePayload(structured);
						if (!string.IsNullOrWhiteSpace(structuredNormalized)
							&& !LooksLikeCodexTranscript(structuredNormalized)
							&& !ContainsRuntimeTranscriptMarkers(structuredNormalized))
						{
							AiChatLog.Info($"codex ask success source=json-events length={structuredNormalized.Length} elapsedMs={stopwatch.ElapsedMilliseconds}");
							return structuredNormalized;
						}
					}

					var normalized = NormalizeCodexResponsePayload(output);
					if (string.IsNullOrWhiteSpace(normalized) && LooksLikeCodexTranscript(output))
					{
						AiChatLog.Warn($"codex transcript parse failed length={output.Length} elapsedMs={stopwatch.ElapsedMilliseconds}");
						return "Codex returned runtime transcript without a final assistant message. Please continue from current context without replaying prior steps.";
					}

					if (LooksLikeCodexTranscript(output))
					{
						var leakedSignals = new[] { "mcp startup:", "thinking", "tokens used" };
						if (leakedSignals.Any(signal => normalized.Contains(signal, StringComparison.OrdinalIgnoreCase)))
						{
							AiChatLog.Warn($"codex transcript leak detected; suppressing output length={normalized.Length} elapsedMs={stopwatch.ElapsedMilliseconds}");
							return "Codex returned runtime transcript instead of final assistant message. Please retry once; if it persists, reduce context or switch to /ask mode.";
						}
					}

					var finalText = string.IsNullOrWhiteSpace(normalized) ? string.Empty : normalized;
					if (LooksLikeCodexTranscript(finalText) || ContainsRuntimeTranscriptMarkers(finalText))
					{
						AiChatLog.Warn($"codex runtime transcript blocked length={finalText.Length} elapsedMs={stopwatch.ElapsedMilliseconds}");
						return "Codex returned runtime transcript instead of final assistant message. Please continue from current context without replaying prior steps.";
					}

					if (string.IsNullOrWhiteSpace(finalText))
					{
						AiChatLog.Warn($"codex empty assistant payload after normalization stdoutLength={output.Length} elapsedMs={stopwatch.ElapsedMilliseconds}");
						return "Codex finished but produced no parseable assistant message. Please continue from current context without replaying prior steps.";
					}

					AiChatLog.Info($"codex ask success source=stdout length={finalText.Length} elapsedMs={stopwatch.ElapsedMilliseconds}");
					return finalText;
				}

				AiChatLog.Warn($"codex ask finished with empty output stderrLength={error.Length} elapsedMs={stopwatch.ElapsedMilliseconds}");
				return string.IsNullOrWhiteSpace(error) ? "Codex CLI returned no output." : error;
			}
			catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
			{
				try
				{
					if (process != null && !process.HasExited)
					{
						process.Kill(entireProcessTree: true);
					}
				}
				catch
				{
				}

				AiChatLog.Warn($"codex ask timeout elapsedMs={stopwatch.ElapsedMilliseconds}");
				return $"Codex request timed out after {(int)askTimeout.TotalSeconds}s. You can retry, narrow the question, or reduce context.";
			}
			finally
			{
				try
				{
					if (!string.IsNullOrWhiteSpace(outputLastMessagePath) && File.Exists(outputLastMessagePath))
					{
						File.Delete(outputLastMessagePath);
					}
				}
				catch
				{
					// best effort temp-file cleanup
				}

				process?.Dispose();
			}
		}

		private static string TryReadOutputLastMessage(string? path)
		{
			if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
			{
				return string.Empty;
			}

			try
			{
				return File.ReadAllText(path, Encoding.UTF8).Trim();
			}
			catch
			{
				return string.Empty;
			}
		}

		private static string ExtractAssistantMessageFromTranscript(string output)
		{
			if (string.IsNullOrWhiteSpace(output))
			{
				return string.Empty;
			}

			var text = SanitizeCliOutput(output);
			if (!text.StartsWith("OpenAI Codex", StringComparison.OrdinalIgnoreCase))
			{
				return text;
			}

			var lines = text.Split('\n');
			var assistantLineIndex = -1;
			for (var i = lines.Length - 1; i >= 0; i--)
			{
				if (string.Equals(lines[i].Trim(), "assistant", StringComparison.OrdinalIgnoreCase))
				{
					assistantLineIndex = i;
					break;
				}
			}

			if (assistantLineIndex < 0 || assistantLineIndex >= lines.Length - 1)
			{
				return string.Empty;
			}

			var extracted = string.Join("\n", lines.Skip(assistantLineIndex + 1)).Trim();
			if (string.IsNullOrWhiteSpace(extracted))
			{
				return string.Empty;
			}

			var tokensUsedIndex = extracted.LastIndexOf("\ntokens used", StringComparison.OrdinalIgnoreCase);
			if (tokensUsedIndex >= 0)
			{
				extracted = extracted[..tokensUsedIndex].Trim();
			}

			return extracted;
		}

		private static string NormalizeCodexResponsePayload(string output)
		{
			if (string.IsNullOrWhiteSpace(output))
			{
				return string.Empty;
			}

			var text = SanitizeCliOutput(output);
			var schemaMessage = TryExtractSchemaMessage(text);
			if (!string.IsNullOrWhiteSpace(schemaMessage))
			{
				text = schemaMessage;
			}

			if (!LooksLikeCodexTranscript(text))
			{
				return text;
			}

			var assistantMessage = ExtractAssistantMessageFromTranscript(text);
			if (!string.IsNullOrWhiteSpace(assistantMessage))
			{
				return assistantMessage;
			}

			var toolCallBlock = TryExtractLastToolCallBlock(text);
			if (!string.IsNullOrWhiteSpace(toolCallBlock))
			{
				return toolCallBlock;
			}

			return string.Empty;
		}

		private static string TryExtractSchemaMessage(string text)
		{
			if (string.IsNullOrWhiteSpace(text))
			{
				return string.Empty;
			}

			if (!(text.StartsWith("{", StringComparison.Ordinal) && text.EndsWith("}", StringComparison.Ordinal)))
			{
				return string.Empty;
			}

			try
			{
				using var doc = JsonDocument.Parse(text);
				if (doc.RootElement.ValueKind == JsonValueKind.Object
					&& doc.RootElement.TryGetProperty("message", out var message)
					&& message.ValueKind == JsonValueKind.String)
				{
					return message.GetString() ?? string.Empty;
				}
			}
			catch
			{
				// not schema envelope
			}

			return string.Empty;
		}

		private static string TryExtractLastToolCallBlock(string transcript)
		{
			if (string.IsNullOrWhiteSpace(transcript))
			{
				return string.Empty;
			}

			var matches = Regex.Matches(transcript, "<tool_call>\\s*[\\s\\S]*?\\s*</tool_call>", RegexOptions.IgnoreCase);
			if (matches.Count == 0)
			{
				return string.Empty;
			}

			return matches[^1].Value.Trim();
		}

		private static string ExtractAssistantMessageFromJsonEvents(string stdout)
		{
			if (string.IsNullOrWhiteSpace(stdout))
			{
				return string.Empty;
			}

			string? lastCandidate = null;
			foreach (var rawLine in NormalizeLineEndings(stdout).Split('\n'))
			{
				var line = rawLine?.Trim();
				if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("{", StringComparison.Ordinal) || !line.EndsWith("}", StringComparison.Ordinal))
				{
					continue;
				}

				try
				{
					using var doc = JsonDocument.Parse(line);
					if (TryExtractAssistantTextFromJsonElement(doc.RootElement, out var text) && !string.IsNullOrWhiteSpace(text))
					{
						lastCandidate = text.Trim();
					}
				}
				catch
				{
					// ignore non-JSONL lines
				}
			}

			return lastCandidate ?? string.Empty;
		}

		private static bool TryExtractAssistantTextFromJsonElement(JsonElement element, out string text)
		{
			text = string.Empty;

			if (element.ValueKind == JsonValueKind.Object)
			{
				var role = TryGetObjectString(element, "role");
				if (string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase))
				{
					if (TryGetMessageText(element, out text))
					{
						return true;
					}
				}

				foreach (var prop in element.EnumerateObject())
				{
					if (TryExtractAssistantTextFromJsonElement(prop.Value, out text))
					{
						return true;
					}
				}
			}
			else if (element.ValueKind == JsonValueKind.Array)
			{
				foreach (var item in element.EnumerateArray())
				{
					if (TryExtractAssistantTextFromJsonElement(item, out text))
					{
						return true;
					}
				}
			}

			return false;
		}

		private static bool TryGetMessageText(JsonElement obj, out string text)
		{
			text = string.Empty;

			foreach (var field in new[] { "last_message", "message", "content", "output_text", "text", "value" })
			{
				if (!obj.TryGetProperty(field, out var value))
				{
					continue;
				}

				if (value.ValueKind == JsonValueKind.String)
				{
					text = value.GetString() ?? string.Empty;
					if (!string.IsNullOrWhiteSpace(text))
					{
						return true;
					}
				}

				if (value.ValueKind == JsonValueKind.Array)
				{
					var parts = new List<string>();
					foreach (var item in value.EnumerateArray())
					{
						if (item.ValueKind == JsonValueKind.String)
						{
							parts.Add(item.GetString() ?? string.Empty);
							continue;
						}

						if (item.ValueKind == JsonValueKind.Object)
						{
							if (item.TryGetProperty("text", out var textProp) && textProp.ValueKind == JsonValueKind.String)
							{
								parts.Add(textProp.GetString() ?? string.Empty);
								continue;
							}

							if (item.TryGetProperty("value", out var valueProp) && valueProp.ValueKind == JsonValueKind.String)
							{
								parts.Add(valueProp.GetString() ?? string.Empty);
								continue;
							}

							if (TryGetMessageText(item, out var nested))
							{
								parts.Add(nested);
							}
						}
					}

					text = string.Join("", parts.Where(part => !string.IsNullOrWhiteSpace(part))).Trim();
					if (!string.IsNullOrWhiteSpace(text))
					{
						return true;
					}
				}

				if (value.ValueKind == JsonValueKind.Object && TryGetMessageText(value, out text) && !string.IsNullOrWhiteSpace(text))
				{
					return true;
				}
			}

			return false;
		}

		private static string? TryGetObjectString(JsonElement obj, string propertyName)
		{
			if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(propertyName, out var prop) || prop.ValueKind != JsonValueKind.String)
			{
				return null;
			}

			return prop.GetString();
		}

		private static bool LooksLikeCodexTranscript(string output)
		{
			var text = SanitizeCliOutput(output);
			if (string.IsNullOrWhiteSpace(text))
			{
				return false;
			}

			var score = 0;
			if (text.Contains("OpenAI Codex", StringComparison.OrdinalIgnoreCase))
			{
				score += 2;
			}

			if (Regex.IsMatch(text, "(?im)^workdir:\\s+"))
			{
				score++;
			}

			if (Regex.IsMatch(text, "(?im)^model:\\s+"))
			{
				score++;
			}

			if (Regex.IsMatch(text, "(?im)^provider:\\s+"))
			{
				score++;
			}

			if (Regex.IsMatch(text, "(?im)^approval:\\s+"))
			{
				score++;
			}

			if (Regex.IsMatch(text, "(?im)^sandbox:\\s+"))
			{
				score++;
			}

			if (text.Contains("mcp startup:", StringComparison.OrdinalIgnoreCase))
			{
				score++;
			}

			if (text.Contains("tokens used", StringComparison.OrdinalIgnoreCase))
			{
				score++;
			}

			if (text.Contains("You are an autonomous ILSpy analysis agent.", StringComparison.OrdinalIgnoreCase))
			{
				score += 2;
			}

			return score >= 3;
		}

		private static bool ContainsRuntimeTranscriptMarkers(string output)
		{
			var text = SanitizeCliOutput(output);
			if (string.IsNullOrWhiteSpace(text))
			{
				return false;
			}

			var markers = 0;
			if (text.Contains("mcp startup:", StringComparison.OrdinalIgnoreCase))
			{
				markers++;
			}

			if (Regex.IsMatch(text, "(?im)^thinking\\s*$"))
			{
				markers++;
			}

			if (text.Contains("tokens used", StringComparison.OrdinalIgnoreCase))
			{
				markers++;
			}

			if (Regex.IsMatch(text, "(?im)^workdir:\\s+") && Regex.IsMatch(text, "(?im)^model:\\s+"))
			{
				markers++;
			}

			return markers >= 2;
		}

		private static string NormalizeLineEndings(string text)
		{
			return text.Replace("\r\n", "\n").Replace('\r', '\n');
		}

		private static string SanitizeCliOutput(string text)
		{
			if (string.IsNullOrWhiteSpace(text))
			{
				return string.Empty;
			}

			var noAnsi = Regex.Replace(text, "\\x1B\\[[0-9;?]*[ -/]*[@-~]", string.Empty);
			var noBom = noAnsi.Replace("\uFEFF", string.Empty).Replace("\0", string.Empty);
			return NormalizeLineEndings(noBom).Trim();
		}

		private static string GetCodexExecutable()
		{
			var value = Environment.GetEnvironmentVariable("ILSPY_CODEX_CLI");
			if (!string.IsNullOrWhiteSpace(value))
			{
				return value;
			}

			if (OperatingSystem.IsWindows())
			{
				if (TryResolveOnPath("codex", out var resolved))
				{
					return resolved;
				}

				var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
				if (!string.IsNullOrWhiteSpace(appData))
				{
					var npmGlobalCmd = Path.Combine(appData, "npm", "codex.cmd");
					if (File.Exists(npmGlobalCmd))
					{
						return npmGlobalCmd;
					}
				}
			}

			return "codex";
		}

		private static bool TryResolveOnPath(string command, out string resolvedPath)
		{
			resolvedPath = string.Empty;

			if (Path.IsPathRooted(command) && File.Exists(command))
			{
				resolvedPath = command;
				return true;
			}

			var path = Environment.GetEnvironmentVariable("PATH");
			if (string.IsNullOrWhiteSpace(path))
				return false;

			var hasExtension = Path.HasExtension(command);
			var suffixes = hasExtension
				? [string.Empty]
				: (OperatingSystem.IsWindows()
					? (Environment.GetEnvironmentVariable("PATHEXT")?.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
						?? [".COM", ".EXE", ".BAT", ".CMD"])
					: [string.Empty]);

			foreach (var segment in path.Split(Path.PathSeparator))
			{
				if (string.IsNullOrWhiteSpace(segment))
					continue;

				try
				{
					foreach (var suffix in suffixes)
					{
						var candidate = Path.Combine(segment.Trim(), command + suffix);
						if (!File.Exists(candidate))
							continue;

						resolvedPath = candidate;
						return true;
					}
				}
				catch
				{
					// ignore invalid path entries
				}
			}

			return false;
		}

		private static string BuildArguments(string? outputLastMessagePath)
		{
			var args = new List<string>();
			var extra = Environment.GetEnvironmentVariable("ILSPY_CODEX_ARGS");
			if (!string.IsNullOrWhiteSpace(extra))
			{
				args.AddRange(SplitArguments(extra));
			}

			args.Add("exec");
			args.Add("--skip-git-repo-check");
			args.Add("--json");
			args.Add("--color");
			args.Add("never");

			var schemaPath = EnsureOutputSchemaFile();
			if (!string.IsNullOrWhiteSpace(schemaPath))
			{
				args.Add("--output-schema");
				args.Add(schemaPath);
			}

			if (!string.IsNullOrWhiteSpace(outputLastMessagePath))
			{
				args.Add("-o");
				args.Add(outputLastMessagePath);
			}
			args.Add("-");

			return string.Join(" ", args.Select(QuoteArgument));
		}

		private static string EnsureOutputSchemaFile()
		{
			try
			{
				var schemaPath = Path.Combine(Path.GetTempPath(), "ilspy-codex-output-schema.json");
				const string schemaJson = "{\"$schema\":\"https://json-schema.org/draft/2020-12/schema\",\"type\":\"object\",\"properties\":{\"message\":{\"type\":\"string\"}},\"required\":[\"message\"],\"additionalProperties\":false}";
				if (!File.Exists(schemaPath) || !string.Equals(File.ReadAllText(schemaPath, Encoding.UTF8), schemaJson, StringComparison.Ordinal))
				{
					File.WriteAllText(schemaPath, schemaJson, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
				}

				return schemaPath;
			}
			catch
			{
				return string.Empty;
			}
		}

		private static IEnumerable<string> SplitArguments(string input)
		{
			if (string.IsNullOrWhiteSpace(input))
			{
				return [];
			}

			var result = new List<string>();
			var current = new StringBuilder();
			var inQuotes = false;

			foreach (var ch in input)
			{
				switch (ch)
				{
					case '"':
						inQuotes = !inQuotes;
						break;
					case ' ' when !inQuotes:
						if (current.Length > 0)
						{
							result.Add(current.ToString());
							current.Clear();
						}
						break;
					default:
						current.Append(ch);
						break;
				}
			}

			if (current.Length > 0)
			{
				result.Add(current.ToString());
			}

			return result.Where(token => !string.IsNullOrWhiteSpace(token));
		}

		private static string QuoteArgument(string argument)
		{
			if (string.IsNullOrEmpty(argument))
			{
				return "\"\"";
			}

			if (argument.IndexOfAny([' ', '"']) < 0)
			{
				return argument;
			}

			return "\"" + argument.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
		}
	}
}
