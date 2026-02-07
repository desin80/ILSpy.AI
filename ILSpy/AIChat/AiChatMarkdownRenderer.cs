using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.ILSpy;
using ICSharpCode.ILSpy.Options;
using ICSharpCode.ILSpy.TextView;

using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

using WpfBlock = System.Windows.Documents.Block;
using MdBlock = Markdig.Syntax.Block;
using MdTable = Markdig.Extensions.Tables.Table;
using MdTableCell = Markdig.Extensions.Tables.TableCell;
using MdTableRow = Markdig.Extensions.Tables.TableRow;

namespace ICSharpCode.ILSpy.AIChat
{
	#nullable enable

	internal sealed class AiChatMarkdownRenderer
	{
		private const int ParseCacheLimit = 128;

		private readonly MarkdownPipeline pipeline = new MarkdownPipelineBuilder()
			.UseAdvancedExtensions()
			.DisableHtml()
			.Build();

		private readonly AiChatLinkReferenceParser referenceParser;
		private readonly Action<AiChatLinkReference> onReferenceClick;
		private readonly Action<MouseWheelEventArgs>? onCodeBlockMouseWheel;
		private readonly Dictionary<long, (int Hash, MarkdownDocument Document)> markdownParseCache = new();
		private readonly Queue<long> markdownParseCacheOrder = new();

		public AiChatMarkdownRenderer(AiChatLinkReferenceParser referenceParser, Action<AiChatLinkReference> onReferenceClick, Action<MouseWheelEventArgs>? onCodeBlockMouseWheel = null)
		{
			this.referenceParser = referenceParser;
			this.onReferenceClick = onReferenceClick;
			this.onCodeBlockMouseWheel = onCodeBlockMouseWheel;
		}

		public IReadOnlyList<WpfBlock> Render(AiChatMessage message)
		{
			var text = message.Text ?? string.Empty;
			if (string.IsNullOrWhiteSpace(text))
			{
				return [CreateHeaderParagraph(message), CreateBodyParagraph(string.Empty)];
			}

			try
			{
				var start = DateTime.UtcNow;
				var doc = GetOrParseMarkdown(message.Id, text);
				var blocks = new List<WpfBlock> {
					CreateHeaderParagraph(message),
				};

				foreach (var block in doc)
				{
					AddBlock(blocks, block);
				}

				if (blocks.Count == 1)
				{
					blocks.Add(CreateBodyParagraph(text));
				}

				AiChatLog.Info($"markdown render messageId={message.Id} blocks={blocks.Count - 1} elapsedMs={(DateTime.UtcNow - start).TotalMilliseconds:F0}");
				return blocks;
			}
			catch (Exception ex)
			{
				AiChatLog.Error(ex, "markdown render failed; fallback to plain text");
				return [CreateHeaderParagraph(message), CreateBodyParagraph(text)];
			}
		}

		public IReadOnlyList<WpfBlock> RenderBody(string text)
		{
			if (string.IsNullOrWhiteSpace(text))
			{
				return [CreateBodyParagraph(string.Empty)];
			}

			try
			{
				var doc = Markdown.Parse(text, pipeline);
				var blocks = new List<WpfBlock>();
				foreach (var block in doc)
				{
					AddBlock(blocks, block);
				}

				if (blocks.Count == 0)
				{
					blocks.Add(CreateBodyParagraph(text));
				}

				return blocks;
			}
			catch (Exception ex)
			{
				AiChatLog.Error(ex, "markdown body render failed; fallback to plain text");
				return [CreateBodyParagraph(text)];
			}
		}

		private MarkdownDocument GetOrParseMarkdown(long messageId, string text)
		{
			var hash = StringComparer.Ordinal.GetHashCode(text);
			if (markdownParseCache.TryGetValue(messageId, out var entry) && entry.Hash == hash)
			{
				return entry.Document;
			}

			var parsed = Markdown.Parse(text, pipeline);
			markdownParseCache[messageId] = (hash, parsed);
			markdownParseCacheOrder.Enqueue(messageId);

			while (markdownParseCache.Count > ParseCacheLimit && markdownParseCacheOrder.Count > 0)
			{
				var evicted = markdownParseCacheOrder.Dequeue();
				markdownParseCache.Remove(evicted);
			}

			return parsed;
		}

