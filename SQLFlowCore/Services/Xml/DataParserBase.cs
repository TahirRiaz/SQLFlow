using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Globalization;

namespace SQLFlowCore.Services.Xml
{
    /// <summary>
    /// Represents a base class for data parsers that implement the IDataReader, IDisposable, and IDataRecord interfaces.
    /// </summary>
    /// <remarks>
    /// This abstract class provides a foundation for creating custom data parsers. It includes a collection of columns, 
    /// an event for reading records, and methods for managing the data reader's lifecycle.
    /// </remarks>
    internal abstract class DataParserBase : IDataReader, IDisposable, IDataRecord
    {
        protected ParseBase Reader;
        private string[] _values;
        private string[] _names;
        private Dictionary<string, int> _indexByName = new();
        private DataTable _schemaTable;
        internal DataReaderColumnCollection Columns;
        private bool _disposed;
        protected bool Initialized;

        public event ReadRecordEventHandler ReadRecord;

        /// <summary>
        /// Initializes a new instance of the <see cref="DataParserBase"/> class.
        /// </summary>
        /// <param name="reader">An instance of <see cref="ParseBase"/> that will be used for parsing data.</param>
        /// <param name="columns">A collection of <see cref="DataReaderColumnCollection"/> that represents the columns in the data reader.</param>
        public DataParserBase(ParseBase reader, DataReaderColumnCollection columns)
        {
            Reader = reader;
            Columns = columns;
        }

        private void CheckColumnIndex(int i)
        {
            if (i < 0 || i >= Columns.Count)
            {
                string[] textArray1 = new string[] { "No column was found at index ", i.ToString("###,##0"), " in column collection of length ", Columns.Count.ToString("###,##0"), "." };
                throw new IndexOutOfRangeException(string.Concat(textArray1));
            }
        }

