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

using System.Composition;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Windows.Threading;
using System.Windows.Controls;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;
using System;

using TomsToolbox.Wpf.Composition.AttributedModel;

namespace ICSharpCode.ILSpy.AIChat
{
	#nullable enable

	[DataTemplate(typeof(AiChatPaneModel))]
	[NonShared]
	public partial class AiChatPane
	{
		private const double StepDetailsMaxHeight = 300;
		private static readonly string[] WaitingDots = [".", "..", "..."];
		private static readonly Dictionary<string, string> FriendlyToolNames = new(StringComparer.OrdinalIgnoreCase) {
			["assemblies"] = "List loaded assemblies",
			["selected"] = "Read current selection",
			["decompile"] = "Decompile current selection",
			["read_selected_window"] = "Read selected code window",
			["search"] = "Search symbols",
			["search_many"] = "Batch symbol search",
			["decompile_many"] = "Batch decompile targets",
			["read_result_window"] = "Read search result window",
			["open_result"] = "Open search result",
			["analyze"] = "Run analyzer",
		};

		private AiChatPaneModel? observedModel;
		private readonly Dictionary<string, bool> stepExpansionStates = new();
		private readonly Dictionary<TextBlock, string> animatedPlanningHeaders = new();
		private DispatcherTimer? waitingAnimationTimer;
		private int waitingAnimationFrame;

		public AiChatPane()
		{
			InitializeComponent();
			DataContextChanged += AiChatPane_DataContextChanged;
			Unloaded += AiChatPane_Unloaded;
		}

		private void AiChatPane_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
		{
			if (observedModel != null)
			{
				observedModel.Messages.CollectionChanged -= Messages_CollectionChanged;
				observedModel.PropertyChanged -= ObservedModel_PropertyChanged;
			}

			observedModel = e.NewValue as AiChatPaneModel;
			if (observedModel != null)
			{
				observedModel.Messages.CollectionChanged += Messages_CollectionChanged;
				observedModel.PropertyChanged += ObservedModel_PropertyChanged;
				RenderMessages();
				ScrollToEnd();
			}
		}

		private void ObservedModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
		{
			if (e.PropertyName == nameof(AiChatPaneModel.IsBusy))
			{
				UpdateWaitingAnimationState();
			}
		}

		private void Messages_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
		{
			RenderMessages();
			ScrollToEnd();
		}

		private void TranscriptBox_Loaded(object sender, RoutedEventArgs e)
		{
			RenderMessages();
			ScrollToEnd();
		}

		private void AiChatPane_Unloaded(object sender, RoutedEventArgs e)
		{
			StopWaitingAnimation();
		}

		private void RenderMessages()
		{
			if (observedModel == null)
				return;

			animatedPlanningHeaders.Clear();

			var document = new FlowDocument {
				PagePadding = new Thickness(0),
			};
			foreach (var message in observedModel.Messages)
			{
				if (TryCreateAutoStepBlocks(message, out var blocks))
				{
					foreach (var block in blocks)
					{
						document.Blocks.Add(block);
					}
				}
				else
				{
					document.Blocks.Add(CreateParagraph(message));
				}
			}

			TranscriptBox.Document = document;
			UpdateWaitingAnimationState();
		}

