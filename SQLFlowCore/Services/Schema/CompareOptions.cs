using System.Collections.Generic;

namespace SQLFlowCore.Services.Schema
{
    /// <summary>
    /// Represents options for comparing SQL schemas.
    /// </summary>
    internal class CompareOptions
    {
        /// <summary>
        /// Gets or sets a value indicating whether to synchronize source columns.
        /// </summary>
        internal bool SyncSrcColumns { get; set; }
        /// <summary>
        /// Gets or sets a value indicating whether to synchronize nullable columns.
        /// </summary>
        internal bool SyncNullable { get; set; }
        /// <summary>
        /// Gets or sets a value indicating whether to synchronize data types.
        /// </summary>
        internal bool SyncDataType { get; set; }
        /// <summary>
        /// Gets or sets a value indicating whether to synchronize primary keys.
        /// </summary>
        internal bool SyncPrimaryKey { get; set; }
        /// <summary>
        /// Gets or sets a value indicating whether to synchronize indexes.
        /// </summary>
        internal bool SyncIndexes { get; set; }
        /// <summary>
        /// Gets or sets a value indicating whether to synchronize identity columns.
        /// </summary>
        internal bool SyncIdentity { get; set; }
        /// <summary>
        /// Gets or sets a value indicating whether to synchronize collation.
        /// </summary>
        internal bool SyncCollation { get; set; }
        /// <summary>
        /// Gets or sets a value indicating whether to compare columns.
        /// </summary>
        internal bool CompareColumns { get; set; }
        /// <summary>
        /// Gets or sets a value indicating whether to compare data types.
        /// </summary>
        internal bool CompareDataType { get; set; }
        /// <summary>
        /// Gets or sets a value indicating whether to add missing columns to target.
        /// </summary>
        internal bool AddMissingColToTarget { get; set; }
        /// <summary>
        /// Gets or sets a list of columns to ignore during comparison.
        /// </summary>
        internal List<string> IgnoreColumns { get; set; }
        /// <summary>
        /// Initializes a new instance of the <see cref="CompareOptions"/> class.
        /// </summary>
        internal CompareOptions()
        {
            AddMissingColToTarget = true;
            IgnoreColumns = new List<string>();
            CompareColumns = true;
            CompareDataType = true;
            SyncSrcColumns = true;
            SyncNullable = false;
            SyncDataType = true;
            SyncPrimaryKey = false;
            SyncIndexes = false;
            SyncIdentity = false;
            SyncCollation = false;
        }
    }
}

