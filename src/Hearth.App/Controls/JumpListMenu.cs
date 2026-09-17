using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Hearth.Core.Diagnostics;
using Hearth.Core.Icons;
using Hearth.Core.Shell;
using Hearth.Core.Threading;

namespace Hearth.App.Controls;

/// <summary>
/// An app's jump list as context-menu rows, shared by the desktop and the
/// Start menu: its own categories and recent files, then its tasks, as on the
/// taskbar.
/// </summary>
internal static class JumpListMenu
{
    private static readonly string LinkCache = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Hearth", "jumplist");

    private const int MaxEntriesPerCategory = 8;

    /// <summary>
    /// Adds the jump list for <paramref name="appId"/> to the menu. Returns true
    /// when anything was added. <paramref name="beforeLaunch"/> runs before an
    /// entry starts, e.g. to close the Start menu. Call on the UI thread (STA).
    /// </summary>
    public static bool Add(ItemsControl menu, string appId, Action? beforeLaunch = null)
    {
        IReadOnlyList<JumpListCategory> categories;
        try
        {
            // A jump list is a small file plus one COM call; the UI thread is
            // STA, which is all the shell calls need.
            categories = JumpLists.Read(appId, MaxEntriesPerCategory);
        }
        catch (Exception ex)
        {
            Log.Error($"jump list for {appId}", ex);
            return false;
        }

        var added = false;
        foreach (var category in categories)
        {
            var entries = category.Entries.Take(MaxEntriesPerCategory).ToList();
            if (entries.All(e => e.IsSeparator)) continue;

            if (added) menu.Items.Add(new Separator());
            menu.Items.Add(Header(category.Title));

            foreach (var entry in entries)
            {
                if (entry.IsSeparator)
                {
                    menu.Items.Add(new Separator());
                    continue;
                }

                var row = new MenuItem
                {
                    Header = new TextBlock
                    {
                        Text = entry.Title,
                        MaxWidth = 320,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                    },
                    ToolTip = entry.ParsingName is { } target ? $"{target} {entry.Arguments}".Trim() : null,
                };
                row.Click += (_, _) =>
                {
                    beforeLaunch?.Invoke();
                    Launch(entry);
                };
                menu.Items.Add(row);
                _ = LoadIconAsync(row, entry);
            }

            added = true;
        }

        return added;
    }

    /// <summary>A small, non-interactive section title.</summary>
    public static MenuItem Header(string title) => new()
    {
        Header = new TextBlock
        {
            Text = title,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Opacity = 0.6,
        },
        IsHitTestVisible = false,
        Focusable = false,
        Padding = new Thickness(8, 6, 8, 2),
    };

    public static void Launch(JumpListEntry entry)
    {
        try
        {
            if (entry.PackagedAppId is not null)
            {
                if (!JumpLists.ActivatePackaged(entry)) Log.Write($"jump list: activation refused for '{entry.Title}'");
                return;
            }

            // The app's own link, exactly as it wrote it: arguments, working
            // directory and identity all come along.
            var target = JumpLists.MaterialiseLink(entry, LinkCache) ?? entry.ParsingName;
            if (target is not null && !ShellLauncher.Open(target))
                Log.Write($"jump list: could not open '{entry.Title}'");
        }
        catch (Exception ex)
        {
            Log.Error($"jump list launch '{entry.Title}'", ex);
        }
    }

    private static async Task LoadIconAsync(MenuItem row, JumpListEntry entry)
    {
        try
        {
            // A materialised link carries the app's chosen icon; a document
            // shows its file type's.
            var raw = await StaTask.Run(() =>
            {
                var name = entry.PackagedAppId is { } appId
                    ? $@"shell:AppsFolder\{appId}"
                    : JumpLists.MaterialiseLink(entry, LinkCache) ?? entry.ParsingName;
                return name is null ? null : IconExtractor.Extract(name, 32);
            }).ConfigureAwait(true);

            if (raw is null) return;
            var image = new Image { Source = raw.ToBitmapSource(), Width = 16, Height = 16 };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            row.Icon = image;
        }
        catch (Exception ex)
        {
            Log.Error($"jump list icon '{entry.Title}'", ex);
        }
    }
}
