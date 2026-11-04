using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace ModernImageViewer
{
    public sealed partial class MainWindow
    {
        private static readonly (string Title, (string Keys, string Action)[] Rows)[] ShortcutSections =
        {
            ("Anywhere", new[]
            {
                ("F1", "This list"),
                ("F6", "Move to the header buttons. Left, Right or Tab moves along, Enter presses, Esc returns"),
                ("F11", "Full screen"),
                ("Ctrl+1, 2, 3", "Window and tile size presets"),
            }),
            ("Folder view", new[]
            {
                ("Arrow keys, Page Up, Page Down, Home, End", "Move the cursor"),
                ("Enter", "Open the image, or view the selected images together"),
                ("Space", "Select or deselect the image"),
                ("Ctrl+click, Shift+click", "Select several images"),
                ("Backspace", "Up one folder"),
                ("F2", "Rename"),
                ("F5", "Refresh"),
                ("Delete", "Move to the Recycle Bin"),
                ("Tab, Ctrl+B", "Sidebar"),
                ("Esc", "Close the sidebar, or clear the selection"),
            }),
            ("Viewer", new[]
            {
                ("Left, Right", "Previous and next image"),
                ("Home, End", "First and last image"),
                ("B", "Two pages at once, in a comic book"),
                ("Space", "Play or pause an animation"),
                (", and .", "Previous and next frame of an animation"),
                ("Delete", "Move to the Recycle Bin"),
                ("Enter, Esc", "Back to the folder"),
            }),
            ("Detached window", new[]
            {
                ("F11", "Full screen"),
                ("Delete", "Move to the Recycle Bin"),
                ("Esc", "Close the window"),
            }),
            ("Multi-View", new[]
            {
                ("+, -", "More or fewer lines"),
                ("R", "Rows or columns"),
                ("H", "Next hero"),
                ("Ctrl+Z, Ctrl+Y", "Undo and redo"),
                ("Ctrl+C", "Copy the canvas as an image"),
                ("Ctrl+S", "Save or load"),
                ("Alt+drag", "Swap two images"),
                ("Space", "Play or pause the storyboard or Auto-Mix"),
                ("Left, Right", "Previous and next scene or layout, while playing"),
                ("B", "Storyboard"),
                ("F11", "Full screen"),
                ("Esc", "Pause auto-play if it is playing, otherwise back to the folder"),
            }),
            ("Cinematic", new[]
            {
                ("Space", "Pause or play"),
                ("Left, Right", "Previous and next slide"),
                ("F11", "Full screen"),
                ("Esc", "Back out one step: preview, Edit show, full screen, then the show"),
            }),
            ("Floating cells", new[]
            {
                ("Ctrl+drag", "Pan the image inside the cell"),
                ("Alt while dragging", "Move without snapping"),
                ("Ctrl on the rotate handle", "Turn the shape and keep the image still"),
                ("Shift on the rotate handle", "Rotate in 15 degree steps"),
            }),
        };

        private bool _shortcutsOpen;

        private async Task ShowShortcutsAsync()
        {
            if (_shortcutsOpen || RootGrid.XamlRoot == null) return;
            _shortcutsOpen = true;
            try
            {
                var panel = new StackPanel { Spacing = 14 };
                foreach (var (title, rows) in ShortcutSections)
                {
                    var section = new StackPanel { Spacing = 4 };
                    section.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold });
                    var grid = new Grid { ColumnSpacing = 16, RowSpacing = 3 };
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    foreach (var (keys, action) in rows)
                    {
                        int row = grid.RowDefinitions.Count;
                        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                        var keyText = new TextBlock { Text = keys, FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
                        var actionText = new TextBlock { Text = action, FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
                        Grid.SetRow(keyText, row);
                        Grid.SetRow(actionText, row);
                        Grid.SetColumn(actionText, 1);
                        grid.Children.Add(keyText);
                        grid.Children.Add(actionText);
                    }
                    section.Children.Add(grid);
                    panel.Children.Add(section);
                }

                var dialog = new ContentDialog
                {
                    Title = "Keyboard shortcuts",
                    Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
                    CloseButtonText = "Close",
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = RootGrid.XamlRoot,
                    RequestedTheme = CurrentRequestedTheme
                };
                await dialog.ShowAsync();
            }
            catch (COMException)
            {
                // Another dialog is already open, and only one can show at a time.
            }
            finally
            {
                _shortcutsOpen = false;
            }
        }
    }
}
