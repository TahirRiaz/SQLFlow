using System.IO;

namespace SQLFlowCore.Services.IO
{
    /// <summary>
    /// Represents an adapter for the <see cref="FileSystemInfo"/> class.
    /// </summary>
    internal class FileSystemInfoAdapter : IFileItemAdapter<FileSystemInfo>
    {
        /// <summary>
        /// Converts a <see cref="FileSystemInfo"/> object to a <see cref="GenericFileItem"/> object.
        /// </summary>
        /// <param name="fileInfo">The <see cref="FileSystemInfo"/> object to convert.</param>
        /// <returns>A <see cref="GenericFileItem"/> object that represents the converted <see cref="FileSystemInfo"/> object.</returns>
        public GenericFileItem ConvertToGenericFileItem(FileSystemInfo fileInfo)
        {
            return new GenericFileItem
            {
                Name = fileInfo.Name,
                FullPath = fileInfo.FullName,
                LastModified = fileInfo.LastWriteTime,
                DirectoryName = Path.GetDirectoryName(fileInfo.FullName),
                ContentLength = fileInfo is FileInfo fi ? fi.Length : 0,
                CreationTime = fileInfo.CreationTime,
                LastWriteTime = fileInfo.LastWriteTime
            };
        }
    }
}
