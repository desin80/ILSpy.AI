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
using System.Collections.ObjectModel;
using System.Composition;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;

using ICSharpCode.ILSpy.Commands;
using ICSharpCode.ILSpy.ViewModels;

namespace ICSharpCode.ILSpy.AIChat
{
	#nullable enable

	[ExportToolPane]
	[Shared]
	[Export]
	public sealed class AiChatPaneModel : ToolPaneModel
	{
		public const string PaneContentId = "aiChatPane";

		private readonly AiChatService aiChatService;
		private string? inputText = string.Empty;
		private bool isBusy;
		private CancellationTokenSource? runningCts;

		public AiChatPaneModel(AiChatService aiChatService)
		{
			this.aiChatService = aiChatService;
			ContentId = PaneContentId;
			Title = "AI Chat";
			Icon = "Images/Search";
			ShortcutKey = new KeyGesture(Key.Y, ModifierKeys.Control | ModifierKeys.Shift);
			IsCloseable = true;
			SubmitCommand = new DelegateCommand(Submit, () => !IsBusy);
			CancelCommand = new DelegateCommand(Cancel, () => IsBusy);
			ClearCommand = new DelegateCommand(Clear);
			Messages.Add(new AiChatMessage {
				Role = AiChatMessageRole.System,
				Text = "AI Chat ready. Type /help to see available commands.",
			});
		}

		public ObservableCollection<AiChatMessage> Messages { get; } = [];

		public string InputText {
			get => inputText ?? string.Empty;
			set {
				value ??= string.Empty;
				SetProperty(ref inputText, value);
			}
		}

		public bool IsBusy {
			get => isBusy;
			private set {
				if (SetProperty(ref isBusy, value))
				{
					CommandManager.InvalidateRequerySuggested();
				}
			}
		}

		public ICommand SubmitCommand { get; }

		public ICommand CancelCommand { get; }

		public ICommand ClearCommand { get; }

		private void Submit()
		{
			if (IsBusy)
				return;

			var text = InputText.Trim();
			if (string.IsNullOrEmpty(text))
				return;

			InputText = string.Empty;
			CommandManager.InvalidateRequerySuggested();
			Messages.Add(new AiChatMessage {
				Role = AiChatMessageRole.User,
				Text = text,
			});

			_ = ExecuteAsync(text);
		}

		private async Task ExecuteAsync(string input)
		{
			var previous = runningCts;
			runningCts?.Cancel();
			runningCts = new CancellationTokenSource();
			previous?.Dispose();

			IsBusy = true;
			try
			{
				var parsed = AiChatParsedCommand.Parse(input);
				var output = await aiChatService.ExecuteAsync(parsed, runningCts.Token);
				Messages.Add(new AiChatMessage {
					Role = AiChatMessageRole.Assistant,
					Text = output,
				});
			}
			catch (OperationCanceledException)
			{
				Messages.Add(new AiChatMessage {
					Role = AiChatMessageRole.System,
					Text = "Current request cancelled.",
				});
			}
			catch (System.Exception ex)
			{
				Messages.Add(new AiChatMessage {
					Role = AiChatMessageRole.System,
					Text = $"Error: {ex.Message}",
				});
			}
			finally
			{
				IsBusy = false;
			}
		}

		private void Cancel()
		{
			runningCts?.Cancel();
		}

		private void Clear()
		{
			Messages.Clear();
			Messages.Add(new AiChatMessage {
				Role = AiChatMessageRole.System,
				Text = "Chat cleared.",
			});
		}
	}
}
