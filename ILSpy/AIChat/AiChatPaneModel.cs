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
using System.Text;
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
		private readonly SettingsService settingsService;
		private string? inputText = string.Empty;
		private bool isBusy;
		private CancellationTokenSource? runningCts;
		private long nextMessageId = 1;

		public AiChatPaneModel(AiChatService aiChatService, SettingsService settingsService)
		{
			this.aiChatService = aiChatService;
			this.settingsService = settingsService;
			ContentId = PaneContentId;
			Title = "AI Chat";
			Icon = "Images/Search";
			ShortcutKey = new KeyGesture(Key.Y, ModifierKeys.Control | ModifierKeys.Shift);
			IsCloseable = true;
			SubmitCommand = new DelegateCommand(Submit, () => !IsBusy);
			CancelCommand = new DelegateCommand(Cancel, () => IsBusy);
			ClearCommand = new DelegateCommand(Clear);
			Messages.Add(new AiChatMessage {
				Id = Interlocked.Increment(ref nextMessageId),
				Role = AiChatMessageRole.System,
				Text = "AI Chat ready. Type /help to see available commands, or /auto <goal> for autonomous workflow.",
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

		public bool SendOnEnter {
			get => settingsService.GetSettings<AiChatSettings>().SendOnEnter;
			set {
				var settings = settingsService.GetSettings<AiChatSettings>();
				if (settings.SendOnEnter == value)
					return;

				settings.SendOnEnter = value;
				OnPropertyChanged();
			}
		}

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
				Id = Interlocked.Increment(ref nextMessageId),
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
			var assistantMessageId = Interlocked.Increment(ref nextMessageId);
			var streamingBuilder = new StringBuilder();
			Messages.Add(new AiChatMessage {
				Id = assistantMessageId,
				Role = AiChatMessageRole.Assistant,
				Text = "[status] Working...",
			});

			Task ReportProgressAsync(string update)
			{
				return App.Current.Dispatcher.InvokeAsync(() =>
				{
					if (string.IsNullOrEmpty(update))
						return;

					streamingBuilder.Append(update);
					ReplaceMessage(assistantMessageId, AiChatMessageRole.Assistant, streamingBuilder.ToString());
				}).Task;
			}
			try
			{
				var parsed = AiChatParsedCommand.Parse(input);
				var output = await aiChatService.ExecuteAsync(parsed, ReportProgressAsync, runningCts.Token);
				if (!string.IsNullOrWhiteSpace(output))
				{
					ReplaceMessage(assistantMessageId, AiChatMessageRole.Assistant, output);
				}
			}
			catch (OperationCanceledException)
			{
				ReplaceMessage(assistantMessageId, AiChatMessageRole.System, "Current request cancelled.");
			}
			catch (System.Exception ex)
			{
				ReplaceMessage(assistantMessageId, AiChatMessageRole.System, $"Error: {ex.Message}");
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
				Id = Interlocked.Increment(ref nextMessageId),
				Role = AiChatMessageRole.System,
				Text = "Chat cleared.",
			});
		}

		private void ReplaceMessage(long messageId, AiChatMessageRole role, string text)
		{
			var index = -1;
			for (var i = 0; i < Messages.Count; i++)
			{
				if (Messages[i].Id == messageId)
				{
					index = i;
					break;
				}
			}

			if (index < 0)
			{
				Messages.Add(new AiChatMessage {
					Id = messageId,
					Role = role,
					Text = text,
				});
				return;
			}

			Messages[index] = new AiChatMessage {
				Id = messageId,
				Role = role,
				Text = text,
			};
		}
	}
}