		private void AddBlock(ICollection<WpfBlock> blocks, MdBlock block)
		{
			switch (block)
			{
				case ParagraphBlock paragraph:
					blocks.Add(CreateParagraph(paragraph.Inline));
					break;
				case HeadingBlock heading:
					blocks.Add(CreateHeading(heading));
					break;
				case QuoteBlock quote:
					blocks.Add(CreateQuote(quote));
					break;
				case ListBlock list:
					blocks.Add(CreateList(list));
					break;
				case MdTable table:
					blocks.Add(CreateTable(table));
					break;
				case FencedCodeBlock fenced:
					blocks.Add(CreateCodeBlock(fenced));
					break;
				case CodeBlock code:
					blocks.Add(CreateCodeBlock(code));
					break;
				case ThematicBreakBlock:
					blocks.Add(new Paragraph(new Run(new string('-', 28))) {
						Margin = new Thickness(2, 2, 2, 6),
						Foreground = GetThemeBrush(SystemColors.ControlDarkBrushKey, Color.FromRgb(148, 148, 148)),
					});
					break;
				case Markdig.Syntax.LinkReferenceDefinitionGroup:
					break;
				default:
					blocks.Add(CreateBodyParagraph(GetBlockText(block)));
					break;
			}
		}

		private Paragraph CreateHeaderParagraph(AiChatMessage message)
		{
			var roleBrush = GetRoleBrush(message.Role);
			var subtle = GetThemeBrush(SystemColors.GrayTextBrushKey, Color.FromRgb(130, 130, 130));

			var header = new Paragraph {
				Margin = new Thickness(2, 2, 2, 2),
				LineHeight = 18,
			};

			header.Inlines.Add(new Run($"[{message.Timestamp:HH:mm:ss}] ") {
				Foreground = subtle,
			});
			header.Inlines.Add(new Run($"{GetRoleText(message.Role)}: ") {
				Foreground = roleBrush,
				FontWeight = FontWeights.Normal,
			});

			return header;
		}

		private Paragraph CreateHeading(HeadingBlock heading)
		{
			var paragraph = new Paragraph {
				Margin = new Thickness(2, 3, 2, 4),
				FontWeight = FontWeights.SemiBold,
				FontSize = heading.Level <= 2 ? 13 : 12,
				Foreground = GetThemeBrush(SystemColors.WindowTextBrushKey, Color.FromRgb(26, 26, 26)),
			};

			AppendInline(paragraph.Inlines, heading.Inline);
			return paragraph;
		}

		private Paragraph CreateParagraph(ContainerInline? inline)
		{
			var paragraph = new Paragraph {
				Margin = new Thickness(2, 1, 2, 6),
				LineHeight = 18,
				Foreground = GetThemeBrush(SystemColors.WindowTextBrushKey, Color.FromRgb(28, 28, 28)),
			};

			AppendInline(paragraph.Inlines, inline);
			return paragraph;
		}

		private Paragraph CreateBodyParagraph(string text)
		{
			var paragraph = new Paragraph {
				Margin = new Thickness(2, 1, 2, 6),
				LineHeight = 18,
				Foreground = GetThemeBrush(SystemColors.WindowTextBrushKey, Color.FromRgb(28, 28, 28)),
			};

			AppendTextWithAutoLinks(paragraph.Inlines, text);
			return paragraph;
		}

		private WpfBlock CreateQuote(QuoteBlock quote)
		{
			var panel = new StackPanel();
			foreach (var child in quote)
			{
				if (child is MdBlock childBlock)
				{
					var nested = new List<WpfBlock>();
					AddBlock(nested, childBlock);
					foreach (var block in nested)
					{
						if (block is Paragraph p)
						{
							p.Margin = new Thickness(0, 1, 0, 3);
						}
						panel.Children.Add(new RichTextBox {
							Document = new FlowDocument(block) { PagePadding = new Thickness(0) },
							IsReadOnly = true,
							BorderThickness = new Thickness(0),
							Background = Brushes.Transparent,
							Padding = new Thickness(0),
							IsDocumentEnabled = true,
							IsUndoEnabled = false,
							VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
							HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
						});
					}
				}
			}

			return new BlockUIContainer(new Border {
				BorderBrush = GetThemeBrush(SystemColors.ControlDarkBrushKey, Color.FromRgb(150, 150, 150)),
				BorderThickness = new Thickness(2, 0, 0, 0),
				Padding = new Thickness(8, 2, 2, 2),
				Margin = new Thickness(2, 1, 2, 6),
				Background = Brushes.Transparent,
				Child = panel,
			});
		}

