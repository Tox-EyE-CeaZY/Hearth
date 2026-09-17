using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Hearth.Core.Shell;

/// <summary>
/// File search through the Windows Search index.
///
/// Queried with SQL through ADO (ADODB ships with Windows, and the index is
/// exposed as the Search.CollatorDSO provider), late-bound so Hearth needs no
/// OLE DB package. Enumerating a <c>search-ms:</c> shell folder was tried
/// first and returned nothing when enumerated outside Explorer.
/// </summary>
public static class IndexSearch
{
    private const string ConnectionString = "Provider=Search.CollatorDSO;Extended Properties='Application=Windows';";

    /// <summary>
    /// Files and folders whose name contains every word of the query, newest
    /// first. Blocking; call it on an STA thread. Returns what it has if
    /// <paramref name="cancel"/> fires.
    /// </summary>
    public static IReadOnlyList<LauncherItem> Search(string query, int max, CancellationToken cancel)
    {
        var results = new List<LauncherItem>(max);
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0) return results;

        var type = Type.GetTypeFromProgID("ADODB.Connection");
        if (type is null) return results;

        dynamic? connection = null;
        dynamic? rows = null;
        try
        {
            connection = Activator.CreateInstance(type)!;
            connection.Open(ConnectionString);

            var sql = new StringBuilder($"SELECT TOP {max} System.ItemPathDisplay, System.ItemNameDisplay FROM SYSTEMINDEX WHERE SCOPE='file:'");
            foreach (var word in words) sql.Append($" AND System.ItemNameDisplay LIKE '%{EscapeLike(word)}%'");
            sql.Append(" ORDER BY System.DateModified DESC");

            rows = connection.Execute(sql.ToString());
            while (!(bool)rows.EOF && !cancel.IsCancellationRequested)
            {
                var path = rows.Fields.Item(0).Value as string;
                var name = rows.Fields.Item(1).Value as string;
                if (!string.IsNullOrEmpty(path))
                {
                    var isFolder = Directory.Exists(path);
                    var extension = Path.GetExtension(path);
                    if (name is not null && (extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase) || extension.Equals(".url", StringComparison.OrdinalIgnoreCase)))
                        name = Path.GetFileNameWithoutExtension(name);
                    results.Add(new LauncherItem
                    {
                        Id = path,
                        DisplayName = string.IsNullOrEmpty(name) ? Path.GetFileName(path) : name,
                        Kind = isFolder ? LauncherItemKind.Folder : LauncherItemKind.File,
                        Target = path,
                        FileSystemPath = path,
                    });
                }
                rows.MoveNext();
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException
                                       or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            Debug.WriteLine($"[Hearth] index search failed: {ex.Message}");
        }
        finally
        {
            try { rows?.Close(); } catch (COMException) { }
            try { connection?.Close(); } catch (COMException) { }
            if (rows is not null) Marshal.FinalReleaseComObject(rows);
            if (connection is not null) Marshal.FinalReleaseComObject(connection);
        }

        return results;
    }

    /// <summary>
    /// Makes user text safe inside a quoted LIKE pattern: quotes are doubled,
    /// and the wildcard characters are bracketed so they match literally.
    /// </summary>
    public static string EscapeLike(string text)
    {
        var escaped = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            switch (c)
            {
                case '\'': escaped.Append("''"); break;
                case '%' or '_' or '[': escaped.Append('[').Append(c).Append(']'); break;
                default: escaped.Append(c); break;
            }
        }
        return escaped.ToString();
    }
}
