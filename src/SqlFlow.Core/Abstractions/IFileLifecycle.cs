namespace SqlFlow.Core.Abstractions;

/// <summary>
/// Independent, composable file operations that can be hooked into a source ingestion - copy, zip,
/// and delete. Each is callable on its own; the CSV reader invokes them after a successful load,
/// driven by the metadata (CopyToPath / ZipToPath / SrcDeleteIngested / SrcDeleteAtPath).
/// </summary>
public interface IFileLifecycle
{
    /// <summary>Copy a file to a directory or explicit destination path.</summary>
    void Copy(string sourceFile, string targetPath, bool overwrite = true);

    /// <summary>Compress a file into a zip at a directory or explicit destination path; returns the zip path.</summary>
    string Zip(string sourceFile, string targetPath);

    /// <summary>Delete a file if it exists.</summary>
    void Delete(string file);
}