		private WpfBlock CreateList(ListBlock list)
		{
			var wpfList = new System.Windows.Documents.List {
				MarkerStyle = list.IsOrdered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
				Margin = new Thickness(18, 1, 2, 6),
			};

			foreach (var item in list)
			{
				if (item is not ListItemBlock itemBlock)
					continue;

				var listItem = new ListItem();
				foreach (var sub in itemBlock)
				{
					if (sub is MdBlock subBlock)
					{
						if (subBlock is ParagraphBlock p)
						{
							listItem.Blocks.Add(CreateParagraph(p.Inline));
						}
						else
						{
							var nested = new List<WpfBlock>();
							AddBlock(nested, subBlock);
							foreach (var b in nested)
							{
								listItem.Blocks.Add(b);
							}
						}
					}
				}

				if (listItem.Blocks.Count == 0)
				{
					listItem.Blocks.Add(CreateBodyParagraph(GetBlockText(itemBlock)));
				}

				wpfList.ListItems.Add(listItem);
			}

			return wpfList;
		}

		private WpfBlock CreateTable(MdTable table)
		{
			var grid = new Grid {
				Margin = new Thickness(0),
			};

			var rows = table.OfType<MdTableRow>().ToList();
			var maxColumns = rows.Count == 0 ? 0 : rows.Max(r => r.Count());
			for (var column = 0; column < maxColumns; column++)
			{
				grid.ColumnDefinitions.Add(new ColumnDefinition {
					Width = new GridLength(1, GridUnitType.Star),
				});
			}

			for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
			{
				grid.RowDefinitions.Add(new RowDefinition {
					Height = GridLength.Auto,
				});

				var row = rows[rowIndex];
				var columnIndex = 0;
				foreach (var cellObj in row)
				{
					if (cellObj is not MdTableCell cell)
						continue;

					var textBlock = new TextBlock {
						Text = GetTableCellText(cell),
						TextWrapping = TextWrapping.Wrap,
						Margin = new Thickness(0),
						Foreground = GetThemeBrush(SystemColors.WindowTextBrushKey, Color.FromRgb(30, 30, 30)),
					};

					var cellBorder = new Border {
						BorderBrush = GetThemeBrush(SystemColors.ControlDarkBrushKey, Color.FromRgb(150, 150, 150)),
						BorderThickness = new Thickness(1),
						Padding = new Thickness(4, 2, 4, 2),
						Background = row.IsHeader
							? GetThemeBrush(SystemColors.ControlLightBrushKey, Color.FromRgb(240, 240, 240))
							: Brushes.Transparent,
						Child = textBlock,
					};

					Grid.SetRow(cellBorder, rowIndex);
					Grid.SetColumn(cellBorder, columnIndex);
					grid.Children.Add(cellBorder);

					columnIndex++;
				}
			}

			return new BlockUIContainer(new Border {
				Margin = new Thickness(2, 1, 2, 6),
				Child = grid,
			});
		}