        internal void CheckDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(GetType().FullName, "This object has been previously disposed. Methods on this object can no longer be called.");
            }
        }

        private void CheckInit()
        {
            if (!Initialized)
            {
                if (Columns.Count == 0)
                {
                    throw new InvalidOperationException("At least one column must exist in the column collection. Columns may be added to the collection using the Columns property and its Add method.");
                }
                _values = new string[Columns.Count];
                _names = new string[Columns.Count];
                string[] names = Columns.Names;
                int index = 0;
                while (true)
                {
                    if (index >= names.Length)
                    {
                        Initialize(_names);
                        int num2 = 0;
                        while (true)
                        {
                            if (num2 >= _names.Length)
                            {
                                Initialized = true;
                                break;
                            }
                            string str2 = _names[num2];
                            if (str2 != null)
                            {
                                _indexByName[str2] = num2;
                            }
                            num2++;
                        }
                        break;
                    }
                    string columnName = names[index];
                    _names[Columns.GetIndex(columnName)] = columnName;
                    index++;
                }
            }
        }

        public void Close()
        {
            ((IDisposable)this).Dispose();
        }

        private void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    Columns = null;
                    _values = null;
                    _names = null;
                    _indexByName = null;
                }
                if (Initialized)
                {
                    ((IDisposable)Reader).Dispose();
                }
                Reader = null;
                _disposed = true;
            }
        }

        ~DataParserBase()
        {
            Dispose(false);
        }

        protected virtual void Initialize(string[] names)
        {
        }

        protected abstract bool IsDbNull(int i, string columnValue);
        private bool OnReadRecord()
        {
            bool skipRecord = false;
            if (ReadRecord != null)
            {
                ReadRecordEventArgs e = new ReadRecordEventArgs(_values, _indexByName);
                ReadRecord(e);
                skipRecord = e.SkipRecord;
            }
            return skipRecord;
        }

        DataTable IDataReader.GetSchemaTable()
        {
            if (_schemaTable == null)
            {
                _schemaTable = new DataTable("SchemaTable");
                _schemaTable.Locale = CultureInfo.InvariantCulture;
                _schemaTable.Columns.Add(new DataColumn(SchemaTableColumn.ColumnName, typeof(string)));
                _schemaTable.Columns.Add(new DataColumn(SchemaTableColumn.ColumnOrdinal, typeof(int)));
                _schemaTable.Columns.Add(new DataColumn(SchemaTableColumn.ColumnSize, typeof(int)));
                _schemaTable.Columns.Add(new DataColumn(SchemaTableColumn.NumericPrecision, typeof(short)));
                _schemaTable.Columns.Add(new DataColumn(SchemaTableColumn.NumericScale, typeof(short)));
                _schemaTable.Columns.Add(new DataColumn(SchemaTableColumn.IsUnique, typeof(bool)));
                _schemaTable.Columns.Add(new DataColumn(SchemaTableColumn.IsKey, typeof(bool)));
                _schemaTable.Columns.Add(new DataColumn(SchemaTableOptionalColumn.BaseServerName, typeof(string)));
                _schemaTable.Columns.Add(new DataColumn(SchemaTableOptionalColumn.BaseCatalogName, typeof(string)));
                _schemaTable.Columns.Add(new DataColumn(SchemaTableColumn.BaseColumnName, typeof(string)));
                _schemaTable.Columns.Add(new DataColumn(SchemaTableColumn.BaseSchemaName, typeof(string)));
                _schemaTable.Columns.Add(new DataColumn(SchemaTableColumn.BaseTableName, typeof(string)));
                _schemaTable.Columns.Add(new DataColumn(SchemaTableColumn.DataType, typeof(Type)));
                _schemaTable.Columns.Add(new DataColumn(SchemaTableColumn.AllowDBNull, typeof(bool)));
                _schemaTable.Columns.Add(new DataColumn(SchemaTableColumn.ProviderType, typeof(int)));
                _schemaTable.Columns.Add(new DataColumn(SchemaTableColumn.IsAliased, typeof(bool)));
                _schemaTable.Columns.Add(new DataColumn(SchemaTableColumn.IsExpression, typeof(bool)));
                _schemaTable.Columns.Add(new DataColumn("IsIdentity", typeof(bool)));
                _schemaTable.Columns.Add(new DataColumn(SchemaTableOptionalColumn.IsAutoIncrement, typeof(bool)));
                _schemaTable.Columns.Add(new DataColumn(SchemaTableOptionalColumn.IsRowVersion, typeof(bool)));
                _schemaTable.Columns.Add(new DataColumn(SchemaTableOptionalColumn.IsHidden, typeof(bool)));
                _schemaTable.Columns.Add(new DataColumn(SchemaTableColumn.IsLong, typeof(bool)));
                _schemaTable.Columns.Add(new DataColumn(SchemaTableOptionalColumn.IsReadOnly, typeof(bool)));
                _schemaTable.Columns.Add(new DataColumn(SchemaTableOptionalColumn.ProviderSpecificDataType, typeof(Type)));
                _schemaTable.Columns.Add(new DataColumn("DataTypeName", typeof(string)));
                _schemaTable.Columns.Add(new DataColumn("XmlSchemaCollectionDatabase", typeof(string)));
                _schemaTable.Columns.Add(new DataColumn("XmlSchemaCollectionOwningSchema", typeof(string)));
                _schemaTable.Columns.Add(new DataColumn("XmlSchemaCollectionName", typeof(string)));
                _schemaTable.Columns.Add(new DataColumn("UdtAssemblyQualifiedName", typeof(string)));
                _schemaTable.Columns.Add(new DataColumn(SchemaTableColumn.NonVersionedProviderType, typeof(int)));
                _schemaTable.Columns.Add(new DataColumn("IsColumnSet", typeof(bool)));
                for (int i = 0; i < Columns.Count; i++)
                {
                    DataRow row1 = _schemaTable.Rows.Add(new object[0]);
                    row1[SchemaTableColumn.ColumnName] = ((IDataRecord)this).GetName(i);
                    row1[SchemaTableColumn.ColumnOrdinal] = i;
                    row1[SchemaTableColumn.ColumnSize] = 0x7fffffff;
                    row1[SchemaTableColumn.NumericPrecision] = 0xff;
                    row1[SchemaTableColumn.NumericScale] = 0xff;
                    row1[SchemaTableColumn.IsUnique] = false;
                    row1[SchemaTableColumn.IsKey] = false;
                    row1[SchemaTableOptionalColumn.BaseServerName] = "";
                    row1[SchemaTableOptionalColumn.BaseCatalogName] = "";
                    row1[SchemaTableColumn.BaseColumnName] = ((IDataRecord)this).GetName(i);
                    row1[SchemaTableColumn.BaseSchemaName] = "";
                    row1[SchemaTableColumn.BaseTableName] = "";
                    row1[SchemaTableColumn.DataType] = ((IDataRecord)this).GetFieldType(i);
                    row1[SchemaTableColumn.AllowDBNull] = true;
                    row1[SchemaTableColumn.ProviderType] = (int)Columns[i].DbType;
                    row1[SchemaTableColumn.IsAliased] = false;
                    row1[SchemaTableColumn.IsExpression] = false;
                    row1["IsIdentity"] = false;
                    row1[SchemaTableOptionalColumn.IsAutoIncrement] = false;
                    row1[SchemaTableOptionalColumn.IsRowVersion] = false;
                    row1[SchemaTableOptionalColumn.IsHidden] = false;
                    row1[SchemaTableColumn.IsLong] = false;
                    row1[SchemaTableOptionalColumn.IsReadOnly] = true;
                    row1[SchemaTableOptionalColumn.ProviderSpecificDataType] = ((IDataRecord)this).GetFieldType(i);
                    row1["DataTypeName"] = Columns[i].DataTypeName;
                    row1["XmlSchemaCollectionDatabase"] = "";
                    row1["XmlSchemaCollectionOwningSchema"] = "";
                    row1["XmlSchemaCollectionName"] = "";
                    row1["UdtAssemblyQualifiedName"] = "";
                    row1[SchemaTableColumn.NonVersionedProviderType] = (int)Columns[i].DbType;
                    row1["IsColumnSet"] = false;
                }
            }
            return _schemaTable;
        }

        bool IDataReader.NextResult()
        {
            return false;
        }

        bool IDataReader.Read()
        {
            CheckDisposed();
            CheckInit();
            bool flag = true;
            bool flag2 = true;
            while (flag & flag2)
            {
                flag = Reader.ReadRecord();
                if (flag)
                {
                    int index = 0;
                    while (true)
                    {
                        if (index >= _values.Length)
                        {
                            flag2 = OnReadRecord();
                            break;
                        }
                        _values[index] = Reader[index];
                        index++;
                    }
                }
            }
            return flag;
        }

        bool IDataRecord.GetBoolean(int i)
        {
            return (bool)((IDataRecord)this).GetValue(i);
        }

        byte IDataRecord.GetByte(int i)
        {
            return (byte)((IDataRecord)this).GetValue(i);
        }

        long IDataRecord.GetBytes(int i, long fieldOffset, byte[] buffer, int bufferoffset, int length)
        {
            throw new NotSupportedException("GetBytes is not currently supported by the IDataRecord implementation supplied by " + GetType().FullName + ".");
        }

        char IDataRecord.GetChar(int i)
        {
            return (char)((IDataRecord)this).GetValue(i);
        }

        long IDataRecord.GetChars(int i, long fieldoffset, char[] buffer, int bufferoffset, int length)
        {
            throw new NotSupportedException("GetChars is not currently supported by the IDataRecord implementation supplied by " + GetType().FullName + ".");
        }

        IDataReader IDataRecord.GetData(int i)
        {
            return i != 0 ? null : this;
        }

        string IDataRecord.GetDataTypeName(int i)
        {
            CheckDisposed();
            CheckColumnIndex(i);
            return Columns[i].DataTypeName;
        }

        DateTime IDataRecord.GetDateTime(int i)
        {
            return (DateTime)((IDataRecord)this).GetValue(i);
        }

        decimal IDataRecord.GetDecimal(int i)
        {
            return (decimal)((IDataRecord)this).GetValue(i);
        }

        double IDataRecord.GetDouble(int i)
        {
            return (double)((IDataRecord)this).GetValue(i);
        }

        Type IDataRecord.GetFieldType(int i)
        {
            CheckDisposed();
            CheckColumnIndex(i);
            return Columns[i].FieldType;
        }

        float IDataRecord.GetFloat(int i)
        {
            return (float)((IDataRecord)this).GetValue(i);
        }

        Guid IDataRecord.GetGuid(int i)
        {
            return (Guid)((IDataRecord)this).GetValue(i);
        }

        short IDataRecord.GetInt16(int i)
        {
            return (short)((IDataRecord)this).GetValue(i);
        }

        int IDataRecord.GetInt32(int i)
        {
            return (int)((IDataRecord)this).GetValue(i);
        }

        long IDataRecord.GetInt64(int i)
        {
            return (long)((IDataRecord)this).GetValue(i);
        }

        string IDataRecord.GetName(int i)
        {
            CheckDisposed();
            CheckInit();
            CheckColumnIndex(i);
            return _names[i];
        }

        int IDataRecord.GetOrdinal(string name)
        {
            CheckDisposed();
            CheckInit();
            return !_indexByName.ContainsKey(name) ? -1 : _indexByName[name];
        }

        string IDataRecord.GetString(int i)
        {
            return ((IDataRecord)this).GetValue(i).ToString();
        }

        object IDataRecord.GetValue(int i)
        {
            CheckDisposed();
            CheckColumnIndex(i);
            string str = _values[i];
            DataReaderColumn column = Columns[i];
            IFormatProvider formatProvider = column.formatProvider;
            DbType dbType = column.DbType;
            if (dbType != DbType.String && str != null)
                str = str.Trim();
            object obj = null;
            if (!IsDbNull(i, str))
            {
                try
                {
                    switch (dbType)
                    {
                        case DbType.Byte:
                            obj = byte.Parse(str, NumberStyles.Any, formatProvider);
                            break;
                        case DbType.Boolean:
                            switch (str.ToUpper())
                            {
                                case "-1":
                                case "1":
                                case "T":
                                case "TRUE":
                                case "Y":
                                case "YES":
                                    obj = true;
                                    break;
                                case "0":
                                case "F":
                                case "FALSE":
                                case "N":
                                case "NO":
                                    obj = false;
                                    break;
                                default:
                                    obj = bool.Parse(str);
                                    break;
                            }
                            break;
                        case DbType.DateTime:
                            string format = column.format;
                            obj = format == null ? DateTime.Parse(str, formatProvider) : (object)DateTime.ParseExact(str, format, formatProvider);
                            break;
                        case DbType.Decimal:
                            obj = decimal.Parse(str, NumberStyles.Any, formatProvider);
                            break;
                        case DbType.Double:
                            obj = double.Parse(str, NumberStyles.Any, formatProvider);
                            break;
                        case DbType.Guid:
                            obj = new Guid(str);
                            break;
                        case DbType.Int16:
                            obj = short.Parse(str, NumberStyles.Any, formatProvider);
                            break;
                        case DbType.Int32:
                            obj = int.Parse(str, NumberStyles.Any, formatProvider);
                            break;
                        case DbType.Int64:
                            obj = long.Parse(str, NumberStyles.Any, formatProvider);
                            break;
                        case DbType.Single:
                            obj = float.Parse(str, NumberStyles.Any, formatProvider);
                            break;
                        case DbType.String:
                            obj = str;
                            break;
                    }
                }
                catch (Exception ex)
                {
                    ThrowHelpfulException(i, ex, column.FieldType);
                }
            }
            else
                obj = column.defaultValue;
            return obj;
        }

        int IDataRecord.GetValues(object[] values)
        {
            int num = Math.Min(_values.Length, values.Length);
            for (int i = 0; i < num; i++)
            {
                values[i] = ((IDataRecord)this).GetValue(i);
            }
            return num;
        }

        bool IDataRecord.IsDBNull(int i)
        {
            CheckDisposed();
            CheckColumnIndex(i);
            string columnValue = _values[i];
            if (Columns[i].DbType != DbType.String && columnValue != null)
            {
                columnValue = columnValue.Trim();
            }
            return IsDbNull(i, columnValue);
        }

        void IDisposable.Dispose()
        {
            if (!_disposed)
            {
                Dispose(true);
                GC.SuppressFinalize(this);
            }
        }

        /// <summary>
        /// Throws a formatted exception message to provide more context about the error.
        /// </summary>
        /// <param name="columnIndex">The index of the column where the exception occurred.</param>
        /// <param name="e">The original exception that was thrown.</param>
        /// <param name="type">The expected type of the data in the column.</param>
        /// <exception cref="FormatException">Thrown when a value cannot be converted to the expected type.</exception>
        /// <exception cref="OverflowException">Thrown when an overflow occurs while trying to convert a value to the expected type.</exception>
        private void ThrowHelpfulException(int columnIndex, Exception e, Type type)
        {
            string str = columnIndex.ToString("###,##0");
            string name = ((IDataRecord)this).GetName(columnIndex);
            if (name != null && name.Length > 0)
            {
                str = str + " (" + name + ")";
            }
            switch (e)
            {
                case FormatException _:
                    {
                        string[] textArray1 = new string[11];
                        textArray1[0] = "Value \"";
                        textArray1[1] = _values[columnIndex];
                        textArray1[2] = "\" can not be converted to type ";
                        textArray1[3] = type.Name;
                        textArray1[4] = "/";
                        textArray1[5] = Columns[columnIndex].DataTypeName;
                        textArray1[6] = " in column ";
                        textArray1[7] = str;
                        textArray1[8] = " in record ";
                        textArray1[9] = Reader.CurrentRecord.ToString("###,##0");
                        textArray1[10] = ".";
                        throw new FormatException(string.Concat(textArray1));
                    }
                case OverflowException _:
                    {
                        string[] textArray2 = new string[11];
                        textArray2[0] = "Overflow occurred while trying to convert value \"";
                        textArray2[1] = _values[columnIndex];
                        textArray2[2] = "\" to type ";
                        textArray2[3] = type.Name;
                        textArray2[4] = "/";
                        textArray2[5] = Columns[columnIndex].DataTypeName;
                        textArray2[6] = " in column ";
                        textArray2[7] = str;
                        textArray2[8] = " in record ";
                        textArray2[9] = Reader.CurrentRecord.ToString("###,##0");
                        textArray2[10] = ".";
                        throw new OverflowException(string.Concat(textArray2));
                    }
            }
        }

        int IDataReader.RecordsAffected => -1;

        bool IDataReader.IsClosed => _disposed;

        int IDataReader.Depth => 0;

        object IDataRecord.this[string name]
        {
            get
            {
                CheckDisposed();
                int ordinal = ((IDataRecord)this).GetOrdinal(name);
                return ((IDataRecord)this).GetValue(ordinal);
            }
        }

        object IDataRecord.this[int i] => ((IDataRecord)this).GetValue(i);

        int IDataRecord.FieldCount
        {
            get
            {
                CheckDisposed();
                return Columns.Count;
            }
        }

        /// <summary>
        /// Represents a column in a data reader. This class provides methods and properties to work with the column data type, default value, format, and format provider.
        /// </summary>
        public abstract class DataReaderColumn
        {
            private static readonly Dictionary<string, Type> TypeMappings = new(StringComparer.InvariantCultureIgnoreCase);
            private static readonly Dictionary<string, string> NameMappings = new(StringComparer.InvariantCultureIgnoreCase);
            private static readonly Dictionary<string, DbType> DbTypeMappings = new(StringComparer.InvariantCultureIgnoreCase);
            internal readonly Type FieldType = typeof(string);
            internal readonly string DataTypeName = "";
            internal readonly DbType DbType = DbType.String;
            internal IFormatProvider formatProvider = CultureInfo.CurrentCulture;
            internal object defaultValue = DBNull.Value;
            internal string format;

            static DataReaderColumn()
            {
                LoadFieldTypeMappings();
                LoadNameMappings();
                LoadDbTypeMappings();
            }

            internal DataReaderColumn(string dataType)
            {
                if (dataType == null)
                {
                    throw new ArgumentNullException(nameof(dataType), "Data type can not be null.");
                }
                VerifyLookup(dataType);
                FieldType = LookupTypeMapping(dataType);
                DataTypeName = LookupNameMapping(dataType);
                DbType = LookupDbTypeMapping(dataType);
            }

            private static void LoadDbTypeMappings()
            {
                DbTypeMappings["STRING"] = DbType.String;
                DbTypeMappings["TEXT"] = DbType.String;
                DbTypeMappings["NTEXT"] = DbType.String;
                DbTypeMappings["CHAR"] = DbType.String;
                DbTypeMappings["NCHAR"] = DbType.String;
                DbTypeMappings["VARCHAR"] = DbType.String;
                DbTypeMappings["NVARCHAR"] = DbType.String;
                DbTypeMappings["XML"] = DbType.String;
                DbTypeMappings["DATETIME"] = DbType.DateTime;
                DbTypeMappings["SMALLDATETIME"] = DbType.DateTime;
                DbTypeMappings["GUID"] = DbType.Guid;
                DbTypeMappings["UNIQUE"] = DbType.Guid;
                DbTypeMappings["UNIQUEIDENTIFIER"] = DbType.Guid;
                DbTypeMappings["IDENTIFIER"] = DbType.Guid;
                DbTypeMappings["DOUBLE"] = DbType.Double;
                DbTypeMappings["FLOAT"] = DbType.Double;
                DbTypeMappings["NUMERIC"] = DbType.Decimal;
                DbTypeMappings["DECIMAL"] = DbType.Decimal;
                DbTypeMappings["MONEY"] = DbType.Decimal;
                DbTypeMappings["SMALLMONEY"] = DbType.Decimal;
                DbTypeMappings["CURRENCY"] = DbType.Decimal;
                DbTypeMappings["BOOLEAN"] = DbType.Boolean;
                DbTypeMappings["BOOL"] = DbType.Boolean;
                DbTypeMappings["BIT"] = DbType.Boolean;
                DbTypeMappings["INT"] = DbType.Int32;
                DbTypeMappings["INTEGER"] = DbType.Int32;
                DbTypeMappings["INT32"] = DbType.Int32;
                DbTypeMappings["SHORT"] = DbType.Int32;
                DbTypeMappings["LONG"] = DbType.Int64;
                DbTypeMappings["BIGINT"] = DbType.Int64;
                DbTypeMappings["INT64"] = DbType.Int64;
                DbTypeMappings["SINGLE"] = DbType.Single;
                DbTypeMappings["REAL"] = DbType.Single;
                DbTypeMappings["SMALLINT"] = DbType.Int16;
                DbTypeMappings["INT16"] = DbType.Int16;
                DbTypeMappings["TINYINT"] = DbType.Byte;
                DbTypeMappings["BYTE"] = DbType.Byte;
            }

            private static void LoadFieldTypeMappings()
            {
                TypeMappings["STRING"] = typeof(string);
                TypeMappings["TEXT"] = typeof(string);
                TypeMappings["NTEXT"] = typeof(string);
                TypeMappings["CHAR"] = typeof(string);
                TypeMappings["NCHAR"] = typeof(string);
                TypeMappings["VARCHAR"] = typeof(string);
                TypeMappings["NVARCHAR"] = typeof(string);
                TypeMappings["XML"] = typeof(string);
                TypeMappings["DATETIME"] = typeof(DateTime);
                TypeMappings["SMALLDATETIME"] = typeof(DateTime);
                TypeMappings["GUID"] = typeof(Guid);
                TypeMappings["UNIQUE"] = typeof(Guid);
                TypeMappings["UNIQUEIDENTIFIER"] = typeof(Guid);
                TypeMappings["IDENTIFIER"] = typeof(Guid);
                TypeMappings["DOUBLE"] = typeof(double);
                TypeMappings["FLOAT"] = typeof(double);
                TypeMappings["NUMERIC"] = typeof(decimal);
                TypeMappings["DECIMAL"] = typeof(decimal);
                TypeMappings["MONEY"] = typeof(decimal);
                TypeMappings["SMALLMONEY"] = typeof(decimal);
                TypeMappings["CURRENCY"] = typeof(decimal);
                TypeMappings["BOOLEAN"] = typeof(bool);
                TypeMappings["BOOL"] = typeof(bool);
                TypeMappings["BIT"] = typeof(bool);
                TypeMappings["INT32"] = typeof(int);
                TypeMappings["INT"] = typeof(int);
                TypeMappings["INTEGER"] = typeof(int);
                TypeMappings["SHORT"] = typeof(int);
                TypeMappings["INT64"] = typeof(long);
                TypeMappings["LONG"] = typeof(long);
                TypeMappings["BIGINT"] = typeof(long);
                TypeMappings["SINGLE"] = typeof(float);
                TypeMappings["REAL"] = typeof(float);
                TypeMappings["INT16"] = typeof(short);
                TypeMappings["SMALLINT"] = typeof(short);
                TypeMappings["TINYINT"] = typeof(byte);
                TypeMappings["BYTE"] = typeof(byte);
            }

            private static void LoadNameMappings()
            {
                NameMappings["STRING"] = "varchar";
                NameMappings["TEXT"] = "text";
                NameMappings["NTEXT"] = "ntext";
                NameMappings["CHAR"] = "char";
                NameMappings["NCHAR"] = "nchar";
                NameMappings["VARCHAR"] = "varchar";
                NameMappings["NVARCHAR"] = "nvarchar";
                NameMappings["XML"] = "xml";
                NameMappings["DATETIME"] = "datetime";
                NameMappings["SMALLDATETIME"] = "smalldatetime";
                NameMappings["GUID"] = "uniqueidentifier";
                NameMappings["UNIQUE"] = "uniqueidentifier";
                NameMappings["UNIQUEIDENTIFIER"] = "uniqueidentifier";
                NameMappings["IDENTIFIER"] = "uniqueidentifier";
                NameMappings["DOUBLE"] = "double";
                NameMappings["FLOAT"] = "float";
                NameMappings["NUMERIC"] = "decimal";
                NameMappings["DECIMAL"] = "decimal";
                NameMappings["MONEY"] = "money";
                NameMappings["SMALLMONEY"] = "smallmoney";
                NameMappings["CURRENCY"] = "money";
                NameMappings["BOOLEAN"] = "bit";
                NameMappings["BOOL"] = "bit";
                NameMappings["BIT"] = "bit";
                NameMappings["INT"] = "int";
                NameMappings["INTEGER"] = "int";
                NameMappings["INT32"] = "int";
                NameMappings["SHORT"] = "short";
                NameMappings["LONG"] = "long";
                NameMappings["INT64"] = "long";
                NameMappings["BIGINT"] = "bigint";
                NameMappings["SINGLE"] = "single";
                NameMappings["REAL"] = "real";
                NameMappings["SMALLINT"] = "smallint";
                NameMappings["INT16"] = "smallint";
                NameMappings["TINYINT"] = "tinyint";
                NameMappings["BYTE"] = "byte";
            }

            private static DbType LookupDbTypeMapping(string dataType)
            {
                return DbTypeMappings[dataType];
            }

            private static string LookupNameMapping(string dataType)
            {
                return NameMappings[dataType];
            }

            private static Type LookupTypeMapping(string dataType)
            {
                return TypeMappings[dataType];
            }

            private static void VerifyLookup(string dataType)
            {
                if (!DbTypeMappings.ContainsKey(dataType) || !NameMappings.ContainsKey(dataType) || !TypeMappings.ContainsKey(dataType))
                {
                    throw new ArgumentException("Could not find matching data column type for passed in type \"" + dataType + "\".", nameof(dataType));
                }
            }

            public object DefaultValue
            {
                get => defaultValue;
                set => defaultValue = value;
            }

            public IFormatProvider FormatProvider
            {
                get => formatProvider;
                set => formatProvider = value;
            }

            public string Format
            {
                get => format;
                set
                {
                    if (value == null || value.Length <= 0)
                    {
                        format = null;
                    }
                    else
                    {
                        if (DbType != DbType.DateTime)
                        {
                            throw new NotSupportedException("Format string is currently only supported for DateTime columns.");
                        }
                        format = value;
                    }
                }
            }
        }

        /// <summary>
        /// Represents a collection of DataReaderColumn objects.
        /// </summary>
        public abstract class DataReaderColumnCollection : NamedColCollection
        {
            /// <summary>
            /// Initializes a new instance of the DataReaderColumnCollection class.
            /// </summary>
            protected DataReaderColumnCollection()
            {
            }
            /// <summary>
            /// Adds a DataReaderColumn to the collection.
            /// </summary>
            /// <param name="column">The DataReaderColumn to add to the collection.</param>
            public void Add(DataReaderColumn column)
            {
                Add(column, null);
            }

            /// <summary>
            /// Adds a DataReaderColumn to the collection with the specified column name.
            /// </summary>
            /// <param name="column">The DataReaderColumn to add to the collection.</param>
            /// <param name="columnName">The name of the column.</param>
            public void Add(DataReaderColumn column, string columnName)
            {
                base.Add(column, columnName);
            }

#pragma warning disable CS0108 // 'DataParserBase.DataReaderColumnCollection.this[int]' hides inherited member 'NamedColCollection.this[int]'. Use the new keyword if hiding was intended.
            /// <summary>
            /// Gets or sets the DataReaderColumn at the specified index.
            /// </summary>
            /// <param name="columnIndex">The zero-based index of the DataReaderColumn to get or set.</param>
            /// <returns>The DataReaderColumn at the specified index.</returns>
            public DataReaderColumn this[int columnIndex]
#pragma warning restore CS0108 // 'DataParserBase.DataReaderColumnCollection.this[int]' hides inherited member 'NamedColCollection.this[int]'. Use the new keyword if hiding was intended.
            {
                get
                {
                    return base[columnIndex] as DataReaderColumn;
                }
                set
                {
                    base[columnIndex] = value;
                }
            }

#pragma warning disable CS0108 // 'DataParserBase.DataReaderColumnCollection.this[string]' hides inherited member 'NamedColCollection.this[string]'. Use the new keyword if hiding was intended.
            /// <summary>
            /// Gets or sets the DataReaderColumn with the specified column name.
            /// </summary>
            /// <param name="columnName">The name of the column to get or set.</param>
            /// <returns>The DataReaderColumn with the specified column name.</returns>
            public DataReaderColumn this[string columnName]
#pragma warning restore CS0108 // 'DataParserBase.DataReaderColumnCollection.this[string]' hides inherited member 'NamedColCollection.this[string]'. Use the new keyword if hiding was intended.
            {
                get
                {
                    return base[columnName] as DataReaderColumn;
                }
                set
                {
                    base[columnName] = value;
                }
            }
        }

        /// <summary>
        /// Provides data for the ReadRecord event.
        /// </summary>
        /// <remarks>
        /// This class contains the data needed by the ReadRecord event handler to process each record read by the DataParserBase.
        /// It includes a collection of the values in the current record and a flag indicating whether the record should be skipped.
        /// </remarks>
        public sealed class ReadRecordEventArgs
        {
            internal ReadRecordEventArgs(string[] values, Dictionary<string, int> columnIndexByName)
            {
                Values = new RecordValuesCollection(values, columnIndexByName);
            }

            public RecordValuesCollection Values { get; set; }

            public bool SkipRecord { get; set; }
        }

        /// <summary>
        /// Represents the method that will handle the ReadRecord event of a DataParserBase.
        /// </summary>
        /// <param name="e">The ReadRecordEventArgs instance containing the event data.</param>
        public delegate void ReadRecordEventHandler(ReadRecordEventArgs e);

        /// <summary>
        /// Represents a collection of record values, providing access by both index and column name.
        /// </summary>
        /// <remarks>
        /// This class is used to encapsulate a set of values that belong to a record in the data source.
        /// It provides the ability to access these values either by their ordinal index or by their column name.
        /// </remarks>
        public sealed class RecordValuesCollection : ICollection, IEnumerable
        {
            private string[] _values;
            private Dictionary<string, int> _indexByName;

            internal RecordValuesCollection(string[] values, Dictionary<string, int> indexByName)
            {
                _indexByName = indexByName;
                _values = values;
            }

            void ICollection.CopyTo(Array array, int index)
            {
                _values.CopyTo(array, index);
            }

            IEnumerator IEnumerable.GetEnumerator()
            {
                return _values.GetEnumerator();
            }

            public int Count => _values.Length;

            bool ICollection.IsSynchronized => false;

            object ICollection.SyncRoot => _values.SyncRoot;

            public string this[int columnIndex]
            {
                get => _values[columnIndex];
                set => _values[columnIndex] = value;
            }

            public string this[string columnName]
            {
                get => this[_indexByName[columnName]];
                set => this[_indexByName[columnName]] = value;
            }
        }
    }
}

