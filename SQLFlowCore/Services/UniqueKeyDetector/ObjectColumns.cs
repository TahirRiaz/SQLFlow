namespace SQLFlowCore.Services.UniqueKeyDetector
{
    public class ObjectColumns
    {
        // Assuming the QUOTENAME(COLUMN_NAME) returns the name of the column,
        // this property will hold that name.
        public string ObjectName { get; set; }

        public string ColumnName { get; set; }

        public int Ordinal { get; set; }
        // This corresponds to the CAST(1 AS BIT) in your SQL,
        // indicating whether the column is selected.
        public bool Selected { get; set; }
    }
}