		private WpfBlock CreateCodeBlock(CodeBlock code)
		{
			var content = GetCodeBlockText(code);
			var textEditor = new TextEditor {
				Text = content,
				IsReadOnly = true,
				ShowLineNumbers = false,
				WordWrap = false,
				VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
				HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
				BorderThickness = new Thickness(0),
				Background = Brushes.Transparent,
				Padding = new Thickness(0),
				Margin = new Thickness(0),
				FontFamily = GetCodeFont(),
				FontSize = 12,
			};
			textEditor.Options.EnableHyperlinks = false;
			textEditor.Options.EnableEmailHyperlinks = false;
			textEditor.PreviewMouseWheel += CodeBlockEditor_PreviewMouseWheel;
			try
			{
				textEditor.SyntaxHighlighting = ResolveCodeHighlighting(code);
			}
			catch (Exception ex)
			{
				AiChatLog.Warn("markdown code highlight fallback: " + ex.Message);
				textEditor.SyntaxHighlighting = null;
			}

			return new BlockUIContainer(new Border {
				BorderBrush = GetThemeBrush(SystemColors.ControlDarkBrushKey, Color.FromRgb(138, 138, 138)),
				BorderThickness = new Thickness(1),
				Background = GetThemeBrush(SystemColors.ControlLightBrushKey, Color.FromRgb(244, 244, 244)),
				Margin = new Thickness(2, 1, 2, 6),
				Padding = new Thickness(8, 6, 8, 6),
				Child = textEditor,
				MaxHeight = 380,
			});
		}

		private void CodeBlockEditor_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
		{
			if (sender is not TextEditor editor)
			{
				return;
			}

			var scrollViewer = editor.FindVisualChild<ScrollViewer>();
			if (scrollViewer == null)
			{
				return;
			}

			var canScrollUp = e.Delta > 0 && scrollViewer.VerticalOffset > 0;
			var canScrollDown = e.Delta < 0 && scrollViewer.VerticalOffset < scrollViewer.ScrollableHeight;
			if (canScrollUp || canScrollDown)
			{
				e.Handled = true;
				return;
			}

			onCodeBlockMouseWheel?.Invoke(e);
		}

		private static IHighlightingDefinition? ResolveCodeHighlighting(CodeBlock code)
		{
			DecompilerTextView.RegisterHighlighting();

			if (code is FencedCodeBlock fenced)
			{
				var languageHint = fenced.Info?.ToString()?.Trim();
				if (!string.IsNullOrWhiteSpace(languageHint))
				{
					var token = languageHint.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries)[0].Trim().ToLowerInvariant();
					return token switch {
						"cs" or "csharp" or "c#" => HighlightingManager.Instance.GetDefinition("C#"),
						"il" or "ilasm" => HighlightingManager.Instance.GetDefinition("ILAsm"),
						"asm" => HighlightingManager.Instance.GetDefinition("Asm"),
						"xml" or "xaml" or "html" => HighlightingManager.Instance.GetDefinition("xml"),
						_ => HighlightingManager.Instance.GetDefinitionByExtension("." + token),
					};
				}
			}

			var content = GetCodeBlockText(code);
			if (LooksLikeCSharp(content))
			{
				return HighlightingManager.Instance.GetDefinition("C#");
			}

			if (LooksLikeIL(content))
			{
				return HighlightingManager.Instance.GetDefinition("ILAsm");
			}

			if (LooksLikeXml(content))
			{
				return HighlightingManager.Instance.GetDefinition("xml");
			}

			return HighlightingManager.Instance.GetDefinition("C#");
		}

		private static bool LooksLikeCSharp(string text)
		{
			if (string.IsNullOrWhiteSpace(text))
				return false;

			return text.Contains("public ", StringComparison.Ordinal)
				|| text.Contains("private ", StringComparison.Ordinal)
				|| text.Contains("class ", StringComparison.Ordinal)
				|| text.Contains("void ", StringComparison.Ordinal)
				|| text.Contains("=>", StringComparison.Ordinal)
				|| text.Contains("using ", StringComparison.Ordinal)
				|| (text.Contains('{') && text.Contains('}') && text.Contains(';'));
		}

		private static bool LooksLikeIL(string text)
		{
			if (string.IsNullOrWhiteSpace(text))
				return false;

			return text.Contains(".method", StringComparison.Ordinal)
				|| text.Contains("ldarg", StringComparison.Ordinal)
				|| text.Contains("stloc", StringComparison.Ordinal)
				|| text.Contains("IL_", StringComparison.Ordinal)
				|| text.Contains(".maxstack", StringComparison.Ordinal);
		}

