using System.Threading;

using ICSharpCode.ILSpy.AIChat;
using ICSharpCode.ILSpyX.Search;

using NUnit.Framework;

using Shouldly;

namespace ICSharpCode.ILSpy.Tests.AIChat
{
	#nullable enable

	[TestFixture]
	[Apartment(ApartmentState.STA)]
	public class AiChatLinkNavigatorTests
	{
		[Test]
		public void Navigate_ExternalLink_UsesExternalHandler()
		{
			var navigator = new AiChatLinkNavigator();
			string? opened = null;
			navigator.OpenExternalLink = link => opened = link;

			var result = navigator.Navigate(AiChatLinkReference.CreateExternal("https://example.com"));

			result.Success.ShouldBeTrue();
			opened.ShouldBe("https://example.com");
		}

		[Test]
		public void Navigate_DottedSymbol_Unique_Navigates()
		{
			var navigator = new AiChatLinkNavigator();
			object? navigated = null;
			var scrolledLine = 0;
			navigator.SearchOverride = (_, _, _) => [
				new FakeSearchResult {
					Name = "ExecuteAsync",
					Location = "ICSharpCode.ILSpy.AIChat.AiChatToolDispatcher",
					Assembly = "ILSpy",
					Fitness = 2,
					Image = new object(),
					LocationImage = new object(),
					AssemblyImage = new object(),
					ReferenceObject = new object(),
				},
			];
			navigator.NavigateReferenceOverride = reference => navigated = reference;
			navigator.ScrollToLineOverride = line => {
				scrolledLine = line;
				return true;
			};

			var result = navigator.Navigate(AiChatLinkReference.CreateSymbol("ICSharpCode.ILSpy.AIChat.AiChatToolDispatcher.ExecuteAsync", 56));

			result.Success.ShouldBeTrue();
			navigated.ShouldNotBeNull();
			scrolledLine.ShouldBe(56);
		}

		[Test]
		public void Navigate_QualifiedSymbol_PrefersExactContainer()
		{
			var navigator = new AiChatLinkNavigator();
			object? navigated = null;
			navigator.SearchOverride = (_, _, _) => [
				new FakeSearchResult {
					Name = "ExecuteAsync",
					Location = "ICSharpCode.ILSpy.AIChat.AiChatService",
					Assembly = "ILSpy",
					Fitness = 1,
					Image = new object(),
					LocationImage = new object(),
					AssemblyImage = new object(),
					ReferenceObject = "service",
				},
				new FakeSearchResult {
					Name = "ExecuteAsync",
					Location = "ICSharpCode.ILSpy.AIChat.AiChatToolDispatcher",
					Assembly = "ILSpy",
					Fitness = 1,
					Image = new object(),
					LocationImage = new object(),
					AssemblyImage = new object(),
					ReferenceObject = "dispatcher",
				},
			];
			navigator.NavigateReferenceOverride = reference => navigated = reference;

			var result = navigator.Navigate(AiChatLinkReference.CreateSymbol("ICSharpCode.ILSpy.AIChat.AiChatToolDispatcher.ExecuteAsync"));

			result.Success.ShouldBeTrue();
			result.Candidates.Count.ShouldBe(0);
			navigated.ShouldBe("dispatcher");
		}

		[Test]
		public void Navigate_DottedSymbol_Ambiguous_ReturnsCandidates()
		{
			var navigator = new AiChatLinkNavigator {
				SearchOverride = (_, _, _) => [
					new FakeSearchResult {
						Name = "ExecuteAsync",
						Location = "ICSharpCode.ILSpy.AIChat.AiChatToolDispatcher",
						Assembly = "ILSpy",
						Fitness = 1,
						Image = new object(),
						LocationImage = new object(),
						AssemblyImage = new object(),
						ReferenceObject = "ref1",
					},
					new FakeSearchResult {
						Name = "ExecuteAsync",
						Location = "ICSharpCode.ILSpy.AIChat.AiChatService",
						Assembly = "ILSpy",
						Fitness = 1,
						Image = new object(),
						LocationImage = new object(),
						AssemblyImage = new object(),
						ReferenceObject = "ref2",
					},
				],
			};

			var result = navigator.Navigate(AiChatLinkReference.CreateSymbol("ExecuteAsync"));

			result.Success.ShouldBeFalse();
			result.Candidates.Count.ShouldBeGreaterThan(1);
			result.Message.ShouldContain("Multiple symbols matched");
			result.Candidates[0].Reference.ShouldNotBeNull();
			result.Candidates[0].DisplayText.ShouldContain("ExecuteAsync");
		}

		private sealed class FakeSearchResult : SearchResult
		{
			public object? ReferenceObject { get; set; }
			public override object? Reference => ReferenceObject;
		}
	}
}
