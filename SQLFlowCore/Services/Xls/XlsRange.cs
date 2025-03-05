namespace SQLFlowCore.Services.Xls
{
    /// <summary>
    /// Represents a range in an Excel spreadsheet.
    /// </summary>
    public class XlsRange
    {
        /// <summary>
        /// Gets or sets a value indicating whether this instance represents a range.
        /// </summary>
        /// <value>
        ///   <c>true</c> if this instance is a range; otherwise, <c>false</c>.
        /// </value>
        internal bool IsRange { get; set; } = false;
        /// <summary>
        /// Gets or sets the index of the starting column in the range.
        /// </summary>
        /// <value>
        /// The index of the starting column.
        /// </value>
        internal int FromColumnIndex { get; set; } = 0;
        /// <summary>
        /// Gets or sets the index of the ending column in the range.
        /// </summary>
        /// <value>
        /// The index of the ending column.
        /// </value>
        internal int ToColumnIndex { get; set; } = 0;
        /// <summary>
        /// Gets or sets the starting row in the range.
        /// </summary>
        /// <value>
        /// The starting row.
        /// </value>
        internal int FromRow { get; set; } = 0;
        /// <summary>
        /// Gets or sets the ending row in the range.
        /// </summary>
        /// <value>
        /// The ending row.
        /// </value>
        internal int ToRow { get; set; } = 0;
    }
}
