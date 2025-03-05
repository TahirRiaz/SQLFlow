namespace SQLFlowCore.Services.Schema
{
    /// <summary>
    /// Represents the source and target columns for a SQL operation.
    /// </summary>
    /// <remarks>
    /// This class is used to hold the metadata of source and target columns involved in a SQL operation.
    /// It includes properties for the database, schema, object name, column name, ordinal position, data type, length, precision, scale, max length, default value, and collation name for both source and target columns.
    /// </remarks>
    internal class SrcTrgColumns
    {
        internal string srcObjDatabase { get; set; }
        internal string srcObjSchema { get; set; }
        internal string srcObjName { get; set; }
        internal string srcColName { get; set; }
        internal int srcOrdinalPosition { get; set; }
        internal string srcDataType { get; set; }
        internal int srcLength { get; set; }
        internal int srcPrecision { get; set; }
        internal int srcScale { get; set; }
        internal int srcMaxLength { get; set; }
        internal string srcColumnDefault { get; set; }
        internal string srcCollationName { get; set; }
        internal string trgObjDatabase { get; set; }
        internal string trgObjSchema { get; set; }
        internal string trgObjName { get; set; }
        internal string trgColName { get; set; }
        internal int trgOrdinalPosition { get; set; }
        internal string trgDataType { get; set; }
        internal int trgLength { get; set; }
        internal int trgPrecision { get; set; }
        internal int trgScale { get; set; }
        internal int trgMaxLength { get; set; }
        internal string trgColumnDefault { get; set; }
        internal string trgCollationName { get; set; }
    }


}