		private static bool LooksLikeXml(string text)
		{
			if (string.IsNullOrWhiteSpace(text))
				return false;

			return text.Contains("</", StringComparison.Ordinal)
				|| text.Contains("<?xml", StringComparison.Ordinal)
				|| (text.Contains('<') && text.Contains('>') && text.Contains("/>", StringComparison.Ordinal));
		}

		private void AppendInline(InlineCollection target, ContainerInline? inline, bool enableAutoLinks = true)
		{
			if (inline == null)
				return;

			foreach (var child in inline)
			{
				switch (child)
				{
					case LiteralInline literal:
						var text = literal.Content.ToString();
						if (enableAutoLinks)
						{
							AppendTextWithAutoLinks(target, text);
						}
						else
						{
							target.Add(new Run(text));
						}
						break;
					case LineBreakInline breakInline:
						target.Add(breakInline.IsHard ? new LineBreak() : new Run(" "));
						break;
					case CodeInline codeInline:
						AppendCodeInline(target, codeInline.Content, enableAutoLinks);
						break;
					case EmphasisInline emphasis:
						var span = new Span();
						if (emphasis.DelimiterCount >= 2)
						{
							span.FontWeight = FontWeights.SemiBold;
						}
						else
						{
							span.FontStyle = FontStyles.Italic;
						}

						if (emphasis.DelimiterChar == '~')
						{
							span.TextDecorations = TextDecorations.Strikethrough;
						}

						AppendInline(span.Inlines, emphasis, enableAutoLinks);
						target.Add(span);
						break;
					case LinkInline link:
						target.Add(CreateHyperlink(link));
						break;
					default:
						if (child is ContainerInline nested)
						{
							AppendInline(target, nested, enableAutoLinks);
						}
						break;
				}
			}
		}

		private void AppendCodeInline(InlineCollection target, string code, bool enableAutoLinks)
		{
			var background = GetThemeBrush(SystemColors.ControlLightBrushKey, Color.FromRgb(240, 240, 240));
			if (!enableAutoLinks)
			{
				target.Add(new Run(code) {
					FontFamily = GetCodeFont(),
					Background = background,
				});
				return;
			}

			var matches = referenceParser.FindMatches(code);
			if (matches.Count == 0)
			{
				target.Add(new Run(code) {
					FontFamily = GetCodeFont(),
					Background = background,
				});
				return;
			}

			var cursor = 0;
			foreach (var match in matches)
			{
				if (match.Start < cursor)
					continue;

				if (match.Start > cursor)
				{
					target.Add(new Run(code.Substring(cursor, match.Start - cursor)) {
						FontFamily = GetCodeFont(),
						Background = background,
					});
				}

				var display = code.Substring(match.Start, match.Length);
				var hyperlink = new Hyperlink(new Run(display)) {
					Foreground = GetThemeBrush(SystemColors.HotTrackBrushKey, Color.FromRgb(0, 102, 204)),
					TextDecorations = TextDecorations.Underline,
				};
				hyperlink.Click += (_, _) => onReferenceClick(match.Reference);
				target.Add(hyperlink);

				cursor = match.Start + match.Length;
			}

			if (cursor < code.Length)
			{
				target.Add(new Run(code[cursor..]) {
					FontFamily = GetCodeFont(),
					Background = background,
				});
			}
		}

		private Hyperlink CreateHyperlink(LinkInline link)
		{
			var inline = new Span();
			if (link.FirstChild != null)
			{
				AppendInline(inline.Inlines, link, enableAutoLinks: false);
			}
			else
			{
				inline.Inlines.Add(new Run(link.Url ?? string.Empty));
			}

			AiChatLinkReference? reference = null;
			if (!string.IsNullOrWhiteSpace(link.Url) && referenceParser.TryParse(link.Url, out var parsed))
			{
				reference = parsed;
			}

			var hyperlink = new Hyperlink(inline) {
				Foreground = GetThemeBrush(SystemColors.HotTrackBrushKey, Color.FromRgb(0, 102, 204)),
			};
			hyperlink.TextDecorations = TextDecorations.Underline;

			if (reference != null)
			{
				hyperlink.Click += (_, _) => onReferenceClick(reference);
			}
			else if (!string.IsNullOrWhiteSpace(link.Url))
			{
				var fallback = AiChatLinkReference.CreateExternal(link.Url!);
				hyperlink.Click += (_, _) => onReferenceClick(fallback);
			}

			return hyperlink;
		}

