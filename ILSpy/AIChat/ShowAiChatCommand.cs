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
using System.Windows.Input;

using ICSharpCode.ILSpy.Commands;

namespace ICSharpCode.ILSpy.AIChat
{
	[ExportMainMenuCommand(ParentMenuID = nameof(Properties.Resources._View), Header = "AI Chat", MenuCategory = nameof(Properties.Resources.View), MenuOrder = 600)]
	[ExportToolbarCommand(ToolTip = "AI Chat", ToolbarIcon = "Images/Search", ToolbarCategory = nameof(Properties.Resources.View), ToolbarOrder = 600)]
	[Shared]
	internal sealed class ShowAiChatCommand : ICommand
	{
		private readonly Docking.DockWorkspace dockWorkspace;

		public ShowAiChatCommand(Docking.DockWorkspace dockWorkspace)
		{
			this.dockWorkspace = dockWorkspace;
		}

		public bool CanExecute(object parameter)
		{
			return true;
		}

		public void Execute(object parameter)
		{
			dockWorkspace.ShowToolPane(AiChatPaneModel.PaneContentId);
		}

		public event System.EventHandler CanExecuteChanged {
			add { CommandManager.RequerySuggested += value; }
			remove { CommandManager.RequerySuggested -= value; }
		}
	}
}
