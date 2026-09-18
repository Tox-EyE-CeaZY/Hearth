using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Hearth.Core.Shell;

namespace Hearth.App.Widgets.SteamLauncher;

public sealed class SteamLauncherWidget : IWidget
{
    public string Id => "steam-launcher";
    public string Title => "Steam Launcher";

    public (int Columns, int Rows) DefaultSpan => (3, 2);
    public (int Columns, int Rows) MinimumSpan => (2, 2);

    public double BoardHeight(bool wide) => wide ? 150 : 190;
    public int Order => 165;

    public FrameworkElement CreateView(WidgetContext context) => new SteamLauncherView(context);

    private sealed class SteamLauncherView : WidgetView
    {
        private readonly WidgetHeader _header;
        private readonly WrapPanel _grid = new();
        private readonly FrameworkElement _empty;
        private IReadOnlyList<SteamGame>? _games;
        private bool _loading;

        public SteamLauncherView(WidgetContext context) : base(context)
        {
            _header = new WidgetHeader(context, "\uECAD", "Steam");
            _header.AddAction("\uE72C", "Refresh library", LoadGames);

            _empty = WidgetLayout.Empty(context, "\uECAD", "No games found");

            _grid.Orientation = Orientation.Horizontal;
            _grid.Margin = new Thickness(4 * S);

            var layout = new DockPanel();
            DockPanel.SetDock(_header, Dock.Top);
            layout.Children.Add(_header);
            layout.Children.Add(new Grid { Children = { WidgetLayout.Scroller(_grid), _empty } });
            SetBody(layout);

            layout.SizeChanged += (_, _) =>
                _header.Visibility = layout.ActualHeight >= 90 * S ? Visibility.Visible : Visibility.Collapsed;
        }

        protected override void OnShown()
        {
            if (_games == null && !_loading)
            {
                LoadGames();
            }
        }

        private void LoadGames()
        {
            if (_loading) return;
            _loading = true;
            _header.Detail = "Loading...";
            
            Task.Run(() =>
            {
                try
                {
                    var games = SteamLibrary.GetInstalledGames();
                    Post(() =>
                    {
                        _games = games;
                        Rebuild();
                        _loading = false;
                    });
                }
                catch (Exception ex)
                {
                    Post(() =>
                    {
                        Hearth.Core.Diagnostics.Log.Error("SteamLauncherWidget", ex);
                        _loading = false;
                    });
                }
            });
        }

        private void Rebuild()
        {
            if (_games == null) return;
            
            _header.Detail = _games.Count == 0 ? string.Empty : $"{_games.Count} installed";
            _empty.Visibility = _games.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            _grid.Children.Clear();
            foreach (var game in _games)
            {
                _grid.Children.Add(GameChip(game));
            }
        }

        private FrameworkElement GameChip(SteamGame game)
        {
            var chip = new Chip(Context, game.Name, () => ShellLauncher.Open($"steam://rungameid/{game.Id}"));
            chip.Margin = new Thickness(2 * S);
            chip.ToolTip = $"Launch {game.Name}";
            return chip;
        }
    }
}
