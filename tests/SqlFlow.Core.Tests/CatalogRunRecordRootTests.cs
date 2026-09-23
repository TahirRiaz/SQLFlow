using SqlFlow.Catalog;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// A run the CLI records into a repository (<c>sqlflow run flow.yaml --db ... --repo ...</c>) keeps the path the repository
/// sync gives its flow: relative to the repo's recorded root when the flow lies inside it, else to the root of the
/// repository the flow is checked out in, and only outside any repository to the flow's own folder. Before, every recorded
/// run moved its pipeline to the repository's top level until the next sync.
/// </summary>
public sealed class CatalogRunRecordRootTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sqlflow-recordroot-" + Guid.NewGuid().ToString("N")[..8]);

    public CatalogRunRecordRootTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Flow(string relative)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "name: demo\n");
        return path;
    }

    private static string Folder(string file) => Path.GetDirectoryName(file)!;

    [Fact]
    public void A_flow_in_a_clone_is_recorded_relative_to_the_clones_root()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        var flow = Flow(Path.Combine("wells", "cache", "lookups.yaml"));

        var root = CatalogSync.RunRecordRoot(flow, Folder(flow), recordedRoot: null);

        Assert.Equal(_root, root);
        Assert.Equal(Path.Combine("wells", "cache", "lookups.yaml"), Path.GetRelativePath(root, flow));
    }

    [Fact]
    public void A_flow_in_a_worktree_is_recorded_relative_to_the_worktrees_root()
    {
        // A worktree's .git is a file naming the repository it belongs to.
        File.WriteAllText(Path.Combine(_root, ".git"), "gitdir: /elsewhere/.git/worktrees/wt\n");
        var flow = Flow(Path.Combine("flows", "orders.yaml"));

        Assert.Equal(_root, CatalogSync.RunRecordRoot(flow, Folder(flow), recordedRoot: null));
    }

    [Fact]
    public void The_repos_recorded_root_wins_when_the_flow_lies_inside_it()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        var synced = Path.Combine(_root, "estate");
        var flow = Flow(Path.Combine("estate", "flows", "orders.yaml"));

        Assert.Equal(synced, CatalogSync.RunRecordRoot(flow, Folder(flow), synced));

        // A recorded root the flow is not inside (another checkout of the same repository) does not apply.
        var elsewhere = Path.Combine(Path.GetTempPath(), "sqlflow-recordroot-other-" + Guid.NewGuid().ToString("N")[..8]);
        Assert.Equal(_root, CatalogSync.RunRecordRoot(flow, Folder(flow), elsewhere));

        // Nor does a folder whose name merely starts like the recorded root.
        var sibling = Flow(Path.Combine("estate-2", "orders.yaml"));
        Assert.Equal(_root, CatalogSync.RunRecordRoot(sibling, Folder(sibling), synced));
    }

    [Fact]
    public void A_flow_outside_any_repository_is_recorded_relative_to_its_own_folder()
    {
        var flow = Flow(Path.Combine("loose", "orders.yaml"));

        var root = CatalogSync.RunRecordRoot(flow, Folder(flow), recordedRoot: "   ");

        // The temp folder may itself sit inside a repository on a developer's machine; the rule then finds that one,
        // which is still where the flow is checked out. Only where no repository encloses it is its folder the root.
        var enclosing = new DirectoryInfo(Folder(flow));
        while (enclosing is not null && !Directory.Exists(Path.Combine(enclosing.FullName, ".git")) && !File.Exists(Path.Combine(enclosing.FullName, ".git")))
        {
            enclosing = enclosing.Parent;
        }

        Assert.Equal(enclosing?.FullName ?? Folder(flow), root);
    }
}
