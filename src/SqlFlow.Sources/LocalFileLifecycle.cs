using System.IO.Compression;
using SqlFlow.Core.Abstractions;

namespace SqlFlow.Sources;

/// <summary>Local-filesystem implementation of the independent file-lifecycle operations.</summary>
public sealed class LocalFileLifecycle : IFileLifecycle
{
    public void Copy(string sourceFile, string targetPath, bool overwrite = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceFile);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);

        var destination = ResolveDestination(targetPath, Path.GetFileName(sourceFile));
        File.Copy(sourceFile, destination, overwrite);
    }

    public string Zip(string sourceFile, string targetPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceFile);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);

        var destination = ResolveDestination(targetPath, Path.GetFileNameWithoutExtension(sourceFile) + ".zip");
        if (File.Exists(destination))
        {
            File.Delete(destination);
        }

        using var archive = ZipFile.Open(destination, ZipArchiveMode.Create);
        archive.CreateEntryFromFile(sourceFile, Path.GetFileName(sourceFile));
        return destination;
    }

    public void Delete(string file)
    {
        if (File.Exists(file))
        {
            File.Delete(file);
        }
    }

    private static string ResolveDestination(string targetPath, string defaultFileName)
    {
        // Treat the target as a directory when it exists as one or has no file extension.
        var isDirectory = Directory.Exists(targetPath) || string.IsNullOrEmpty(Path.GetExtension(targetPath));
        var destination = isDirectory ? Path.Combine(targetPath, defaultFileName) : targetPath;

        var directory = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        return destination;
    }
}