		private static Paragraph CreateParagraph(AiChatMessage message)
		{
			var isDark = IsDarkTheme();
			Brush foreground;
			Brush subtle;
			switch (message.Role)
			{
				case AiChatMessageRole.User:
					foreground = isDark ? new SolidColorBrush(Color.FromRgb(122, 194, 255)) : new SolidColorBrush(Color.FromRgb(0, 102, 204));
					subtle = isDark ? new SolidColorBrush(Color.FromRgb(90, 120, 145)) : new SolidColorBrush(Color.FromRgb(126, 147, 169));
					break;
				case AiChatMessageRole.System:
					foreground = isDark ? new SolidColorBrush(Color.FromRgb(255, 210, 120)) : new SolidColorBrush(Color.FromRgb(148, 94, 0));
					subtle = isDark ? new SolidColorBrush(Color.FromRgb(135, 110, 80)) : new SolidColorBrush(Color.FromRgb(163, 134, 98));
					break;
				default:
					foreground = isDark ? new SolidColorBrush(Color.FromRgb(224, 224, 224)) : new SolidColorBrush(Color.FromRgb(28, 28, 28));
					subtle = isDark ? new SolidColorBrush(Color.FromRgb(130, 130, 130)) : new SolidColorBrush(Color.FromRgb(134, 134, 134));
					break;
			}

			var paragraph = new Paragraph {
				Margin = new Thickness(2, 2, 2, 8),
				LineHeight = 18,
				TextAlignment = TextAlignment.Left,
			};

			paragraph.Inlines.Add(new Run($"[{message.Timestamp:HH:mm:ss}] ") {
				Foreground = subtle,
			});
			paragraph.Inlines.Add(new Run($"{GetRoleText(message.Role)}: ") {
				Foreground = foreground,
				FontWeight = FontWeights.Normal,
			});
			paragraph.Inlines.Add(new Run(message.Text ?? string.Empty) {
				Foreground = foreground,
			});

			ApplySemanticStyling(paragraph, message);

			return paragraph;
		}

		private bool TryCreateAutoStepBlocks(AiChatMessage message, out IReadOnlyList<Block> blocks)
		{
			blocks = [];
			if (message.Role != AiChatMessageRole.Assistant || string.IsNullOrWhiteSpace(message.Text))
				return false;

			if (!message.Text.Contains("=== Step", StringComparison.OrdinalIgnoreCase))
				return false;

			if (!TryParseAutoProgress(message.Text, out var parsed))
				return false;

			var result = new List<Block>();
			if (!string.IsNullOrWhiteSpace(parsed.Prefix))
			{
				result.Add(CreateParagraph(new AiChatMessage {
					Id = message.Id,
					Role = message.Role,
					Timestamp = message.Timestamp,
					Text = parsed.Prefix.Trim(),
				}));
			}

			for (var i = 0; i < parsed.Steps.Count; i++)
			{
				var step = parsed.Steps[i];
				var card = BuildStepCard(message.Id, step, false);
				result.Add(new BlockUIContainer(card) {
					Margin = new Thickness(2, 2, 4, 8),
				});
			}

			blocks = result;
			return result.Count > 0;
		}

