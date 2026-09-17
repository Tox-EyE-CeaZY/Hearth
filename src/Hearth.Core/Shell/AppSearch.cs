namespace Hearth.Core.Shell;

/// <summary>How well an app name matches typed text, shared by every search box.</summary>
public static class AppSearch
{
    /// <summary>
    /// 3 when the name starts with the query; 2 when a word, or the initials,
    /// do ("vsc" for Visual Studio Code); 1 when the name merely contains it;
    /// 0 for no match.
    /// </summary>
    public static int Score(string name, string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return 0;
        query = query.Trim();

        if (name.StartsWith(query, StringComparison.CurrentCultureIgnoreCase)) return 3;

        var words = name.Split([' ', '-', '_', '.', '('], StringSplitOptions.RemoveEmptyEntries);
        foreach (var word in words)
        {
            if (word.StartsWith(query, StringComparison.CurrentCultureIgnoreCase)) return 2;
        }

        var initials = string.Concat(words.Select(w => w[0]));
        if (query.Length > 1 && initials.StartsWith(query, StringComparison.CurrentCultureIgnoreCase)) return 2;

        return name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ? 1 : 0;
    }

    /// <summary>Apps ranked for a query: best score first, then alphabetically.</summary>
    public static IEnumerable<LauncherItem> Rank(IEnumerable<LauncherItem> apps, string query) =>
        apps.Select(app => (app, score: Score(app.DisplayName, query)))
            .Where(m => m.score > 0)
            .OrderByDescending(m => m.score)
            .ThenBy(m => m.app.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .Select(m => m.app);
}
