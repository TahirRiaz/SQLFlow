using SqlFlow.Core;

namespace SqlFlow.SourceControl;

/// <summary>The outcome of writing a scripted snapshot to the working tree: which files were created, changed,
/// left untouched (byte-identical), or removed because their object no longer exists in the database.</summary>
public sealed record SnapshotWriteResult
{
    public IReadOnlyList<string> Added { get; init; } = [];
    public IReadOnlyList<string> Changed { get; init; } = [];
    public IReadOnlyList<string> Unchanged { get; init; } = [];
    public IReadOnlyList<string> Deleted { get; init; } = [];

    public int TotalChanged => Added.Count + Changed.Count + Deleted.Count;
}

/// <summary>
/// Materializes a <see cref="ScriptedDatabase"/> into the git working tree under its database folder, the
/// faithful equivalent of the legacy stage-to-disk step. It writes one file per object, skips files whose
/// content is already identical (so re-running a stable database produces no git churn), and deletes files for
/// objects that have since been dropped (so the next commit records the deletion). Only the database's own
/// folder is touched, so several scm flows can target distinct database folders in one repository without
/// stepping on each other.
/// </summary>
public static class SnapshotWriter
{
    public static SnapshotWriteResult Write(string workingDirectory, ScriptedDatabase database, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentNullException.ThrowIfNull(database);

        var root = Path.GetFullPath(workingDirectory);
        var databaseDirectory = Path.Combine(root, database.DatabaseName);

        var added = new List<string>();
        var changed = new List<string>();
        var unchanged = new List<string>();
        var desired = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var obj in database.Objects)
        {
            ct.ThrowIfCancellationRequested();
            var fullPath = Path.GetFullPath(Path.Combine(root, obj.RelativePath.Replace('/', Path.DirectorySeparatorChar)));

            // A scripted object must never escape the working tree (a hostile object name with traversal
            // sequences); the rooted-path check is the trust boundary for what we write.
            if (!fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && !string.Equals(fullPath, root, StringComparison.Ordinal))
            {
                throw new SqlFlowException($"Refusing to write '{obj.RelativePath}' outside the repository working tree.");
            }

            desired.Add(NormalizeRelative(Path.GetRelativePath(root, fullPath)));

            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            if (File.Exists(fullPath) && string.Equals(File.ReadAllText(fullPath), obj.Sql, StringComparison.Ordinal))
            {
                unchanged.Add(obj.RelativePath);
                continue;
            }

            var existed = File.Exists(fullPath);
            File.WriteAllText(fullPath, obj.Sql);
            (existed ? changed : added).Add(obj.RelativePath);
        }

        var deleted = DeleteremovedFiles(root, databaseDirectory, desired, ct);

        return new SnapshotWriteResult
        {
            Added = added,
            Changed = changed,
            Unchanged = unchanged,
            Deleted = deleted,
        };
    }

    /// <summary>Removes <c>.sql</c> files under the database folder that the current snapshot did not produce,
    /// so a dropped object leaves the repository as a tracked deletion.</summary>
    private static IReadOnlyList<string> DeleteremovedFiles(
        string root, string databaseDirectory, HashSet<string> desired, CancellationToken ct)
    {
        if (!Directory.Exists(databaseDirectory))
        {
            return [];
        }

        var deleted = new List<string>();
        foreach (var existing in Directory.EnumerateFiles(databaseDirectory, "*.sql", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var relative = NormalizeRelative(Path.GetRelativePath(root, existing));
            if (!desired.Contains(relative))
            {
                File.Delete(existing);
                deleted.Add(relative);
            }
        }

        deleted.Sort(StringComparer.Ordinal);
        return deleted;
    }

    private static string NormalizeRelative(string path) => path.Replace(Path.DirectorySeparatorChar, '/');
}
