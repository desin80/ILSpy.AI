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
using System.Threading;
using System.Threading.Tasks;

namespace ICSharpCode.ILSpy.AIChat
{
	#nullable enable

	[Export]
	[Shared]
	public sealed class AiChatCodexClient
	{
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

			var executable = GetCodexExecutable();
			var arguments = BuildArguments();

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

			using var process = new Process { StartInfo = psi };
			try
			{
				process.Start();
			}
			catch (Exception ex)
			{
				return $"Failed to start Codex CLI '{executable}': {ex.Message}";
			}

			await process.StandardInput.WriteAsync(prompt.AsMemory(), cancellationToken);
			await process.StandardInput.FlushAsync();
			process.StandardInput.Close();

			var outputBuilder = new StringBuilder();
			var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

			if (onPartialResponse == null)
			{
				outputBuilder.Append(await process.StandardOutput.ReadToEndAsync(cancellationToken));
			}
			else
			{
				char[] buffer = new char[1024];
				while (true)
				{
					var count = await process.StandardOutput.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
					if (count == 0)
					{
						break;
					}

					var chunk = new string(buffer, 0, count);
					outputBuilder.Append(chunk);
					await onPartialResponse(chunk);
				}
			}

			await process.WaitForExitAsync(cancellationToken);
			var error = (await errorTask)?.Trim() ?? string.Empty;
			var output = outputBuilder.ToString().Trim();

			if (process.ExitCode != 0)
			{
				return string.IsNullOrWhiteSpace(error)
					? $"Codex CLI failed with exit code {process.ExitCode}."
					: $"Codex CLI failed with exit code {process.ExitCode}.{Environment.NewLine}{error}";
			}

			if (!string.IsNullOrWhiteSpace(output))
			{
				return output;
			}

			return string.IsNullOrWhiteSpace(error) ? "Codex CLI returned no output." : error;
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

		private static string BuildArguments()
		{
			var args = new List<string>();
			var extra = Environment.GetEnvironmentVariable("ILSPY_CODEX_ARGS");
			if (!string.IsNullOrWhiteSpace(extra))
			{
				args.AddRange(SplitArguments(extra));
			}

			args.Add("exec");
			args.Add("--skip-git-repo-check");
			args.Add("-");

			return string.Join(" ", args.Select(QuoteArgument));
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
