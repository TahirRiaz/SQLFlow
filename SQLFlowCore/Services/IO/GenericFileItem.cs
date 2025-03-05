using System;

namespace SQLFlowCore.Services.IO
{
    /// <summary>
    /// Represents a generic file item in the system.
    /// </summary>
    internal class GenericFileItem
    {
        /// <summary>
        /// Gets or sets the name of the file.
        /// </summary>
        internal string Name { get; set; }
        /// <summary>
        /// Gets or sets the full path of the file.
        /// </summary>
        internal string FullPath { get; set; }
        /// <summary>
        /// Gets or sets the directory name of the file.
        /// </summary>
        internal string DirectoryName { get; set; }
        /// <summary>
        /// Gets or sets the last modified date and time of the file.
        /// </summary>
        internal DateTime LastModified { get; set; }
        /// <summary>
        /// Gets or sets the content length of the file.
        /// </summary>
        internal long ContentLength { get; set; }
        /// <summary>
        /// Gets or sets the creation time of the file.
        /// </summary>
        internal DateTime CreationTime { get; set; }
        /// <summary>
        /// Gets or sets the last write time of the file.
        /// </summary>
        internal DateTime LastWriteTime { get; set; }

    }
}
