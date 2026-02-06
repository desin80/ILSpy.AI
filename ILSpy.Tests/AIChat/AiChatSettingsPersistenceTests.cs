using System;
using System.Linq;

using ICSharpCode.ILSpy.AIChat;

using NUnit.Framework;

using Shouldly;

namespace ICSharpCode.ILSpy.Tests.AIChat
{
	[TestFixture]
	public class AiChatSettingsPersistenceTests
	{
		[Test]
		public void SaveAndLoadPersistedSessions_RoundTrips()
		{
			var settings = new AiChatSettings();
			settings.LastActiveSessionId = 12;

			var now = DateTime.UtcNow;
			var session = new AiChatSession {
				Id = 12,
				Title = "demo",
			};
			session.Messages.Add(new AiChatMessage {
				Id = 101,
				Role = AiChatMessageRole.User,
				Text = "hello",
				Timestamp = now,
			});

			settings.SavePersistedSessions([session]);
			var xml = settings.SaveToXml();

			var loaded = new AiChatSettings();
			loaded.LoadFromXml(xml);

			loaded.LastActiveSessionId.ShouldBe(12);
			var sessions = loaded.LoadPersistedSessions();
			sessions.Count.ShouldBe(1);
			sessions[0].Id.ShouldBe(12);
			sessions[0].Title.ShouldBe("demo");
			sessions[0].Messages.Count.ShouldBe(1);
			sessions[0].Messages.Single().Text.ShouldBe("hello");
			sessions[0].Messages.Single().Role.ShouldBe(AiChatMessageRole.User);
		}

		[Test]
		public void SavePersistedSessions_AllowsEmptyInput_WhenFilteredByModel()
		{
			var settings = new AiChatSettings();

			var emptySession = new AiChatSession {
				Id = 1,
				Title = "New chat",
			};

			var realSession = new AiChatSession {
				Id = 2,
				Title = "real",
			};
			realSession.Messages.Add(new AiChatMessage {
				Id = 10,
				Role = AiChatMessageRole.Assistant,
				Text = "done",
				Timestamp = DateTime.UtcNow,
			});

			settings.SavePersistedSessions([emptySession, realSession]);
			var xml = settings.SaveToXml();

			var loaded = new AiChatSettings();
			loaded.LoadFromXml(xml);

			var sessions = loaded.LoadPersistedSessions();
			sessions.Count.ShouldBe(2);
			sessions.Select(s => s.Id).ShouldContain(1);
			sessions.Select(s => s.Id).ShouldContain(2);
		}
	}
}