		private UIElement BuildStepCard(long messageId, AutoStepRender step, bool expandByDefault)
		{
			var borderBrush = GetThemeBrush(SystemColors.ControlDarkBrushKey, Color.FromRgb(126, 126, 126));
			var cardBackground = GetThemeBrush(SystemColors.ControlLightBrushKey, Color.FromRgb(242, 242, 242));
			var headerForeground = GetThemeBrush(SystemColors.WindowTextBrushKey, Color.FromRgb(32, 32, 32));
			var stepKey = GetStepKey(messageId, step.StepNumber);
			var isExpanded = stepExpansionStates.TryGetValue(stepKey, out var saved)
				? saved
				: expandByDefault;
			var collapsedGlyph = Geometry.Parse("M 0 0 L 0 8 L 6 4 Z");
			var expandedGlyph = Geometry.Parse("M 0 0 L 8 0 L 4 6 Z");

			var content = BuildStepCardContent(step);
			content.Visibility = isExpanded ? Visibility.Visible : Visibility.Collapsed;

			var glyph = new Path {
				Data = isExpanded ? expandedGlyph : collapsedGlyph,
				Fill = headerForeground,
				Stretch = Stretch.Fill,
				Width = 8,
				Height = 8,
				VerticalAlignment = VerticalAlignment.Center,
				Margin = new Thickness(0, 0, 6, 0),
			};

			var headerText = new TextBlock {
				Text = BuildStepHeader(step),
				FontWeight = FontWeights.Normal,
				Foreground = headerForeground,
				TextWrapping = TextWrapping.Wrap,
				FontSize = 12,
				VerticalAlignment = VerticalAlignment.Center,
			};
			if (string.IsNullOrWhiteSpace(step.ToolCall)
				&& string.IsNullOrWhiteSpace(step.ToolResult)
				&& string.IsNullOrWhiteSpace(step.Other)
				&& headerText.Text.EndsWith("planning...", StringComparison.OrdinalIgnoreCase))
			{
				var baseText = headerText.Text.TrimEnd('.');
				headerText.Text = baseText + WaitingDots[waitingAnimationFrame % WaitingDots.Length];
				animatedPlanningHeaders[headerText] = baseText;
			}

			var headerPanel = new DockPanel {
				LastChildFill = true,
			};
			DockPanel.SetDock(glyph, Dock.Left);
			headerPanel.Children.Add(glyph);
			headerPanel.Children.Add(headerText);

			var headerButton = new Button {
				Content = headerPanel,
				BorderThickness = new Thickness(0),
				Background = Brushes.Transparent,
				Padding = new Thickness(0),
				HorizontalContentAlignment = HorizontalAlignment.Left,
				HorizontalAlignment = HorizontalAlignment.Stretch,
				Cursor = Cursors.Hand,
				Focusable = false,
				ClickMode = ClickMode.Press,
			};

			void SetExpanded(bool expanded)
			{
				content.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
				glyph.Data = expanded ? expandedGlyph : collapsedGlyph;
				stepExpansionStates[stepKey] = expanded;
			}

			var lastToggleUtc = DateTime.MinValue;
			headerButton.Click += (_, _) =>
			{
				var now = DateTime.UtcNow;
				if ((now - lastToggleUtc).TotalMilliseconds < 180)
					return;

				lastToggleUtc = now;
				SetExpanded(content.Visibility != Visibility.Visible);
			};

			var container = new StackPanel();
			container.Children.Add(headerButton);
			container.Children.Add(content);

			return new Border {
				BorderBrush = borderBrush,
				BorderThickness = new Thickness(1),
				CornerRadius = new CornerRadius(0),
				Background = cardBackground,
				HorizontalAlignment = HorizontalAlignment.Stretch,
				Margin = new Thickness(0, 0, 1, 0),
				Padding = new Thickness(8, 3, 8, 3),
				Child = container,
			};
		}

		private static string GetStepKey(long messageId, int stepNumber)
		{
			return messageId + ":" + stepNumber;
		}

		private UIElement BuildStepCardContent(AutoStepRender step)
		{
			var panel = new StackPanel {
				Margin = new Thickness(4, 4, 0, 0),
			};

			if (string.Equals(step.Planning.Trim(), "Planning next action...", StringComparison.OrdinalIgnoreCase))
			{
				step.Planning = string.Empty;
			}
			AddStepSection(panel, "Planning", step.Planning, false);
			AddStepSection(panel, "Thinking", step.Thinking, false);
			AddStepSection(panel, "Action", HumanizeToolCall(step.ToolCall), false);
			AddStepSection(panel, "Tool result", step.ToolResult, true);
			AddStepSection(panel, "Notes", step.Other, false);

			if (panel.Children.Count > 0 && panel.Children[panel.Children.Count - 1] is FrameworkElement lastSection)
			{
				var margin = lastSection.Margin;
				lastSection.Margin = new Thickness(margin.Left, margin.Top, margin.Right, 0);
			}

			return panel;
		}

