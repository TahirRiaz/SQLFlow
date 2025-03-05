using System;
using System.Data;
using System.Data.Common;
using System.Threading.Tasks;
using Parquet.Schema;
using Parquet;

namespace SQLFlowCore.Services.Prq
{
    /// <summary>
    /// Represents a reader that provides fast, non-cached, forward-only access to Parquet data.
    /// </summary>
    class ParquetDataReader : IDataReader, IDataRecord
    {
        private readonly ParquetReader _reader;
        private ParquetRowGroupReader _groupReader;

        private Parquet.Data.DataColumn[] _columns;
        private DataField[] _fields;
        private int _currentRowGroup;
        private int _currentRow;
        private bool _readNextGroup = false;

        /// <summary>
        /// Initializes a new instance of the ParquetDataReader class.
        /// </summary>
        /// <param name="reader">The Parquet reader.</param>
        /// <param name="currentRowGroup">The current row group.</param>
        /// <param name="readNextGroup">If set to true, reads the next group.</param>
        internal ParquetDataReader(ParquetReader reader, int currentRowGroup, bool readNextGroup)
        {

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
        /// Asynchronously loads the columns.
        /// </summary>
        private void LoadColumnsAsync()
        {
            for (int i = 0; i < _reader.Schema.Fields.Count; i++)
            {
                _columns[i] = _groupReader.ReadColumnAsync(_fields[i]).Result;
            }
        }

        /// <summary>
        /// Advances the reader to the next result.
        /// </summary>
        /// <returns>true if there are more rows; otherwise false.</returns>
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
        /// Returns a DataTable that describes the column metadata of the ParquetDataReader.
        /// </summary>
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
        /// Gets the value of the specified column in its native format.
        /// </summary>
        /// <param name="i">The zero-based column ordinal.</param>
        /// <returns>The value of the specified column.</returns>
        // Returns the value for the given column in the current row
        public object GetValue(int i)
        {
            if (i < 0 || i >= _columns.Length)
            {
                throw new IndexOutOfRangeException();
            }
            return _columns[i]?.Data.GetValue(_currentRow) ?? DBNull.Value;
        }

        /// <summary>
        /// Advances the reader to the next record.
        /// </summary>
        /// <returns>true if there are more rows; otherwise false.</returns>
        // Reads the next record (row)
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
        /// Gets the data type information for the specified field.
        /// </summary>
        /// <param name="i">The zero-based field ordinal.</param>
        /// <returns>The data type information for the specified field.</returns>
        public string GetDataTypeName(int i)
        {
            return _columns[i].Data.GetType().ToString();
        }

        /// <summary>
        /// Gets the name for the field to find.
        /// </summary>
        /// <param name="i">The zero-based field ordinal.</param>
        /// <returns>The name of the field or the empty string (""), if there is no value to return.</returns>
        public string GetName(int i)
        {
            return _columns[i].Field.Name;
        }

        /// <summary>
        /// Returns the index of the named field.
        /// </summary>
        /// <param name="name">The name of the field to find.</param>
        /// <returns>The index of the named field.</returns>
        public int GetOrdinal(string name)
        {
            int Index = Array.FindIndex(_columns, x => x.Field.Name == name);


            return Index;
        }

        /// <summary>
        /// Closes the ParquetDataReader object.
        /// </summary>
        public void Close()
        {
            _groupReader?.Dispose();
            _groupReader = null;
        }

        /// <summary>
        /// Releases all resources used by the ParquetDataReader.
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
        /// Retrieves the boolean value of the specified column.
        /// </summary>
        /// <param name="i">The zero-based column ordinal.</param>
        /// <returns>The boolean value of the specified column.</returns>
        public bool GetBoolean(int i)
        {
            return Convert.ToBoolean(GetValue(i));
        }

        /// <summary>
        /// Retrieves the byte value of the specified column.
        /// </summary>
        /// <param name="i">The zero-based column ordinal.</param>
        /// <returns>The byte value of the specified column.</returns>
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
        public long GetBytes(int i, long fieldOffset, byte[] buffer, int bufferOffset, int length)
        {
            if (buffer == null) return ((byte[])GetValue(i)).Length;
            var bytesRead = Math.Min(length, ((byte[])GetValue(i)).Length - (int)fieldOffset);
            Array.Copy((byte[])GetValue(i), fieldOffset, buffer, bufferOffset, bytesRead);
            return bytesRead;
        }


        /// <summary>
        /// Retrieves the value of the specified column as a character.
        /// </summary>
        /// <param name="i">The zero-based column ordinal.</param>
        /// <returns>The value of the specified column as a character.</returns>
        /// <exception cref="InvalidCastException">Thrown if the specified column value cannot be converted to a char.</exception>
        public char GetChar(int i)
        {
            return Convert.ToChar(GetValue(i));
        }

        /// <summary>
        /// Reads a specified number of characters from a specified column into a buffer, starting at a specified index.
        /// </summary>
        /// <param name="i">The zero-based column ordinal.</param>
        /// <param name="fieldOffset">The index within the field from which to start the read operation.</param>
        /// <param name="buffer">The buffer into which to read the data.</param>
        /// <param name="bufferOffset">The index within the buffer at which to start placing the data.</param>
        /// <param name="length">The maximum length
        public long GetChars(int i, long fieldOffset, char[] buffer, int bufferOffset, int length)
        {
            if (buffer == null) return ((char[])GetValue(i)).Length;
            var charsRead = Math.Min(length, ((char[])GetValue(i)).Length - (int)fieldOffset);
            Array.Copy((char[])GetValue(i), fieldOffset, buffer, bufferOffset, charsRead);
            return charsRead;
        }

        /// <summary>
        /// Retrieves the data reader for the specified column.
        /// </summary>
        /// <param name="i">The zero-based column ordinal.</param>
        /// <returns>An IDataReader for the specified column.</returns>
        /// <exception cref="NotSupportedException">Always thrown because this method is not supported.</exception>
        public IDataReader GetData(int i)
        {
            throw new NotSupportedException();
        }

        /// <summary>
        /// Retrieves the value of the specified column as a DateTime.
        /// </summary>
        /// <param name="i">The zero-based column ordinal.</param>
        /// <returns>The value of the specified column as a DateTime.</returns>
        public DateTime GetDateTime(int i)
        {
            return Convert.ToDateTime(GetValue(i));
        }

        /// <summary>
        /// Retrieves the value of the specified column as a decimal.
        /// </summary>
        /// <param name="i">The zero-based column ordinal.</param>
        /// <returns>The decimal representation of the value in the specified column.</returns>
        public decimal GetDecimal(int i)
        {
            return Convert.ToDecimal(GetValue(i));
        }

        /// <summary>
        /// Retrieves the value of the specified column as a double-precision floating point number.
        /// </summary>
        /// <param name="i">The zero-based column ordinal.</param>
        /// <returns>The value of the specified column as a double.</returns>
        public double GetDouble(int i)
        {
            return Convert.ToDouble(GetValue(i));
        }

        /// <summary>
        /// Gets the Type of the specified column.
        /// </summary>
        /// <param name="i">The zero-based column ordinal.</param>
        /// <returns>The Type of the specified column.</returns>
        public Type GetFieldType(int i)
        {
            return _columns[i].Data.GetType();
        }

        /// <summary>
        /// Retrieves the value of the specified column as a float.
        /// </summary>
        /// <param name="i">The zero-based column ordinal.</param>
        /// <returns>The value of the specified column as a float.</returns>
        public float GetFloat(int i)
        {
            return Convert.ToSingle(GetValue(i));
        }

        /// <summary>
        /// Retrieves the value of the specified column as a Guid.
        /// </summary>
        /// <param name="i">The zero-based column ordinal.</param>
        /// <returns>The value of the specified column as a Guid.</returns>
        /// <exception cref="IndexOutOfRangeException">The index passed was outside the range of 0 through FieldCount.</exception>
        /// <exception cref="InvalidCastException">The specified cast is not valid.</exception>
        public Guid GetGuid(int i)
        {
            return new Guid(GetValue(i).ToString());
        }

        /// <summary>
        /// Retrieves the value of the specified column as a 16-bit signed integer.
        /// </summary>
        /// <param name="i">The zero-based column ordinal.</param>
        /// <returns>The value of the specified column as a 16-bit signed integer.</returns>
        public short GetInt16(int i)
        {
            return Convert.ToInt16(GetValue(i));
        }

        /// <summary>
        /// Retrieves the value of the specified column as an integer.
        /// </summary>
        /// <param name="i">The zero-based column ordinal.</param>
        /// <returns>The value of the specified column as a 32-bit signed integer.</returns>
        /// <exception cref="InvalidCastException">Thrown if the specified value cannot be converted to an integer.</exception>
        public int GetInt32(int i)
        {
            return Convert.ToInt32(GetValue(i));
        }

        /// <summary>
        /// Retrieves the value of the specified column as a 64-bit signed integer.
        /// </summary>
        /// <param name="i">The zero-based column ordinal.</param>
        /// <returns>The 64-bit signed integer value of the specified column.</returns>
        /// <exception cref="InvalidCastException">Thrown if the specified cast is not valid.</exception>
        public long GetInt64(int i)
        {
            return Convert.ToInt64(GetValue(i));
        }

        /// <summary>
        /// Retrieves the string value of the specified column.
        /// </summary>
        /// <param name="i">The zero-based column ordinal.</param>
        /// <returns>The string value of the specified column.</returns>
        public string GetString(int i)
        {
            return GetValue(i).ToString();
        }

        /// <summary>
        /// Gets the value of the specified column in its native format and converts it to the specified type.
        /// </summary>
        /// <param name="i">The zero-based column ordinal.</param>
        /// <param name="fieldType">The type to which the value of the specified column will be converted.</param>
        /// <returns>The value of the specified column, converted to the specified type.</returns>
        internal object GetValue(int i, Type fieldType)
        {
            return Convert.ChangeType(GetValue(i), fieldType);
        }

        /// <summary>
        /// Populates an array of objects with the column values of the current row.
        /// </summary>
        /// <param name="values">An array of System.Object to copy the attribute fields into.</param>
        /// <returns>The number of instances of System.Object in the array.</returns>
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
        /// <returns>true if the specified field is set to null; otherwise, false.</returns>
        public bool IsDBNull(int i)
        {
            return GetValue(i) == null || Convert.IsDBNull(GetValue(i));
        }
    }
}
