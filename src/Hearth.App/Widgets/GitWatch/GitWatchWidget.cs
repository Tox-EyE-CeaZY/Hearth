using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Hearth.App.Controls;
using Hearth.Core.Shell;

namespace Hearth.App.Widgets.GitWatch;

public sealed class GitWatchWidget : IWidget
{
    public string Id => "git-watch";
    public string Title => "Git Watch";

    public (int Columns, int Rows) DefaultSpan => (2, 2);
    public (int Columns, int Rows) MinimumSpan => (2, 2);

    public double BoardHeight(bool wide) => wide ? 150 : 190;
    public int Order => 167;

    public FrameworkElement CreateView(WidgetContext context) => new GitWatchView(context);

    private sealed class GitWatchView : WidgetView
    {
        private readonly WidgetHeader _header;
        private readonly WrapPanel _content;
        private readonly FrameworkElement _empty;

        public GitWatchView(WidgetContext context) : base(context)
        {
            _header = new WidgetHeader(context, "\uE8B7", "Git Watch"); 
            
            _header.AddAction("\uE713", "Settings", () => new GitWatchSettings().ShowDialog());
            _header.AddAction("\uE72C", "Refresh", LoadStatus);

            _content = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4 * S) };
            _empty = WidgetLayout.Empty(context, "\uE713", "Configure a repository in Settings");

            var layout = new DockPanel();
            DockPanel.SetDock(_header, Dock.Top);
            layout.Children.Add(_header);
            
            var grid = new Grid();
            grid.Children.Add(_content);
            grid.Children.Add(_empty);
            layout.Children.Add(grid);
            SetBody(layout);

            While(() => GitWatchData.Changed += LoadStatus, () => GitWatchData.Changed -= LoadStatus);
            Every(TimeSpan.FromMinutes(2), LoadStatus);

            layout.SizeChanged += (_, _) =>
                _header.Visibility = layout.ActualHeight >= 90 * S ? Visibility.Visible : Visibility.Collapsed;
        }

        protected override void OnShown() => LoadStatus();

        private void LoadStatus()
        {
            var path = GitWatchData.Path;
            if (string.IsNullOrWhiteSpace(path))
            {
                _empty.Visibility = Visibility.Visible;
                _content.Visibility = Visibility.Collapsed;
                _header.Detail = "Not configured";
                return;
            }

            _empty.Visibility = Visibility.Collapsed;
            _content.Visibility = Visibility.Visible;
            _header.Detail = "Checking...";

            Task.Run(async () =>
            {
                var status = await GitRunner.GetStatusAsync(path);

                Post(() =>
                {
                    _content.Children.Clear();

                    if (status == null)
                    {
                        _header.Detail = "Error or not a git repo";
                        return;
                    }

                    var dirName = Path.GetFileName(path) ?? "";
                    _header.Detail = dirName;

                    var branchChip = new Chip(Context, status.Branch, () => ShellLauncher.Open(path), "\uE718"); // Pin icon to signify branch
                    branchChip.Margin = new Thickness(4 * S);
                    _content.Children.Add(branchChip);

                    if (!string.IsNullOrWhiteSpace(status.AheadBehind))
                    {
                        var aheadBehindChip = new Chip(Context, status.AheadBehind, () => ShellLauncher.Open(path));
                        aheadBehindChip.Margin = new Thickness(4 * S);
                        _content.Children.Add(aheadBehindChip);
                    }

                    if (status.Modified > 0)
                    {
                        var modChip = new Chip(Context, $"{status.Modified} changes", () => ShellLauncher.Open(path), "\uE8A5"); // Document icon
                        modChip.Margin = new Thickness(4 * S);
                        _content.Children.Add(modChip);
                    }
                });
            });
        }
    }
}

internal sealed class GitWatchSettings : Window
{
    public GitWatchSettings()
    {
        Title = "Git Watch Settings";
        Width = 400;
        Height = 200;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;
        
        var panel = new StackPanel { Margin = new Thickness(20) };
        
        var label = new TextBlock { Text = "Repository Path:", Margin = new Thickness(0, 0, 0, 8) };
        var textBox = new TextBox { Text = GitWatchData.Path, Margin = new Thickness(0, 0, 0, 16) };
        
        var saveBtn = new Button { Content = "Save", IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right };
        saveBtn.Click += (_, _) =>
        {
            GitWatchData.SetPath(textBox.Text ?? "");
            Close();
        };

        panel.Children.Add(label);
        panel.Children.Add(textBox);
        panel.Children.Add(saveBtn);

        Content = panel;
        DialogChrome.Apply(this);
    }
}