		private void AddStepSection(Panel host, string title, string content, bool large)
		{
			if (string.IsNullOrWhiteSpace(content))
				return;

			var titleBrush = GetThemeBrush(SystemColors.ControlTextBrushKey, Color.FromRgb(38, 38, 38));
			var bodyBrush = GetThemeBrush(SystemColors.WindowTextBrushKey, Color.FromRgb(28, 28, 28));

			host.Children.Add(new TextBlock {
				Text = title,
				FontWeight = FontWeights.Normal,
				Foreground = titleBrush,
				Margin = new Thickness(1, 0, 0, 2),
				FontSize = 11,
			});

			var editor = new TextBox {
				Text = content.TrimEnd(),
				IsReadOnly = true,
				AcceptsReturn = true,
				TextWrapping = TextWrapping.Wrap,
				VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
				HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
				Background = Brushes.Transparent,
				BorderThickness = new Thickness(0),
				Padding = new Thickness(0),
				Foreground = bodyBrush,
				Margin = new Thickness(4, 0, 0, 8),
				FontSize = 12,
			};

			if (large)
			{
				editor.MaxHeight = StepDetailsMaxHeight;
			}

			host.Children.Add(editor);
		}

		private static string BuildStepHeader(AutoStepRender step)
		{
			var summary = !string.IsNullOrWhiteSpace(step.ToolCall)
				? HumanizeToolCall(step.ToolCall)
				: (!string.IsNullOrWhiteSpace(step.Thinking) ? ToSingleLine(step.Thinking, 72) : "planning...");

			return $"Step {step.StepNumber} - {ToSingleLine(summary, 96)}";
		}

		private static string HumanizeToolCall(string toolCall)
		{
			if (string.IsNullOrWhiteSpace(toolCall))
			{
				return string.Empty;
			}

			var trimmed = toolCall.Trim();
			var separatorIndex = trimmed.IndexOfAny([' ', '(']);
			var toolName = separatorIndex >= 0 ? trimmed[..separatorIndex] : trimmed;
			if (FriendlyToolNames.TryGetValue(toolName, out var friendlyName))
			{
				return friendlyName;
			}

			return HumanizeFallbackToolName(toolName);
		}

		private static string HumanizeFallbackToolName(string toolName)
		{
			var normalized = toolName.Replace('_', ' ').Replace('-', ' ').Trim();
			if (string.IsNullOrWhiteSpace(normalized))
			{
				return toolName;
			}

			return char.ToUpperInvariant(normalized[0]) + normalized[1..];
		}

		private static bool TryParseAutoProgress(string text, out AutoProgressParseResult parsed)
		{
			parsed = new AutoProgressParseResult();
			var lines = text.Replace("\r\n", "\n").Split('\n');
			AutoStepRender? currentStep = null;
			var beforeSteps = new List<string>();
			var collectingToolResult = false;

			foreach (var raw in lines)
			{
				var line = raw.TrimEnd('\r');
				if (TryParseStepNumber(line, out var stepNumber))
				{
					currentStep = new AutoStepRender(stepNumber);
					parsed.Steps.Add(currentStep);
					collectingToolResult = false;
					continue;
				}

				if (currentStep == null)
				{
					if (!string.IsNullOrWhiteSpace(line))
					{
						beforeSteps.Add(line);
					}
					continue;
				}

				if (line.StartsWith("Planning next action", StringComparison.OrdinalIgnoreCase))
				{
					currentStep.Planning = line;
					collectingToolResult = false;
					continue;
				}

				if (line.StartsWith("Thinking:", StringComparison.OrdinalIgnoreCase))
				{
					currentStep.Thinking = line["Thinking:".Length..].Trim();
					collectingToolResult = false;
					continue;
				}

				if (line.StartsWith("Tool call:", StringComparison.OrdinalIgnoreCase))
				{
					currentStep.ToolCall = line["Tool call:".Length..].Trim();
					collectingToolResult = false;
					continue;
				}

				if (line.StartsWith("Tool result", StringComparison.OrdinalIgnoreCase))
				{
					var colon = line.IndexOf(':');
					currentStep.ToolResult = colon >= 0 && colon + 1 < line.Length
						? line[(colon + 1)..].Trim()
						: string.Empty;
					collectingToolResult = true;
					continue;
				}

				if (line.StartsWith("Final answer generated", StringComparison.OrdinalIgnoreCase))
				{
					currentStep.Other = AppendLine(currentStep.Other, line);
					collectingToolResult = false;
					continue;
				}

				if (collectingToolResult)
				{
					currentStep.ToolResult = AppendLine(currentStep.ToolResult, line);
				}
				else if (!string.IsNullOrWhiteSpace(line))
				{
					currentStep.Other = AppendLine(currentStep.Other, line);
				}
				else if (collectingToolResult)
				{
					currentStep.ToolResult = AppendLine(currentStep.ToolResult, string.Empty);
				}

			}

			if (parsed.Steps.Count == 0)
			{
				return false;
			}

			if (beforeSteps.Count > 0)
			{
				parsed.Prefix = string.Join(Environment.NewLine, beforeSteps);
			}

			return true;
		}

