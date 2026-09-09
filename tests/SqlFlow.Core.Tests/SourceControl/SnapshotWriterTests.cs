using SqlFlow.Core;
using SqlFlow.SourceControl;
using Xunit;

namespace SqlFlow.Tests.SourceControl;

/// <summary>
/// The snapshot writer's on-disk contract: it materializes one file per object in the database folder, leaves
/// byte-identical files untouched (no git churn), records a dropped object as a deletion, and refuses to write
/// outside the working tree.
/// </summary>
public sealed class SnapshotWriterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sqlflow_scm_writer_" + Guid.NewGuid().ToString("N"));

    public SnapshotWriterTests() => Directory.CreateDirectory(_dir);

    private static ScriptedDatabase Db(string name, params (string Folder, string Schema, string ObjectName, string Sql)[] objects)
        => new()
        {
            DatabaseName = name,
            Objects = objects.Select(o => new ScriptedObject
            {
                Folder = o.Folder,
                Schema = o.Schema,
                Name = o.ObjectName,
                RelativePath = $"{name}/{o.Folder}/{o.Schema}.{o.ObjectName}.sql",
                Sql = o.Sql,
            }).ToList(),
        };

    [Fact]
    public void Write_CreatesOneFilePerObject()
    {
        var result = SnapshotWriter.Write(_dir, Db("Warehouse",
            ("Table", "dbo", "Customer", "CREATE TABLE [dbo].[Customer] (...);\n"),
            ("View", "dbo", "vCustomer", "CREATE VIEW [dbo].[vCustomer] AS SELECT 1;\n")));

        Assert.Equal(2, result.Added.Count);
        Assert.Empty(result.Unchanged);
        Assert.True(File.Exists(Path.Combine(_dir, "Warehouse", "Table", "dbo.Customer.sql")));
        Assert.True(File.Exists(Path.Combine(_dir, "Warehouse", "View", "dbo.vCustomer.sql")));
        Assert.Equal("CREATE TABLE [dbo].[Customer] (...);\n",
            File.ReadAllText(Path.Combine(_dir, "Warehouse", "Table", "dbo.Customer.sql")));
    }

    [Fact]
    public void Write_IsIdempotent_ForUnchangedObjects()
    {
        var db = Db("Warehouse", ("Table", "dbo", "Customer", "CREATE TABLE [dbo].[Customer] (...);\n"));
        SnapshotWriter.Write(_dir, db);

        var second = SnapshotWriter.Write(_dir, db);

        Assert.Empty(second.Added);
        Assert.Empty(second.Changed);
        Assert.Single(second.Unchanged);
        Assert.Equal(0, second.TotalChanged);
    }

    [Fact]
    public void Write_RecordsAChangedObject()
    {
        SnapshotWriter.Write(_dir, Db("Warehouse", ("Table", "dbo", "Customer", "v1\n")));
        var second = SnapshotWriter.Write(_dir, Db("Warehouse", ("Table", "dbo", "Customer", "v2\n")));

        Assert.Single(second.Changed);
        Assert.Equal("v2\n", File.ReadAllText(Path.Combine(_dir, "Warehouse", "Table", "dbo.Customer.sql")));
    }

    [Fact]
    public void Write_DeletesFilesForDroppedObjects()
    {
        SnapshotWriter.Write(_dir, Db("Warehouse",
            ("Table", "dbo", "Customer", "a\n"),
            ("Table", "dbo", "Orders", "b\n")));

        // Orders is gone from the database on the next snapshot.
        var second = SnapshotWriter.Write(_dir, Db("Warehouse", ("Table", "dbo", "Customer", "a\n")));

        Assert.Single(second.Deleted);
        Assert.Equal("Warehouse/Table/dbo.Orders.sql", second.Deleted[0]);
        Assert.False(File.Exists(Path.Combine(_dir, "Warehouse", "Table", "dbo.Orders.sql")));
        Assert.True(File.Exists(Path.Combine(_dir, "Warehouse", "Table", "dbo.Customer.sql")));
    }

    [Fact]
    public void Write_LeavesOtherDatabaseFoldersAlone()
    {
        // A second database folder in the same repository must survive a snapshot of the first.
        var other = Path.Combine(_dir, "OtherDb", "Table");
        Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(other, "dbo.Keep.sql"), "keep\n");

        SnapshotWriter.Write(_dir, Db("Warehouse", ("Table", "dbo", "Customer", "a\n")));

        Assert.True(File.Exists(Path.Combine(other, "dbo.Keep.sql")));
    }

    [Fact]
    public void Write_RefusesPathTraversal()
    {
        var db = new ScriptedDatabase
        {
            DatabaseName = "Warehouse",
            Objects =
            [
                new ScriptedObject
                {
                    Folder = "Table", Schema = "dbo", Name = "evil",
                    RelativePath = "../../escape.sql", Sql = "x\n",
                },
            ],
        };

        Assert.Throws<SqlFlowException>(() => SnapshotWriter.Write(_dir, db));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
