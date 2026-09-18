using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Hearth.App.Controls;
using Hearth.Core.Shell;
using Hearth.Core.Diagnostics;

namespace Hearth.App.Widgets.QuickNote;

public sealed class QuickNoteWidget : IWidget
{
    public string Id => "quick-note";
    public string Title => "Quick Note";

    public (int Columns, int Rows) DefaultSpan => (2, 2);
    public (int Columns, int Rows) MinimumSpan => (2, 2);

    public double BoardHeight(bool wide) => wide ? 150 : 190;
    public int Order => 166;

    public FrameworkElement CreateView(WidgetContext context) => new QuickNoteView(context);

    private sealed class QuickNoteView : WidgetView
    {
        private readonly WidgetHeader _header;
        private readonly InlineInput _input;
        private readonly FrameworkElement _empty;

        public QuickNoteView(WidgetContext context) : base(context)
        {
            _header = new WidgetHeader(context, "\uE8A5", "Quick Note");
            _header.AddAction("\uE713", "Settings", () => new QuickNoteSettings().ShowDialog());

            _input = new InlineInput(context, "Type note and hit enter...", 13);
            _input.Committed += OnCommitted;
            _input.Margin = new Thickness(8 * S);

            _empty = WidgetLayout.Empty(context, "\uE713", "Click Settings to set a file");

            var layout = new DockPanel();
            DockPanel.SetDock(_header, Dock.Top);
            layout.Children.Add(_header);
            
            var grid = new Grid();
            grid.Children.Add(_input);
            grid.Children.Add(_empty);
            layout.Children.Add(grid);
            SetBody(layout);

            While(() => QuickNoteData.Changed += Rebuild, () => QuickNoteData.Changed -= Rebuild);
            layout.SizeChanged += (_, _) =>
                _header.Visibility = layout.ActualHeight >= 90 * S ? Visibility.Visible : Visibility.Collapsed;
        }

        protected override void OnShown() => Rebuild();

        private void Rebuild()
        {
            var path = QuickNoteData.Path;
            var hasPath = !string.IsNullOrWhiteSpace(path);
            _empty.Visibility = hasPath ? Visibility.Collapsed : Visibility.Visible;
            _input.Visibility = hasPath ? Visibility.Visible : Visibility.Collapsed;
            
            if (hasPath)
            {
                _header.Detail = Path.GetFileName(path) ?? "";
            }
            else
            {
                _header.Detail = "Not configured";
            }
        }

        private void OnCommitted(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            var path = QuickNoteData.Path;
            if (string.IsNullOrWhiteSpace(path)) return;

            _header.Detail = "Saving...";
            
            Task.Run(async () => 
            {
                try
                {
                    var timestamp = DateTime.Now.ToString("HH:mm");
                    var line = $"- [{timestamp}] {text}{Environment.NewLine}";
                    await File.AppendAllTextAsync(path, line);
                    
                    Post(() =>
                    {
                        _input.Text = string.Empty;
                        _header.Detail = "Saved";
                    });
                    
                    await Task.Delay(2000);
                    Post(Rebuild);
                }
                catch (Exception ex)
                {
                    Log.Error("QuickNoteWidget", ex);
                    Post(() => _header.Detail = "Failed to save");
                }
            });
        }
    }
}

internal sealed class QuickNoteSettings : Window
{
    public QuickNoteSettings()
    {
        Title = "Quick Note Settings";
        Width = 400;
        Height = 200;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;
        
        var panel = new StackPanel { Margin = new Thickness(20) };
        
        var label = new TextBlock { Text = "Markdown File Path:", Margin = new Thickness(0, 0, 0, 8) };
        var textBox = new TextBox { Text = QuickNoteData.Path, Margin = new Thickness(0, 0, 0, 16) };
        
        var saveBtn = new Button { Content = "Save", IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right };
        saveBtn.Click += (_, _) =>
        {
            QuickNoteData.SetPath(textBox.Text ?? "");
            Close();
        };

        panel.Children.Add(label);
        panel.Children.Add(textBox);
        panel.Children.Add(saveBtn);

        Content = panel;
        DialogChrome.Apply(this);
    }
}
