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
using System.Globalization;
using System.Linq;
using System.Xml.Linq;

using ICSharpCode.ILSpyX.Settings;

using TomsToolbox.Wpf;

namespace ICSharpCode.ILSpy.AIChat
{
	#nullable enable

	public sealed class AiChatSettings : ObservableObjectBase, ISettingsSection
	{
		private AiChatProviderKind provider = AiChatProviderKind.CodexCli;
		private string? openAIBaseUrl = "https://api.openai.com/v1";
		private string? openAIModel = "gpt-4.1-mini";
		private string? openAIApiKey = string.Empty;
		private string? codexCliPath = "codex";
		private string? codexCliArguments = string.Empty;
		private bool sendOnEnter;
		private bool autoModeEnabled;
		private XElement sessionsElement = new("Sessions");
		private int historyStateVersion;
		private int lastActiveSessionId;

		public AiChatProviderKind Provider {
			get => provider;
			set => SetProperty(ref provider, value);
		}

		public string OpenAIBaseUrl {
			get => openAIBaseUrl ?? string.Empty;
			set {
				value ??= string.Empty;
				SetProperty(ref openAIBaseUrl, value);
			}
		}

		public string OpenAIModel {
			get => openAIModel ?? string.Empty;
			set {
				value ??= string.Empty;
				SetProperty(ref openAIModel, value);
			}
		}

		public string OpenAIApiKey {
			get => openAIApiKey ?? string.Empty;
			set {
				value ??= string.Empty;
				SetProperty(ref openAIApiKey, value);
			}
		}

		public string CodexCliPath {
			get => codexCliPath ?? string.Empty;
			set {
				value ??= string.Empty;
				SetProperty(ref codexCliPath, value);
			}
		}

		public string CodexCliArguments {
			get => codexCliArguments ?? string.Empty;
			set {
				value ??= string.Empty;
				SetProperty(ref codexCliArguments, value);
			}
		}

		public bool SendOnEnter {
			get => sendOnEnter;
			set => SetProperty(ref sendOnEnter, value);
		}

		public bool AutoModeEnabled {
			get => autoModeEnabled;
			set => SetProperty(ref autoModeEnabled, value);
		}

		public int LastActiveSessionId {
			get => lastActiveSessionId;
			set => SetProperty(ref lastActiveSessionId, value);
		}

		public XName SectionName => "AiChatSettings";

		public void LoadFromXml(XElement section)
		{
			SendOnEnter = (bool?)section.Attribute(nameof(SendOnEnter)) ?? false;
			AutoModeEnabled = (bool?)section.Attribute(nameof(AutoModeEnabled)) ?? false;
			LastActiveSessionId = (int?)section.Attribute(nameof(LastActiveSessionId)) ?? 0;
			sessionsElement = section.Element("Sessions") is XElement sessions
				? new XElement(sessions)
				: new XElement("Sessions");
		}

		public XElement SaveToXml()
		{
			var section = new XElement(SectionName);
			section.SetAttributeValue(nameof(SendOnEnter), SendOnEnter);
			section.SetAttributeValue(nameof(AutoModeEnabled), AutoModeEnabled);
			section.SetAttributeValue(nameof(LastActiveSessionId), LastActiveSessionId);
			section.Add(new XElement(sessionsElement));
			return section;
		}

		public IReadOnlyList<AiChatSession> LoadPersistedSessions()
		{
			var result = new List<AiChatSession>();
			foreach (var sessionElement in sessionsElement.Elements("Session"))
			{
				var id = (int?)sessionElement.Attribute("Id") ?? 0;
				var title = (string?)sessionElement.Attribute("Title") ?? "New chat";

				var session = new AiChatSession {
					Id = id,
					Title = title,
				};

				foreach (var messageElement in sessionElement.Elements("Message"))
				{
					var messageId = (long?)messageElement.Attribute("Id") ?? 0;
					var roleText = (string?)messageElement.Attribute("Role");
					if (!Enum.TryParse(roleText, out AiChatMessageRole role))
						role = AiChatMessageRole.Assistant;

					var timestampText = (string?)messageElement.Attribute("Timestamp") ?? string.Empty;
					var timestamp = DateTime.Now;
					if (DateTime.TryParseExact(timestampText, "o", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
						timestamp = parsed;

					var text = messageElement.Element("Text")?.Value ?? string.Empty;
					session.Messages.Add(new AiChatMessage {
						Id = messageId,
						Role = role,
						Text = text,
						Timestamp = timestamp,
					});
				}

				result.Add(session);
			}

			return result;
		}

		public void SavePersistedSessions(IEnumerable<AiChatSession> sessions)
		{
			sessionsElement = new XElement("Sessions",
				sessions.Select(session =>
					new XElement("Session",
						new XAttribute("Id", session.Id),
						new XAttribute("Title", session.Title ?? "New chat"),
						session.Messages.Select(message =>
							new XElement("Message",
								new XAttribute("Id", message.Id),
								new XAttribute("Role", message.Role),
								new XAttribute("Timestamp", message.Timestamp.ToString("o", CultureInfo.InvariantCulture)),
								new XElement("Text", message.Text ?? string.Empty))))));

			HistoryStateVersion++;
		}

		private int HistoryStateVersion {
			get => historyStateVersion;
			set => SetProperty(ref historyStateVersion, value);
		}
	}
}
