namespace Hearth.Core.Shell;

public enum LauncherItemKind
{
    /// <summary>An installed application from shell:AppsFolder (Win32 or MSIX).</summary>
    App,
    /// <summary>A filesystem folder the user wants one click away.</summary>
    Folder,
    /// <summary>A loose file or document.</summary>
    File,
    /// <summary>A .lnk or .url sitting on the real desktop.</summary>
    Shortcut,
    /// <summary>A user-created grouping of other items. Has no shell identity.</summary>
    Group,
}

/// <summary>
/// One launchable thing. Deliberately flat and serialisable: the grid, the
/// icon pipeline and the layout store all key off <see cref="Id"/>.
/// </summary>
public sealed class LauncherItem
{
    /// <summary>
    /// Stable identity across runs. For shell-backed items this is the parsing
    /// name (AUMID or full path), which is what we also hand to the launcher.
    /// </summary>
    public required string Id { get; init; }

    public required string DisplayName { get; set; }

    public required LauncherItemKind Kind { get; init; }

    /// <summary>
    /// What ShellExecute is pointed at. Same as <see cref="Id"/> for shell
    /// items; null for groups.
    /// </summary>
    public string? Target { get; init; }

    /// <summary>
    /// Set when the item is a filesystem object, so we can offer "open file
    /// location" and watch for deletion.
    /// </summary>
    public string? FileSystemPath { get; init; }

    /// <summary>User override, e.g. a hand-picked icon pack entry.</summary>
    public string? IconOverridePath { get; set; }

    public override string ToString() => $"{Kind}: {DisplayName}";
}