		private static bool TryParseStepNumber(string line, out int stepNumber)
		{
			stepNumber = 0;
			if (string.IsNullOrWhiteSpace(line))
				return false;

			var trimmed = line.Trim();
			if (!trimmed.StartsWith("=== Step ", StringComparison.OrdinalIgnoreCase))
				return false;

			var numberPart = trimmed["=== Step ".Length..].Trim();
			var end = numberPart.IndexOf(' ');
			if (end >= 0)
			{
				numberPart = numberPart[..end];
			}

			numberPart = numberPart.TrimEnd('=', ' ');
			return int.TryParse(numberPart, out stepNumber);
		}

		private static string AppendLine(string buffer, string line)
		{
			if (string.IsNullOrEmpty(buffer))
			{
				return line;
			}

			return buffer + Environment.NewLine + line;
		}

		private static void ApplySemanticStyling(Paragraph paragraph, AiChatMessage message)
		{
			if (string.IsNullOrWhiteSpace(message.Text))
				return;

			var isDark = IsDarkTheme();

			var text = message.Text;
			if (text.StartsWith("=== Step", StringComparison.OrdinalIgnoreCase))
			{
				paragraph.Foreground = isDark
					? new SolidColorBrush(Color.FromRgb(255, 215, 135))
					: new SolidColorBrush(Color.FromRgb(144, 86, 0));
				paragraph.FontWeight = FontWeights.Normal;
			}
			else if (text.Contains("Planning next action", StringComparison.OrdinalIgnoreCase))
			{
				paragraph.Foreground = isDark
					? new SolidColorBrush(Color.FromRgb(178, 207, 255))
					: new SolidColorBrush(Color.FromRgb(22, 86, 156));
			}
			else if (text.Contains("Thinking:", StringComparison.OrdinalIgnoreCase))
			{
				paragraph.Foreground = isDark
					? new SolidColorBrush(Color.FromRgb(133, 195, 255))
					: new SolidColorBrush(Color.FromRgb(0, 89, 163));
			}
			else if (text.Contains("Tool call:", StringComparison.OrdinalIgnoreCase) || text.Contains("Tool result:", StringComparison.OrdinalIgnoreCase))
			{
				paragraph.Foreground = isDark
					? new SolidColorBrush(Color.FromRgb(148, 220, 140))
					: new SolidColorBrush(Color.FromRgb(28, 112, 59));
				if (text.Contains("Tool call:", StringComparison.OrdinalIgnoreCase))
				{
					paragraph.FontWeight = FontWeights.Normal;
				}
			}
			else if (message.Role == AiChatMessageRole.Assistant)
			{
				paragraph.Foreground = isDark
					? new SolidColorBrush(Color.FromRgb(230, 230, 230))
					: new SolidColorBrush(Color.FromRgb(28, 28, 28));
			}
		}

		private static string GetRoleText(AiChatMessageRole role)
		{
			return role switch
			{
				AiChatMessageRole.User => "You",
				AiChatMessageRole.System => "System",
				_ => "AI",
			};
		}

