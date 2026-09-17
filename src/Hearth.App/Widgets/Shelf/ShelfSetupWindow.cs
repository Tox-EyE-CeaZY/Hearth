using System.IO;
using System.Windows;
using System.Windows.Controls;
using Hearth.App.Controls;
using Microsoft.Win32;

namespace Hearth.App.Widgets.Shelf;

/// <summary>Chooses what the Shelf holds: Hearth's own stash, or a real folder.</summary>
internal sealed class ShelfSetupWindow : Window
{
    private readonly RadioButton _stash;
    private readonly RadioButton _folder;
    private readonly TextBox _path;
    private readonly TextBlock _error;

    public ShelfConfig? Result { get; private set; }

    public ShelfSetupWindow(ShelfConfig current)
    {
        Title = "Shelf — Hearth";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = true;
        Topmost = true;
        DialogChrome.Apply(this);

        var heading = new TextBlock
        {
            Text = "Shelf",
            FontSize = 26,
            FontWeight = FontWeights.SemiBold,
            FontFamily = WidgetChrome.Display,
        };

        _stash = new RadioButton
        {
            Content = "A holding area (Hearth keeps the files for you)",
            GroupName = "mode",
            Margin = new Thickness(0, 16, 0, 4),
            IsChecked = current.Mode == ShelfMode.Stash,
        };
        var stashNote = Muted($"Kept in {ShelfFolder.StashPath}");
        stashNote.Margin = new Thickness(26, 0, 0, 0);

        _folder = new RadioButton
        {
            Content = "A folder on this PC",
            GroupName = "mode",
            Margin = new Thickness(0, 14, 0, 6),
            IsChecked = current.Mode == ShelfMode.Folder,
        };

        _path = new TextBox
        {
            Text = current.FolderPath ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + @"\Downloads",
        };
        _path.GotFocus += (_, _) => _folder.IsChecked = true;
        var browse = new Button { Content = "Browse...", Margin = new Thickness(8, 0, 0, 0) };
        browse.Click += (_, _) => Browse();
        var pathRow = new DockPanel { Margin = new Thickness(26, 0, 0, 0) };
        DockPanel.SetDock(browse, Dock.Right);
        pathRow.Children.Add(browse);
        pathRow.Children.Add(_path);

        _error = Muted(string.Empty);
        _error.Foreground = DialogChrome.Brush("#FFF28B82");
        _error.Margin = new Thickness(26, 8, 0, 0);
        _error.Visibility = Visibility.Collapsed;

        var save = new Button { Content = "Save", MinWidth = 96, IsDefault = true, Style = (Style)Resources["Primary"], Margin = new Thickness(8, 0, 0, 0) };
        save.Click += (_, _) => OnSave();
        var cancel = new Button { Content = "Cancel", MinWidth = 96, IsCancel = true };
        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 22, 0, 0),
            Children = { cancel, save },
        };

        Content = new StackPanel
        {
            Margin = new Thickness(24, 20, 24, 20),
            Children = { heading, _stash, stashNote, _folder, pathRow, _error, footer },
        };
    }

    private void Browse()
    {
        var dialog = new OpenFolderDialog { Title = "Choose the Shelf's folder" };
        if (Directory.Exists(_path.Text)) dialog.InitialDirectory = _path.Text;
        if (dialog.ShowDialog(this) != true) return;
        _path.Text = dialog.FolderName;
        _folder.IsChecked = true;
    }

    private void OnSave()
    {
        if (_folder.IsChecked == true)
        {
            var path = _path.Text.Trim();
            if (!Directory.Exists(path))
            {
                _error.Text = "That folder doesn't exist.";
                _error.Visibility = Visibility.Visible;
                return;
            }
            Result = new ShelfConfig { Mode = ShelfMode.Folder, FolderPath = path };
        }
        else
        {
            Result = new ShelfConfig { Mode = ShelfMode.Stash, FolderPath = _path.Text.Trim() };
        }
        DialogResult = true;
    }

    private static TextBlock Muted(string text) => new()
    {
        Text = text,
        Foreground = DialogChrome.Brush("#99F2F2F4"),
        FontSize = 12,
        TextWrapping = TextWrapping.Wrap,
    };
}