		private void AppendTextWithAutoLinks(InlineCollection target, string text)
		{
			if (string.IsNullOrEmpty(text))
				return;

			var matches = referenceParser.FindMatches(text);
			if (matches.Count == 0)
			{
				target.Add(new Run(text));
				return;
			}

			var cursor = 0;
			foreach (var match in matches)
			{
				if (match.Start < cursor)
					continue;

				if (match.Start > cursor)
				{
					target.Add(new Run(text.Substring(cursor, match.Start - cursor)));
				}

				var display = text.Substring(match.Start, match.Length);
				var hyperlink = new Hyperlink(new Run(display)) {
					Foreground = GetThemeBrush(SystemColors.HotTrackBrushKey, Color.FromRgb(0, 102, 204)),
					TextDecorations = TextDecorations.Underline,
				};
				hyperlink.Click += (_, _) => onReferenceClick(match.Reference);
				target.Add(hyperlink);

				cursor = match.Start + match.Length;
			}

			if (cursor < text.Length)
			{
				target.Add(new Run(text[cursor..]));
			}
		}

		private static string GetCodeBlockText(CodeBlock code)
		{
			var sb = new StringBuilder();
			foreach (var line in code.Lines.Lines)
			{
				sb.Append(line.ToString());
				sb.AppendLine();
			}

			return sb.ToString().TrimEnd('\r', '\n');
		}

		private static string GetBlockText(MdBlock block)
		{
			if (block is LeafBlock leaf)
			{
				var sb = new StringBuilder();
				foreach (var line in leaf.Lines.Lines)
				{
					sb.Append(line.ToString());
					sb.AppendLine();
				}

				return sb.ToString().TrimEnd();
			}

			return block.ToString() ?? string.Empty;
		}

		private static string GetTableCellText(MdTableCell cell)
		{
			var parts = new List<string>();
			foreach (var child in cell)
			{
				if (child is ParagraphBlock paragraph)
				{
					parts.Add(GetInlinePlainText(paragraph.Inline));
				}
				else if (child is MdBlock block)
				{
					parts.Add(GetBlockText(block));
				}
			}

			return string.Join(Environment.NewLine, parts.Where(part => !string.IsNullOrWhiteSpace(part))).Trim();
		}

		private static string GetInlinePlainText(ContainerInline? inline)
		{
			if (inline == null)
				return string.Empty;

			var sb = new StringBuilder();
			foreach (var child in inline)
			{
				switch (child)
				{
					case LiteralInline literal:
						sb.Append(literal.Content.ToString());
						break;
					case LineBreakInline breakInline:
						sb.Append(breakInline.IsHard ? Environment.NewLine : " ");
						break;
					case CodeInline codeInline:
						sb.Append(codeInline.Content);
						break;
					case ContainerInline nested:
						sb.Append(GetInlinePlainText(nested));
						break;
				}
			}

			return sb.ToString();
		}

		private static Brush GetThemeBrush(object resourceKey, Color fallback)
		{
			if (Application.Current?.Resources[resourceKey] is Brush brush)
			{
				return brush;
			}

			return new SolidColorBrush(fallback);
		}

		private static Brush GetRoleBrush(AiChatMessageRole role)
		{
			return role switch
			{
				AiChatMessageRole.User => GetThemeBrush(SystemColors.HighlightBrushKey, Color.FromRgb(0, 102, 204)),
				AiChatMessageRole.System => new SolidColorBrush(Color.FromRgb(148, 94, 0)),
				_ => GetThemeBrush(SystemColors.WindowTextBrushKey, Color.FromRgb(28, 28, 28)),
			};
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

		private static FontFamily GetCodeFont()
		{
			try
			{
				var settings = App.ExportProvider.GetExportedValue<SettingsService>().DisplaySettings;
				if (settings?.SelectedFont != null)
				{
					return settings.SelectedFont;
				}
			}
			catch
			{
			}

			return new FontFamily("Consolas");
		}
	}
}