		private static bool IsDarkTheme()
		{
			if (Application.Current?.Resources[SystemColors.WindowBrushKey] is SolidColorBrush brush)
			{
				var c = brush.Color;
				return c.R + c.G + c.B < 384;
			}

			return false;
		}

		private Brush GetThemeBrush(object resourceKey, Color fallback)
		{
			if (Application.Current?.Resources[resourceKey] is Brush brush)
			{
				return brush;
			}

			return new SolidColorBrush(fallback);
		}

		private static string ToSingleLine(string text, int maxLength)
		{
			if (string.IsNullOrWhiteSpace(text))
				return string.Empty;

			var normalized = text.Replace("\r", " ").Replace("\n", " ").Trim();
			if (normalized.Length <= maxLength)
				return normalized;

			return normalized.Substring(0, maxLength) + "...";
		}

		private void ScrollToEnd()
		{
			Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
			{
				TranscriptBox.ScrollToEnd();
			});
		}

		private void TranscriptBox_SizeChanged(object sender, SizeChangedEventArgs e)
		{
			// Intentionally no-op to avoid step expand/collapse flicker caused by full document re-render.
		}

		private void UpdateWaitingAnimationState()
		{
			var shouldAnimate = (observedModel?.IsBusy == true) || animatedPlanningHeaders.Count > 0;
			if (!shouldAnimate)
			{
				StopWaitingAnimation();
				if (ExecutingStatusText != null)
				{
					ExecutingStatusText.Text = "Executing...";
				}
				return;
			}

			if (waitingAnimationTimer == null)
			{
				waitingAnimationTimer = new DispatcherTimer(DispatcherPriority.Background) {
					Interval = TimeSpan.FromMilliseconds(450),
				};
				waitingAnimationTimer.Tick += WaitingAnimationTimer_Tick;
			}

			if (!waitingAnimationTimer.IsEnabled)
			{
				waitingAnimationTimer.Start();
			}
		}

		private void StopWaitingAnimation()
		{
			if (waitingAnimationTimer?.IsEnabled == true)
			{
				waitingAnimationTimer.Stop();
			}
		}

		private void WaitingAnimationTimer_Tick(object? sender, EventArgs e)
		{
			waitingAnimationFrame = (waitingAnimationFrame + 1) % WaitingDots.Length;
			var dots = WaitingDots[waitingAnimationFrame];

			if (ExecutingStatusText != null)
			{
				ExecutingStatusText.Text = "Executing" + dots;
			}

			foreach (var entry in animatedPlanningHeaders.ToArray())
			{
				if (entry.Key.Parent == null)
				{
					animatedPlanningHeaders.Remove(entry.Key);
					continue;
				}

				entry.Key.Text = entry.Value + dots;
			}

			if (animatedPlanningHeaders.Count == 0 && observedModel?.IsBusy != true)
			{
				StopWaitingAnimation();
			}
		}

		private void InputBox_PreviewKeyDown(object sender, KeyEventArgs e)
		{
			if (e.Key != Key.Enter)
				return;

			if (DataContext is not AiChatPaneModel model)
				return;

			var controlPressed = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
			var shiftPressed = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;

			var shouldSend = model.SendOnEnter
				? !controlPressed && !shiftPressed
				: controlPressed;

			if (!shouldSend)
				return;

			if (!model.SubmitCommand.CanExecute(null))
				return;

			model.SubmitCommand.Execute(null);
			e.Handled = true;
		}

		private void MessageTextBox_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
		{
		}

		private sealed class AutoProgressParseResult
		{
			public List<AutoStepRender> Steps { get; } = [];
			public string Prefix { get; set; } = string.Empty;
		}

		private sealed class AutoStepRender(int stepNumber)
		{
			public int StepNumber { get; } = stepNumber;
			public string Planning { get; set; } = string.Empty;
			public string Thinking { get; set; } = string.Empty;
			public string ToolCall { get; set; } = string.Empty;
			public string ToolResult { get; set; } = string.Empty;
			public string Other { get; set; } = string.Empty;
		}
	}
}
