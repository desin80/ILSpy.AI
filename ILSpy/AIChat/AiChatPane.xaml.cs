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
using System.Collections.Specialized;
using System.Windows.Threading;
using System.Windows.Controls;
using System.Windows;
using System.Windows.Input;

using TomsToolbox.Wpf.Composition.AttributedModel;

namespace ICSharpCode.ILSpy.AIChat
{
	#nullable enable

	[DataTemplate(typeof(AiChatPaneModel))]
	[NonShared]
	public partial class AiChatPane
	{
		private AiChatPaneModel? observedModel;

		public AiChatPane()
		{
			InitializeComponent();
			DataContextChanged += AiChatPane_DataContextChanged;
		}

		private void AiChatPane_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
		{
			if (observedModel != null)
			{
				observedModel.Messages.CollectionChanged -= Messages_CollectionChanged;
			}

			observedModel = e.NewValue as AiChatPaneModel;
			if (observedModel != null)
			{
				observedModel.Messages.CollectionChanged += Messages_CollectionChanged;
			}
		}

		private void Messages_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
		{
			if (e.Action != NotifyCollectionChangedAction.Add)
				return;

			ScrollMessagesToEnd();
		}

		private void MessagesList_Loaded(object sender, RoutedEventArgs e)
		{
			ScrollMessagesToEnd();
		}

		private void MessageTextBox_SizeChanged(object sender, SizeChangedEventArgs e)
		{
			if (e.WidthChanged)
			{
				ScrollMessagesToEnd();
			}
		}

		private void ScrollMessagesToEnd()
		{
			Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
			{
				if (MessagesList.Items.Count == 0)
					return;

				MessagesList.UpdateLayout();
				MessagesList.ScrollIntoView(MessagesList.Items[MessagesList.Items.Count - 1]);
			});
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
			e.Handled = true;
			var wheelEvent = new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) {
				RoutedEvent = MouseWheelEvent,
				Source = sender,
			};
			MessagesList.RaiseEvent(wheelEvent);
		}
	}
}
