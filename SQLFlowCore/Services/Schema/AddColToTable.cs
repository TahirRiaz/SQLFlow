using Microsoft.SqlServer.Management.Smo;

namespace SQLFlowCore.Services.Schema
{
    /// <summary>
    /// Provides methods to add columns to a table.
    /// </summary>
    internal class AddColToTable
    {
        internal static void AddColumn(Table Tbl, Column column)
        {
            //Identity requried only for the SkeyTable
            if (SmoHelper.ColExists(column.Name, Tbl) == false)
            {
                Tbl.Columns.Add(column);
            }
        }

        /// <summary>
        /// Adds a system column of integer type to the specified table.
        /// </summary>
        /// <param name="Tbl">The table to which the column is added.</param>
        /// <param name="SurrogateColumn">The name of the column to add.</param>
        /// <returns>The added column.</returns>
        internal static Column SysColumnInt(Table Tbl, string SurrogateColumn)
        {
            //Identity requried only for the SkeyTable
            if (SmoHelper.ColExists(SurrogateColumn, Tbl) == false)
            {
                Column col2 = new Column(Tbl, SurrogateColumn, DataType.Int)
                {
                    DataType =
                        {
                            MaximumLength = 4,
                            NumericPrecision = 10
                        },
                    Nullable = true
                };
                return col2;
            }
            else
            {
                Column col2 = Tbl.Columns[SurrogateColumn];
                return col2;
            }
        }

        /// <summary>
        /// Adds a system column of DateTime type to the specified table.
        /// </summary>
        /// <param name="Tbl">The table to which the column is added.</param>
        /// <param name="SysColName">The name of the column to add.</param>
        /// <returns>The added column.</returns>
        internal static Column SysColumnDt(Table Tbl, string SysColName)
        {
            if (SmoHelper.ColExists(SysColName, Tbl) == false)
            {
                Column col2 = new Column(Tbl, SysColName, DataType.DateTime)
                {
                    DataType =
                        {
                            MaximumLength = 8,
                            NumericPrecision = 23,
                            NumericScale = 3
                        },
                    Nullable = true
                };
                return col2;
            }
            else
            {
                Column col2 = Tbl.Columns[SysColName];
                return col2;
            }
        }

        /// <summary>
        /// Adds a system column of string type to the specified table.
        /// </summary>
        /// <param name="Tbl">The table to which the column is added.</param>
        /// <param name="SysColName">The name of the column to add.</param>
        /// <param name="Length">The maximum length of the string.</param>
        /// <returns>The added column.</returns>
        internal static Column SysColumnStr(Table Tbl, string SysColName, int Length)
        {
            if (SmoHelper.ColExists(SysColName, Tbl) == false)
            {
                Column col2 = new Column(Tbl, SysColName, DataType.VarChar(Length))
                {
                    DataType =
                        {
                            MaximumLength = Length
                        },
                    Nullable = true
                };
                return col2;
            }
            else
            {
                Column col2 = Tbl.Columns[SysColName];
                return col2;
            }
        }

    }
}

