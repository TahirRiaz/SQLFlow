using Azure.Storage.Files.DataLake.Models;
using System.IO;
namespace SQLFlowCore.Services.IO
{
    /// <summary>
    /// Represents an adapter for the PathItem class.
    /// </summary>
    /// <remarks>
    /// This class implements the IFileItemAdapter interface and provides a method to convert a PathItem object to a GenericFileItem object.
    /// </summary>
    internal class PathItemAdapter : IFileItemAdapter<PathItem>
    {
        /// <summary>
        /// Converts a PathItem object to a GenericFileItem object.
        /// </summary>
        /// <param name="pathItem">The PathItem object to convert.</param>
        /// <returns>A GenericFileItem object that represents the given PathItem object.</returns>
        public GenericFileItem ConvertToGenericFileItem(PathItem pathItem)
        {
            return new GenericFileItem
            {
                Name = Path.GetFileName(pathItem.Name),
                FullPath = pathItem.Name,
                DirectoryName = Path.GetDirectoryName(pathItem.Name),
                ContentLength = pathItem.ContentLength.HasValue ? pathItem.ContentLength.Value : 0,
                LastModified = pathItem.LastModified.DateTime,
                LastWriteTime = pathItem.LastModified.DateTime,
                CreationTime = pathItem.LastModified.DateTime
            };
        }
    }
}

