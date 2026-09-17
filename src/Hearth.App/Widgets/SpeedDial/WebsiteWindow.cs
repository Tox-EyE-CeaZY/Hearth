using System.Windows;
using System.Windows.Controls;
using Hearth.App.Controls;

namespace Hearth.App.Widgets.SpeedDial;

/// <summary>Asks for a website's address (and optionally a name) to add to Speed Dial.</summary>
internal sealed class WebsiteWindow : Window
{
    private readonly TextBox _address;
    private readonly TextBox _name;
    private readonly TextBlock _error;

    public string Address { get; private set; } = string.Empty;
    public string SiteName { get; private set; } = string.Empty;

    public WebsiteWindow()
    {
        Title = "Add a website — Hearth";
        Width = 420;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = true;
        Topmost = true;
        DialogChrome.Apply(this);

        var heading = new TextBlock
        {
            Text = "Add a website",
            FontSize = 26,
            FontWeight = FontWeights.SemiBold,
            FontFamily = WidgetChrome.Display,
            Margin = new Thickness(0, 0, 0, 6),
        };

        _address = new TextBox();
        _name = new TextBox();
        _error = new TextBlock
        {
            Foreground = DialogChrome.Brush("#FFF28B82"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 0),
            Visibility = Visibility.Collapsed,
        };

        var add = new Button { Content = "Add", MinWidth = 96, IsDefault = true, Style = (Style)Resources["Primary"], Margin = new Thickness(8, 0, 0, 0) };
        add.Click += (_, _) => OnAdd();
        var cancel = new Button { Content = "Cancel", MinWidth = 96, IsCancel = true };

        Content = new StackPanel
        {
            Margin = new Thickness(24, 20, 24, 20),
            Children =
            {
                heading,
                Label("Address"), _address,
                Label("Name (optional)"), _name,
                _error,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Margin = new Thickness(0, 22, 0, 0),
                    Children = { cancel, add },
                },
            },
        };

        Loaded += (_, _) => _address.Focus();
    }

    private void OnAdd()
    {
        var text = _address.Text.Trim();
        if (!text.Contains("://", StringComparison.Ordinal)) text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || !uri.Host.Contains('.'))
        {
            _error.Text = "That doesn't look like a web address. Try example.com.";
            _error.Visibility = Visibility.Visible;
            _address.Focus();
            return;
        }

        Address = uri.AbsoluteUri;
        var host = uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host;
        SiteName = _name.Text.Trim() is { Length: > 0 } name ? name : host.Split('.')[0] is var first && first.Length > 0
            ? char.ToUpperInvariant(first[0]) + first[1..]
            : host;
        DialogResult = true;
    }

    private static TextBlock Label(string text) => new()
    {
        Text = text,
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(0, 12, 0, 6),
    };
}
