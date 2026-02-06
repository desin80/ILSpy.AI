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
using System.Collections.Generic;
using System.Composition;
using System.Threading;
using System.Threading.Tasks;
using System.Text;
using System.Windows.Input;
using System.Linq;

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
		private readonly AiChatSettings aiChatSettings;
		private string? inputText = string.Empty;
		private bool isBusy;
		private CancellationTokenSource? runningCts;
		private long nextMessageId = 1;
		private int nextSessionId = 1;
		private AiChatSession? currentSession;
		private bool isHistoryOpen;
		private bool progressCarriageReturnPending;
		private bool suppressSessionSync;

		public AiChatPaneModel(AiChatService aiChatService, SettingsService settingsService)
		{
			this.aiChatService = aiChatService;
			this.settingsService = settingsService;
			aiChatSettings = settingsService.GetSettings<AiChatSettings>();
			ContentId = PaneContentId;
			Title = "AI Chat";
			Icon = "Images/Search";
			ShortcutKey = new KeyGesture(Key.Y, ModifierKeys.Control | ModifierKeys.Shift);
			IsCloseable = true;
			SubmitCommand = new DelegateCommand(Submit, () => !IsBusy);
			CancelCommand = new DelegateCommand(Cancel, () => IsBusy);
			ClearCommand = new DelegateCommand(Clear);
			NewSessionCommand = new DelegateCommand(NewSession, () => !IsBusy);
			DeleteSessionCommand = new DelegateCommand<AiChatSession?>(DeleteSession, session => !IsBusy && session != null);
			SelectSessionCommand = new DelegateCommand<AiChatSession?>(SelectSession, session => session != null);
			OpenHistoryCommand = new DelegateCommand(OpenHistory, () => !IsBusy);
			CloseHistoryCommand = new DelegateCommand(CloseHistory);

			RestoreOrCreateSessions();
		}

		public ObservableCollection<AiChatMessage> Messages { get; } = [];

		public ObservableCollection<AiChatSession> Sessions { get; } = [];

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

		public ICommand NewSessionCommand { get; }

		public ICommand DeleteSessionCommand { get; }

		public ICommand SelectSessionCommand { get; }

		public ICommand OpenHistoryCommand { get; }

		public ICommand CloseHistoryCommand { get; }

		public bool IsHistoryOpen {
			get => isHistoryOpen;
			set => SetProperty(ref isHistoryOpen, value);
		}

		public string CurrentSessionTitle => currentSession?.Title ?? "Session";

		public AiChatSession? SelectedSession {
			get => currentSession;
			set {
				if (!SetProperty(ref currentSession, value))
					return;

				if (currentSession != null)
				{
					LoadSessionMessages(currentSession);
					IsHistoryOpen = false;
					OnPropertyChanged(nameof(CurrentSessionTitle));
				}
			}
		}

		public bool SendOnEnter {
			get => aiChatSettings.SendOnEnter;
			set {
				if (aiChatSettings.SendOnEnter == value)
					return;

				aiChatSettings.SendOnEnter = value;
				OnPropertyChanged();
			}
		}

		public bool AutoModeEnabled {
			get => aiChatSettings.AutoModeEnabled;
			set {
				if (aiChatSettings.AutoModeEnabled == value)
					return;

				aiChatSettings.AutoModeEnabled = value;
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
			var userMessage = new AiChatMessage {
				Id = Interlocked.Increment(ref nextMessageId),
				Role = AiChatMessageRole.User,
				Text = text,
			};

			AppendMessage(userMessage);
			UpdateCurrentSessionTitleFromInput(text);

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
			progressCarriageReturnPending = false;
			AppendMessage(new AiChatMessage {
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

					AppendProgressUpdate(streamingBuilder, update, ref progressCarriageReturnPending);

					ReplaceMessage(assistantMessageId, AiChatMessageRole.Assistant, streamingBuilder.ToString());
				}).Task;
			}
			try
			{
				var parsed = AiChatParsedCommand.Parse(input);
				if (AutoModeEnabled && parsed.Kind is AiChatCommandKind.Prompt or AiChatCommandKind.Ask)
				{
					parsed = new AiChatParsedCommand {
						Kind = AiChatCommandKind.Auto,
						Prompt = parsed.Prompt,
					};
				}
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
			if (currentSession == null)
				return;

			currentSession.Messages.Clear();
			Messages.Clear();
			AppendMessage(new AiChatMessage {
				Id = Interlocked.Increment(ref nextMessageId),
				Role = AiChatMessageRole.System,
				Text = "Chat cleared.",
			});
			PersistSessions();
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
				AppendMessage(new AiChatMessage {
					Id = messageId,
					Role = role,
					Text = text,
				});
				PersistSessions();
				return;
			}

			var updated = new AiChatMessage {
				Id = messageId,
				Role = role,
				Text = text,
			};

			Messages[index] = updated;
			if (currentSession != null)
			{
				for (var i = 0; i < currentSession.Messages.Count; i++)
				{
					if (currentSession.Messages[i].Id == messageId)
					{
						currentSession.Messages[i] = updated;
						break;
					}
				}
			}
			PersistSessions();
		}

		private void NewSession()
		{
			if (IsBusy)
				return;

			var session = CreateNewSession();
			Sessions.Insert(0, session);
			SelectedSession = session;
			IsHistoryOpen = false;
			OnPropertyChanged(nameof(CurrentSessionTitle));
			PersistSessions();
		}

		private void DeleteSession(AiChatSession? session)
		{
			if (IsBusy || session == null)
				return;

			var index = Sessions.IndexOf(session);
			if (index < 0)
				return;

			if (Sessions.Count == 1)
			{
				Sessions.Clear();
				var fresh = CreateNewSession();
				Sessions.Add(fresh);
				SelectedSession = fresh;
				OnPropertyChanged(nameof(CurrentSessionTitle));
				PersistSessions();
				return;
			}

			var removedCurrent = ReferenceEquals(currentSession, session);
			Sessions.RemoveAt(index);

			if (!removedCurrent)
			{
				OnPropertyChanged(nameof(CurrentSessionTitle));
				PersistSessions();
				return;
			}

			var nextIndex = Math.Min(index, Sessions.Count - 1);
			SelectedSession = Sessions[nextIndex];
			OnPropertyChanged(nameof(CurrentSessionTitle));
			PersistSessions();
		}

		private void SelectSession(AiChatSession? session)
		{
			if (session == null)
				return;

			SelectedSession = session;
			PersistSessions();
		}

		private void OpenHistory()
		{
			if (IsBusy)
				return;

			IsHistoryOpen = true;
		}

		private void CloseHistory()
		{
			IsHistoryOpen = false;
		}

		private AiChatSession CreateNewSession()
		{
			var session = new AiChatSession {
				Id = nextSessionId++,
				Title = "New chat",
			};

			return session;
		}

		private void LoadSessionMessages(AiChatSession session)
		{
			suppressSessionSync = true;
			Messages.Clear();
			foreach (var message in session.Messages)
			{
				Messages.Add(message);
			}
			suppressSessionSync = false;
		}

		private void AppendMessage(AiChatMessage message)
		{
			Messages.Add(message);
			if (!suppressSessionSync)
			{
				currentSession?.Messages.Add(message);
			}

			if (!suppressSessionSync)
			{
				PersistSessions();
			}
		}

		private void UpdateCurrentSessionTitleFromInput(string input)
		{
			if (currentSession == null)
				return;

			var normalized = input.Replace("\r", " ").Replace("\n", " ").Trim();
			if (string.IsNullOrEmpty(normalized))
				return;

			var generated = normalized.Length <= 24 ? normalized : normalized.Substring(0, 24) + "...";
			if (string.Equals(currentSession.Title, "New chat", StringComparison.Ordinal))
			{
				if (string.Equals(currentSession.Title, generated, StringComparison.Ordinal))
					return;

				currentSession.Title = generated;
				var index = Sessions.IndexOf(currentSession);
				if (index >= 0)
				{
					OnPropertyChanged(nameof(CurrentSessionTitle));
					PersistSessions();
				}
			}
		}

		private void RestoreOrCreateSessions()
		{
			var persisted = aiChatSettings.LoadPersistedSessions()
				.Select(RemoveLegacyReadyMessage)
				.Where(ShouldPersistSession)
				.ToList();
			if (persisted.Count == 0)
			{
				var fresh = CreateNewSession();
				Sessions.Add(fresh);
				SelectedSession = fresh;
				PersistSessions();
				return;
			}

			foreach (var session in persisted)
			{
				Sessions.Add(session);
				nextSessionId = Math.Max(nextSessionId, session.Id + 1);
				foreach (var message in session.Messages)
				{
					nextMessageId = Math.Max(nextMessageId, message.Id + 1);
				}
			}

			var preferredId = aiChatSettings.LastActiveSessionId;
			var selected = Sessions.FirstOrDefault(session => session.Id == preferredId) ?? Sessions[0];
			SelectedSession = selected;
		}

		private void PersistSessions()
		{
			var persistedSessions = Sessions.Where(ShouldPersistSession).ToList();
			aiChatSettings.LastActiveSessionId = currentSession != null && ShouldPersistSession(currentSession)
				? currentSession.Id
				: persistedSessions.FirstOrDefault()?.Id ?? 0;
			aiChatSettings.SavePersistedSessions(persistedSessions);
		}

		private static bool ShouldPersistSession(AiChatSession session)
		{
			return session.Messages.Any(IsPersistableMessage);
		}

		private static AiChatSession RemoveLegacyReadyMessage(AiChatSession session)
		{
			session.Messages.RemoveAll(message =>
				message.Role == AiChatMessageRole.System
				&& string.Equals(message.Text, "AI Chat ready. Type /help to see available commands.", StringComparison.Ordinal));

			return session;
		}

		private static bool IsPersistableMessage(AiChatMessage message)
		{
			if (string.IsNullOrWhiteSpace(message.Text))
				return false;

			return message.Role == AiChatMessageRole.User || message.Role == AiChatMessageRole.Assistant;
		}

		private static void AppendProgressUpdate(StringBuilder target, string update, ref bool carriageReturnPending)
		{
			foreach (var ch in update)
			{
				if (ch == '\r')
				{
					carriageReturnPending = true;
					continue;
				}

				if (carriageReturnPending)
				{
					if (ch != '\n')
					{
						target.Length = FindCurrentLineStart(target);
					}

					carriageReturnPending = false;
				}

				target.Append(ch);
			}
		}

		private static int FindCurrentLineStart(StringBuilder buffer)
		{
			for (var i = buffer.Length - 1; i >= 0; i--)
			{
				if (buffer[i] == '\n')
				{
					return i + 1;
				}
			}

			return 0;
		}
	}
}
