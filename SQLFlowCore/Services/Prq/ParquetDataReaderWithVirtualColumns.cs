using Parquet;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Threading.Tasks;

namespace SQLFlowCore.Services.Prq
{
    /// <summary>
    /// Represents a data reader for Parquet data with support for virtual columns.
    /// Implements the <see cref="IDataReader"/> and <see cref="IDataRecord"/> interfaces.
    /// </summary>
    class ParquetDataReaderWithVirtualColumns : IDataReader, IDataRecord
    {
        private readonly ParquetReader _reader;
        private ParquetRowGroupReader _groupReader;
        private Parquet.Data.DataColumn[] _columns;
        private Parquet.Schema.DataField[] _fields;
        private int _currentRowGroup;
        private int _currentRow;
        private SortedList<int, DataColumn> _virtualColumns;
        private bool _readNextGroup = false;

        /// <summary>
        /// Initializes a new instance of the <see cref="ParquetDataReaderWithVirtualColumns"/> class.
        /// </summary>
        /// <param name="reader">The Parquet reader.</param>
        /// <param name="currentRowGroup">The current row group.</param>
        /// <param name="readNextGroup">Whether to read the next group.</param>
        /// <param name="virtualColumns">The virtual columns.</param>
        public ParquetDataReaderWithVirtualColumns(ParquetReader reader, int currentRowGroup, bool readNextGroup, SortedList<int, DataColumn> virtualColumns)
        {
            _virtualColumns = virtualColumns;

            _reader = reader ?? throw new ArgumentNullException(nameof(reader));
            _fields = reader.Schema.DataFields;
            _columns = new Parquet.Data.DataColumn[reader.Schema.Fields.Count];
            _currentRowGroup = currentRowGroup;
            _currentRow = -1;
            _readNextGroup = readNextGroup;
            _groupReader = reader.OpenRowGroupReader(_currentRowGroup);
            LoadColumnsAsync();
        }

        /// <summary>
        /// Asynchronously loads the columns from the current row group into memory.
        /// </summary>
        /// <remarks>
        /// This method iterates over the fields in the schema of the Parquet reader, 
        /// and for each field, it reads the corresponding column from the current row group.
        /// The result is stored in the `_columns` array.
        /// </remarks>
        private void LoadColumnsAsync()
        {
            for (int i = 0; i < _reader.Schema.Fields.Count; i++)
            {
                _columns[i] = _groupReader.ReadColumnAsync(_fields[i]).Result;
            }
        }

        /// <summary>
        /// Advances the data reader to the next result.
        /// </summary>
        /// <returns>
        /// true if there are more rows; otherwise, false.
        /// </returns>
        /// <remarks>
        /// This method increments the current row group, checks if it has reached the end of the row groups, 
        /// and if not, opens the next row group for reading and loads its columns into memory. 
        /// If the end of the row groups is reached, the method returns false.
        /// </remarks>
        public bool NextResult()
        {
            _currentRowGroup++;
            if (_currentRowGroup >= _reader.RowGroupCount) return false;
            _currentRow = -1;
            _groupReader = _reader.OpenRowGroupReader(_currentRowGroup);
            Task.Run(LoadColumnsAsync).Wait(); //wait for the columns to finish loading
            return true;
        }

        /// <summary>
        /// Retrieves the schema table for the current Parquet data reader.
        /// </summary>
        /// <returns>
        /// A <see cref="DataTable"/> that contains the schema information for the current Parquet data reader.
        /// Each row in the returned table corresponds to a column from the Parquet data, 
        /// and each column in the table represents a property of the column from the Parquet data.
        /// </returns>
        public DataTable GetSchemaTable()
        {
            DataTable schemaTable = new DataTable("SchemaTable");

            schemaTable.Columns.Add(SchemaTableColumn.ColumnName, typeof(string));
            schemaTable.Columns.Add(SchemaTableColumn.ColumnOrdinal, typeof(int));
            schemaTable.Columns.Add(SchemaTableColumn.ColumnSize, typeof(int));
            schemaTable.Columns.Add(SchemaTableColumn.DataType, typeof(Type));
            schemaTable.Columns.Add(SchemaTableColumn.AllowDBNull, typeof(bool));
            //schemaTable.Columns.Add(SchemaTableColumn.read, typeof(bool)));
            schemaTable.Columns.Add(SchemaTableColumn.IsUnique, typeof(bool));
            schemaTable.Columns.Add(SchemaTableColumn.IsKey, typeof(bool));

            for (int i = 0; i < _reader.Schema.Fields.Count; i++)
            {
                DataRow row = schemaTable.NewRow();
                row[SchemaTableColumn.ColumnName] = _reader.Schema.Fields[i].Name;
                row[SchemaTableColumn.ColumnOrdinal] = i;
                row[SchemaTableColumn.ColumnSize] = -1; // Unknown size
                row[SchemaTableColumn.DataType] = _reader.Schema.DataFields[i].ClrType;
                row[SchemaTableColumn.AllowDBNull] = true; // Assume nullable
                //row[SchemaTableColumn.IsReadOnly] = true; // ReadOnly from data source
                row[SchemaTableColumn.IsUnique] = false; // Assume not unique
                row[SchemaTableColumn.IsKey] = false; // Assume not primary key

                schemaTable.Rows.Add(row);
            }

            return schemaTable;
        }


