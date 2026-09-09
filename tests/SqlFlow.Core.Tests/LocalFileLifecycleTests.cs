using SqlFlow.Sources;
using Xunit;

namespace SqlFlow.Tests;

public sealed class LocalFileLifecycleTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sqlflow_life_" + Guid.NewGuid().ToString("N"));
    private readonly LocalFileLifecycle _lifecycle = new();

    public LocalFileLifecycleTests() => Directory.CreateDirectory(_dir);

    private string MakeFile(string name = "src.csv")
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, "a,b\n1,2\n");
        return path;
    }

    [Fact]
    public void Copy_ToDirectory_PlacesFileWithSameName()
    {
        var file = MakeFile();
        var target = Path.Combine(_dir, "archive");

        _lifecycle.Copy(file, target);

        Assert.True(File.Exists(Path.Combine(target, "src.csv")));
    }

    [Fact]
    public void Zip_CreatesArchiveContainingFile()
    {
        var file = MakeFile("data.csv");
        var target = Path.Combine(_dir, "zips");

        var zipPath = _lifecycle.Zip(file, target);

        Assert.True(File.Exists(zipPath));
        Assert.EndsWith(".zip", zipPath, StringComparison.Ordinal);
    }

    [Fact]
    public void Delete_RemovesFile()
    {
        var file = MakeFile();

        _lifecycle.Delete(file);

        Assert.False(File.Exists(file));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