        /// <summary>
        /// Retrieves the value for the given column in the current row.
        /// </summary>
        /// <param name="i">The zero-based column ordinal.</param>
        /// <returns>
        /// The value of the specified column in its native format. 
        /// This method returns a DBNull for null database columns or the default value for virtual columns.
        /// </returns>
        /// <exception cref="IndexOutOfRangeException">The index passed was outside the range of 0 through FieldCount.</exception>
        public object GetValue(int i)
        {
            //if (i < 0 || i >= _columns.Length)
            //{
            //    throw new IndexOutOfRangeException();
            //}
            //return _columns[i]?.Data.GetValue(_currentRow) ?? throw new ArgumentNullException(nameof(i));

            if (i <= _columns.Length)
            {
                return _columns[i]?.Data.GetValue(_currentRow) ?? DBNull.Value;
            }
            else
            {
                return _virtualColumns[i].DefaultValue;
            }
        }

        /// <summary>
        /// Advances the reader to the next record.
        /// </summary>
        /// <returns>
        /// true if there are more rows; otherwise, false.
        /// </returns>
        /// <remarks>
        /// This method increments the current row, checks if it has reached the end of the rows in the current row group,
        /// and if so, depending on the _readNextGroup flag, either opens the next row group for reading and loads its columns into memory,
        /// or returns false indicating the end of the data. If the end of the row groups is reached, the method also returns false.
        /// </remarks>
        public bool Read()
        {
            _currentRow++;
            if (_currentRow >= _groupReader.RowCount)
            {
                if (_readNextGroup)
                {
                    _currentRowGroup++;
                    if (_currentRowGroup >= _reader.RowGroupCount) return false;
                    _currentRow = 0;
                    _groupReader = _reader.OpenRowGroupReader(_currentRowGroup);
                    LoadColumnsAsync();
                }
                else
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Gets the data type name of the specified column.
        /// </summary>
        /// <param name="i">The zero-based column ordinal.</param>
        /// <returns>
        /// The data type name of the specified column.
        /// </returns>
        /// <remarks>
        /// This method returns the full name of the type of the data contained in the specified column.
        /// </remarks>
        public string GetDataTypeName(int i)
        {
            return _columns[i].Data.GetType().ToString();
        }

        /// <summary>
        /// Gets the name of the column at the specified index.
        /// </summary>
        /// <param name="i">The zero-based index of the column to get the name of.</param>
        /// <returns>
        /// The name of the column at the specified index. If the index is within the range of the actual columns, 
        /// it returns the name of the corresponding column. If the index is outside the range of the actual columns, 
        /// it returns the name of the corresponding virtual column.
        /// </returns>
        /// <exception cref="IndexOutOfRangeException">Thrown when the index is less than zero.</exception>
        public string GetName(int i)
        {
            string rValue = string.Empty;
            if (i < 0 || i <= _columns.Length)
            {
                rValue = _columns[i].Field.Name;
            }
            else
            {
                rValue = _virtualColumns[i].ColumnName;
            }

            return rValue;
        }

        /// <summary>
        /// Gets the ordinal of the column with the specified name.
        /// </summary>
        /// <param name="name">The name of the column.</param>
        /// <returns>
        /// The zero-based ordinal of the column with the specified name. 
        /// If the name specified is not found, returns -1.
        /// </returns>
        /// <remarks>
        /// This method first searches for the column name in the Parquet data columns. 
        /// If not found, it then searches in the virtual columns. 
        /// If the column name is found in the virtual columns, the key of the virtual column is returned as the ordinal.
        /// </remarks>
        public int GetOrdinal(string name)
        {
            int Index = Array.FindIndex(_columns, x => x.Field.Name == name);
            if (Index < 0)
            {
                Index = Array.FindIndex(_virtualColumns.Values.ToArray(), x => x.ColumnName == name);

                if (Index >= 0)
                {
                    Index = _virtualColumns.ElementAt(Index).Key;
                }
            }

            return Index;
        }

        /// <summary>
        /// Closes the ParquetDataReaderWithVirtualColumns and releases any resources associated with it.
        /// </summary>
        /// <remarks>
        /// This method disposes the current ParquetRowGroupReader, if any, and sets it to null.
        /// </remarks>
        public void Close()
        {
            _groupReader?.Dispose();
            _groupReader = null;
        }

        /// <summary>
        /// Disposes the ParquetDataReaderWithVirtualColumns object, releasing any resources it was holding.
        /// </summary>
        public void Dispose()
        {
            Close();
        }

        // Properties:

        public int Depth => _currentRowGroup;

        public int FieldCount => _columns.Length;

        object IDataRecord.this[int i] => GetValue(i);

        object IDataRecord.this[string name] => GetValue(GetOrdinal(name));

        public bool IsClosed => _reader == null;

        public int RecordsAffected => throw new NotSupportedException();

        // Indexers and Methods:

        /// <summary>
        /// Retrieves the boolean value for the given column in the current row.
        /// </summary>
        /// <param name="i">The zero-based column ordinal.</param>
        /// <returns>
        /// The boolean value of the specified column. This method returns false if the value is null or not convertible to boolean.
        /// </returns>
        /// <exception cref="InvalidCastException">Thrown when the value of the specified column cannot be converted to boolean.</exception>
        /// <exception cref="IndexOutOfRangeException">Thrown when the index passed was outside the range of 0 through FieldCount.</exception>
        public bool GetBoolean(int i)
        {
            return Convert.ToBoolean(GetValue(i));
        }

        /// <summary>
        /// Retrieves the byte value for the given column in the current row.
        /// </summary>
        /// <param name="i">The zero-based column ordinal.</param>
        /// <returns>
        /// The byte value of the specified column. 
        /// This method converts the value of the specified column to a byte.
        /// </returns>
        /// <exception cref="InvalidCastException">Thrown if the value cannot be cast to a byte.</exception>
        /// <exception cref="IndexOutOfRangeException">Thrown if the index passed was outside the range of 0 through FieldCount.</exception>
        public byte GetByte(int i)
        {
            return Convert.ToByte(GetValue(i));
        }

        /// <summary>
        /// Reads a specified number of bytes from the specified column into a buffer, starting at the given buffer offset.
        /// </summary>
        /// <param name="i">The zero-based column ordinal.</param>
        /// <param name="fieldOffset">The index within the field from which to start the read operation.</param>
        /// <param name="buffer">The buffer into which to read the bytes.</param>
        /// <param name="bufferOffset">The index within the buffer at which to start placing the data.</param>
        /// <param name="length">The maximum length to copy into the buffer.</param>
        /// <returns>The actual number of bytes read.</returns>
        /// <exception cref="IndexOutOfRangeException">The index passed was outside the range of 0 through FieldCount.</exception>
        /// <exception cref="ArgumentNullException">Buffer is null.</exception>
        public long GetBytes(int i, long fieldOffset, byte[] buffer, int bufferOffset, int length)
        {
            if (buffer == null) return ((byte[])GetValue(i)).Length;
            var bytesRead = Math.Min(length, ((byte[])GetValue(i)).Length - (int)fieldOffset);
            Array.Copy((byte[])GetValue(i), fieldOffset, buffer, bufferOffset, bytesRead);
            return bytesRead;
        }

        /// <summary>
        /// Retrieves the value for the given column in the current row as a character.
        /// </summary>
        /// <param name="i">The zero-based column ordinal.</param>
        /// <returns>
        /// The value of the specified column as a character. 
        /// </returns>
        /// <exception cref="InvalidCastException">Thrown when the value cannot be cast to a character.</exception>
        /// <exception cref="IndexOutOfRangeException">The index passed was outside the range of 0 through FieldCount.</exception>
        public char GetChar(int i)
        {
            return Convert.ToChar(GetValue(i));
        }

        /// <summary>
        /// Reads a specified number of characters from a specified column into a buffer, starting at a specified index.
        /// </summary>
        /// <param name="i">The zero-based column ordinal.</param>
        /// <param name="fieldOffset">The index within the column from which to start the read operation.</param>
        /// <param name="buffer">The buffer into which to copy the data.</param>
        /// <param name="bufferOffset">The index within the buffer at which to start copying the data.</param>
        /// <param name="length">The maximum length to copy into the buffer.</param>
        /// <returns>The actual number of characters read.</returns>
        /// <remarks>
        /// If you pass a null buffer, the method returns the length of the field in characters.
        /// </remarks>
        /// <exception cref="IndexOutOfRangeException">The index passed was outside the range of 0 through FieldCount.</exception>
        /// <exception cref="ArgumentNullException">Buffer is null and fieldOffset is not zero.</exception>
        public long GetChars(int i, long fieldOffset, char[] buffer, int bufferOffset, int length)
        {
            if (buffer == null) return ((char[])GetValue(i)).Length;
            var charsRead = Math.Min(length, ((char[])GetValue(i)).Length - (int)fieldOffset);
            Array.Copy((char[])GetValue(i), fieldOffset, buffer, bufferOffset, charsRead);
            return charsRead;
        }

        /// <summary>
        /// Retrieves a data reader for the specified column ordinal.
        /// </summary>
        /// <param name="i">The zero-based column ordinal.</param>
        /// <returns>A data reader for the specified column ordinal.</returns>
        /// <exception cref="NotSupportedException">Always thrown because this method is not supported.</exception>
        public IDataReader GetData(int i)
        {
            throw new NotSupportedException();
        }

        /// <summary>
        /// Retrieves the DateTime value for the given column in the current row.
        /// </summary>
        /// <param name="i">The zero-based column ordinal.</param>
        /// <returns>
        /// The DateTime value of the specified column. 
        /// </returns>
        /// <exception cref="InvalidCastException">Thrown when the value cannot be cast to a DateTime.</exception>
        /// <exception cref="IndexOutOfRangeException">Thrown when the index is outside the range of 0 through FieldCount.</exception>
        public DateTime GetDateTime(int i)
        {
            return Convert.ToDateTime(GetValue(i));
        }

        /// <summary>
        /// Retrieves the decimal value for the given column in the current row.
        /// </summary>
        /// <param name="i">The zero-based column ordinal.</param>
        /// <returns>
        /// The decimal value of the specified column. 
        /// This method returns a DBNull for null database columns or the default value for virtual columns.
        /// </returns>
        /// <exception cref="IndexOutOfRangeException">The index passed was outside the range of 0 through FieldCount.</exception>
        /// <exception cref="InvalidCastException">The specified cast is not valid.</exception>
        public decimal GetDecimal(int i)
        {
            return Convert.ToDecimal(GetValue(i));
        }

        /// <summary>
        /// Retrieves the value for the given column in the current row as a double.
        /// </summary>
        /// <param name="i">The zero-based column ordinal.</param>
        /// <returns>
        /// The double representation of the value of the specified column. 
        /// This method throws an InvalidCastException if the specified cast is not valid.
        /// </returns>
        /// <exception cref="IndexOutOfRangeException">The index passed was outside the range of 0 through FieldCount.</exception>
        /// <exception cref="InvalidCastException">The conversion of the value to a double failed.</exception>
        public double GetDouble(int i)
        {
            return Convert.ToDouble(GetValue(i));
        }

        /// <summary>
        /// Gets the type of the field at the specified index.
        /// </summary>
        /// <param name="i">The zero-based index of the field to find.</param>
        /// <returns>The type of the field at the specified index.</returns>
        public Type GetFieldType(int i)
        {
            return _columns[i].Data.GetType();
        }

        /// <summary>
        /// Retrieves the float value for the given column in the current row.
        /// </summary>
        /// <param name="i">The zero-based column ordinal.</param>
        /// <returns>
        /// The float value of the specified column. 
        /// This method returns a default float value for null database columns or the default value for virtual columns.
        /// </returns>
        /// <exception cref="InvalidCastException">Thrown when the value cannot be cast to a float.</exception>
        /// <exception cref="IndexOutOfRangeException">The index passed was outside the range of 0 through FieldCount.</exception>
        public float GetFloat(int i)
        {
            return Convert.ToSingle(GetValue(i));
        }

        /// <summary>
        /// Retrieves the value for the given column in the current row as a Guid.
        /// </summary>
        /// <param name="i">The zero-based column ordinal.</param>
        /// <returns>
        /// The Guid value of the specified column. 
        /// This method throws an exception if the value cannot be converted to a Guid.
        /// </returns>
        /// <exception cref="FormatException">Thrown when the value cannot be converted to a Guid.</exception>
        /// <exception cref="IndexOutOfRangeException">Thrown when the index is outside the range of 0 through FieldCount.</exception>
        public Guid GetGuid(int i)
        {
            return new Guid(GetValue(i).ToString());
        }

        /// <summary>
        /// Retrieves the value for the given column in the current row as a 16-bit signed integer.
        /// </summary>
        /// <param name="i">The zero-based column ordinal.</param>
        /// <returns>
        /// The 16-bit signed integer value of the specified column. 
        /// This method returns a DBNull for null database columns or the default value for virtual columns.
        /// </returns>
        /// <exception cref="IndexOutOfRangeException">The index passed was outside the range of 0 through FieldCount.</exception>
        /// <exception cref="InvalidCastException">The specified cast is not valid.</exception>
        public short GetInt16(int i)
        {
            return Convert.ToInt16(GetValue(i));
        }

        /// <summary>
        /// Retrieves the 32-bit integer value of the specified column in the current row.
        /// </summary>
        /// <param name="i">The zero-based column ordinal.</param>
        /// <returns>
        /// The 32-bit integer value of the specified column. This method returns a default value if the column value is null or not an integer.
        /// </returns>
        /// <exception cref="InvalidCastException">Thrown when the specified column value cannot be converted to a 32-bit integer.</exception>
        /// <exception cref="IndexOutOfRangeException">Thrown when the index passed was outside the range of 0 through FieldCount.</exception>
        public int GetInt32(int i)
        {
            return Convert.ToInt32(GetValue(i));
        }

        /// <summary>
        /// Retrieves the value for the given column in the current row as a 64-bit signed integer.
        /// </summary>
        /// <param name="i">The zero-based column ordinal.</param>
        /// <returns>
        /// The 64-bit signed integer value of the specified column. 
        /// This method returns a DBNull for null database columns or the default value for virtual columns.
        /// </returns>
        /// <exception cref="IndexOutOfRangeException">The index passed was outside the range of 0 through FieldCount.</exception>
        /// <exception cref="InvalidCastException">The specified cast is not valid.</exception>
        public long GetInt64(int i)
        {
            return Convert.ToInt64(GetValue(i));
        }


        /// <summary>
        /// Retrieves the string representation of the value for the given column in the current row.
        /// </summary>
        /// <param name="i">The zero-based column ordinal.</param>
        /// <returns>
        /// The string representation of the value of the specified column. 
        /// This method returns a DBNull for null database columns or the default value for virtual columns.
        /// </returns>
        /// <exception cref="IndexOutOfRangeException">The index passed was outside the range of 0 through FieldCount.</exception>
        public string GetString(int i)
        {
            return GetValue(i).ToString();
        }

        /// <summary>
        /// Retrieves the value for the given column in the current row and converts it to the specified type.
        /// </summary>
        /// <param name="i">The zero-based column ordinal.</param>
        /// <param name="fieldType">The type to which the value should be converted.</param>
        /// <returns>
        /// The value of the specified column converted to the specified type.
        /// This method returns a DBNull for null database columns or the default value for virtual columns.
        /// </returns>
        /// <exception cref="IndexOutOfRangeException">The index passed was outside the range of 0 through FieldCount.</exception>
        /// <exception cref="InvalidCastException">This conversion is not supported. -or- <paramref name="i"/> is null and <paramref name="fieldType"/> is a value type.</exception>
        /// <exception cref="FormatException"><paramref name="i"/> is not in a format recognized by <paramref name="fieldType"/>.</exception>
        /// <exception cref="OverflowException"><paramref name="i"/> represents a number that is out of the range of <paramref name="fieldType"/>.</exception>
        /// <exception cref="ArgumentNullException"><paramref name="fieldType"/> is null.</exception>
        internal object GetValue(int i, Type fieldType)
        {
            return Convert.ChangeType(GetValue(i), fieldType);
        }

        /// <summary>
        /// Populates an array of objects with the column values of the current row.
        /// </summary>
        /// <param name="values">An array of <see cref="object"/> to copy the attribute fields into.</param>
        /// <returns>The number of instances of <see cref="object"/> in the array.</returns>
        /// <remarks>
        /// This method populates the input array with the values of the current row up to the size of the array. 
        /// It returns the number of values copied. The length of the array will be the lesser of the available columns or the length of the array.
        /// </remarks>
        public int GetValues(object[] values)
        {
            int count = Math.Min(values.Length, FieldCount);
            for (int i = 0; i < count; i++)
            {
                values[i] = GetValue(i);
            }
            return count;
        }

        /// <summary>
        /// Determines whether the specified field is set to null.
        /// </summary>
        /// <param name="i">The zero-based column ordinal.</param>
        /// <returns>
        /// true if the specified field is set to null; otherwise, false.
        /// </returns>
        /// <exception cref="IndexOutOfRangeException">The index passed was outside the range of 0 through FieldCount.</exception>
        public bool IsDBNull(int i)
        {
            return GetValue(i) == null || Convert.IsDBNull(GetValue(i));
        }
    }
}
