using Microsoft.SqlServer.TransactSql.ScriptDom;
using Microsoft.SqlServer.Types;
using System;
using System.Collections.Generic;
using System.Data;
using System.Xml;

namespace SQLFlowCore.Common
{
    public class DataTypeMapper
    {
        public static Type MapToDotNetType(DataTypeReference sqlType)
        {
            Dictionary<string, Type> typeMap = new Dictionary<string, Type>
            {
                {"CHAR", typeof(string)},
                {"VARCHAR", typeof(string)},
                {"TEXT", typeof(string)},
                {"NCHAR", typeof(string)},
                {"NVARCHAR", typeof(string)},
                {"NTEXT", typeof(string)},
                {"BINARY", typeof(byte[])},
                {"VARBINARY", typeof(byte[])},
                {"IMAGE", typeof(byte[])},
                {"BIT", typeof(bool)},
                {"TINYINT", typeof(byte)},
                {"SMALLINT", typeof(short)},
                {"INT", typeof(int)},
                {"BIGINT", typeof(long)},
                {"NUMERIC", typeof(decimal)},
                {"DECIMAL", typeof(decimal)},
                {"SMALLMONEY", typeof(decimal)},
                {"MONEY", typeof(decimal)},
                {"FLOAT", typeof(double)},
                {"REAL", typeof(float)},
                {"DATE", typeof(DateTime)},
                {"DATETIME", typeof(DateTime)},
                {"SMALLDATETIME", typeof(DateTime)},
                {"DATETIME2", typeof(DateTime)},
                {"DATETIMEOFFSET", typeof(DateTimeOffset)},
                {"TIME", typeof(TimeSpan)},
                {"SQL_VARIANT", typeof(object)},
                {"UNIQUEIDENTIFIER", typeof(Guid)},
                {"XML", typeof(string)}
            };
            if (sqlType is XmlDataTypeReference)
            {
                return typeof(string);
            }
            else if (sqlType is ParameterizedDataTypeReference parameterizedType)
            {
                string baseType = parameterizedType.Name.BaseIdentifier.Value.ToUpper();
                if (typeMap.ContainsKey(baseType))
                {
                    return typeMap[baseType];
                }
                else
                {
                    throw new NotImplementedException($"Mapping for SQL type {baseType} not implemented.");
                }
            }
            else if (sqlType is SqlDataTypeReference simpleType)
            {
                if (typeMap.ContainsKey(simpleType.SqlDataTypeOption.ToString()))
                {
                    return typeMap[simpleType.SqlDataTypeOption.ToString()];
                }
                else
                {
                    throw new NotImplementedException($"Mapping for SQL type {simpleType.SqlDataTypeOption} not implemented.");
                }
            }
            else
            {
                throw new NotImplementedException($"Mapping for SQL type {sqlType.GetType().Name} not implemented.");
            }
        }

        public static readonly Dictionary<string, DbType> ParameterizedTypeMapping = new()
        {
            {"BIT", DbType.Boolean},
            {"TINYINT", DbType.Byte},
            {"SMALLINT", DbType.Int16},
            {"INT", DbType.Int32},
            {"BIGINT", DbType.Int64},
            {"NUMERIC", DbType.Decimal},
            {"DECIMAL", DbType.Decimal},
            {"MONEY", DbType.Currency},
            {"SMALLMONEY", DbType.Currency},
            {"FLOAT", DbType.Double},
            {"REAL", DbType.Single},
            {"DATE", DbType.Date},
            {"DATETIME", DbType.DateTime},
            {"SMALLDATETIME", DbType.DateTime},
            {"DATETIME2", DbType.DateTime2},
            {"DATETIMEOFFSET", DbType.DateTimeOffset},
            {"TIME", DbType.Time},
            {"CHAR", DbType.String},
            {"VARCHAR", DbType.String},
            {"TEXT", DbType.String},
            {"NCHAR", DbType.StringFixedLength},
            {"NVARCHAR", DbType.StringFixedLength},
            {"NTEXT", DbType.StringFixedLength},
            {"BINARY", DbType.Binary},
            {"VARBINARY", DbType.Binary},
            {"IMAGE", DbType.Binary},
            {"SQL_VARIANT", DbType.Object},
            {"XML", DbType.Xml},
            {"XmlDataTypeReference", DbType.Xml},
            {"UNIQUEIDENTIFIER", DbType.Guid},
            {"JSON", DbType.String}
        };

        public static readonly Dictionary<SqlDataTypeOption, DbType> SimpleTypeMapping = new()
        {
            {SqlDataTypeOption.Bit, DbType.Boolean},
            {SqlDataTypeOption.TinyInt, DbType.Byte},
            {SqlDataTypeOption.SmallInt, DbType.Int16},
            {SqlDataTypeOption.Int, DbType.Int32},
            {SqlDataTypeOption.BigInt, DbType.Int64},
            {SqlDataTypeOption.Numeric, DbType.Decimal},
            {SqlDataTypeOption.Decimal, DbType.Decimal},
            {SqlDataTypeOption.Money, DbType.Currency},
            {SqlDataTypeOption.SmallMoney, DbType.Currency},
            {SqlDataTypeOption.Float, DbType.Double},
            {SqlDataTypeOption.Real, DbType.Single},
            {SqlDataTypeOption.Date, DbType.Date},
            {SqlDataTypeOption.DateTime, DbType.DateTime},
            {SqlDataTypeOption.SmallDateTime, DbType.DateTime},
            {SqlDataTypeOption.DateTime2, DbType.DateTime2},
            {SqlDataTypeOption.DateTimeOffset, DbType.DateTimeOffset},
            {SqlDataTypeOption.Time, DbType.Time},
            {SqlDataTypeOption.Char, DbType.String},
            {SqlDataTypeOption.VarChar, DbType.String},
            {SqlDataTypeOption.Text, DbType.String},
            {SqlDataTypeOption.NChar, DbType.StringFixedLength},
            {SqlDataTypeOption.NVarChar, DbType.StringFixedLength},
            {SqlDataTypeOption.NText, DbType.StringFixedLength},
            {SqlDataTypeOption.Binary, DbType.Binary},
            {SqlDataTypeOption.VarBinary, DbType.Binary},
            {SqlDataTypeOption.Image, DbType.Binary},
            {SqlDataTypeOption.Sql_Variant, DbType.Object},
            {SqlDataTypeOption.UniqueIdentifier, DbType.Guid}
        };
        public static DbType MapToDbType(DataTypeReference sqlType)
        {
            if (sqlType is XmlDataTypeReference)
            {
                return DbType.Xml;
            }
            else if (sqlType is ParameterizedDataTypeReference parameterizedType)
            {
                string baseType = parameterizedType.Name.BaseIdentifier.Value.ToUpper();
                if (ParameterizedTypeMapping.TryGetValue(baseType, out var dbType))
                {
                    return dbType;
                }
                throw new NotImplementedException($"Mapping for SQL type {baseType} not implemented.");
            }
            else if (sqlType is SqlDataTypeReference simpleType)
            {
                if (SimpleTypeMapping.TryGetValue(simpleType.SqlDataTypeOption, out var dbType))
                {
                    return dbType;
                }
                throw new NotImplementedException($"Mapping for SQL type {simpleType.SqlDataTypeOption} not implemented.");
            }
            else
            {
                throw new NotImplementedException($"Mapping for SQL type {sqlType.GetType().Name} not implemented.");
            }
        }

        public static string GetSqlServerDataType(Type type, string defaultColDataType)
        {
            switch (type.Name)
            {
                case "String":
                    return defaultColDataType;
                case "Boolean":
                    return "BIT";
                case "Byte":
                    return "TINYINT";
                case "Int16":
                    return "SMALLINT";
                case "Int32":
                    return "INT";
                case "Int64":
                    return "BIGINT";
                case "DateTime":
                    return "DATETIME";
                case "Decimal":
                    return "VARCHAR(255)";
                case "Double":
                    return "FLOAT";
                case "Single":
                    return "REAL";
                case "Guid":
                    return "UNIQUEIDENTIFIER";
                case "Byte[]":
                    return "VARBINARY(MAX)";
                case "TimeSpan":
                    return "TIME";
                default:
                    return "VARCHAR(255)"; // Or throw an exception, depending on your needs
            }
        }

        public static string GetSqlServerDataType(string Name)
        {
            switch (Name)
            {
                case "String":
                    return "NVARCHAR(MAX)";
                case "Boolean":
                    return "BIT";
                case "Byte":
                    return "TINYINT";
                case "Int16":
                    return "SMALLINT";
                case "Int32":
                    return "INT";
                case "Int64":
                    return "BIGINT";
                case "DateTime":
                    return "DATETIME";
                case "Decimal":
                    return "VARCHAR(255)";
                case "Double":
                    return "FLOAT";
                case "Single":
                    return "REAL";
                case "Guid":
                    return "UNIQUEIDENTIFIER";
                case "Byte[]":
                    return "VARBINARY(MAX)";
                case "TimeSpan":
                    return "TIME";
                default:
                    return null; // Or throw an exception, depending on your needs
            }
        }

        public static string GetSqlTypeFromDotNetType(Type type, int size, short precision, short scale)
        {
            if (type == typeof(string))
            {
                return size == -1 || size > 8000 ? "NVARCHAR(MAX)" : $"NVARCHAR({size})";
            }
            if (type == typeof(char)) return "NCHAR(1)";
            if (type == typeof(byte)) return "TINYINT";
            if (type == typeof(sbyte)) return "SMALLINT"; // No direct SQL type equivalent for sbyte
            if (type == typeof(short)) return "SMALLINT";
            if (type == typeof(ushort)) return "INT";  // No direct SQL type equivalent for ushort
            if (type == typeof(int)) return "INT";
            if (type == typeof(uint)) return "BIGINT"; // No direct SQL type equivalent for uint
            if (type == typeof(long)) return "BIGINT";
            if (type == typeof(ulong)) return "NUMERIC(20)"; // No direct SQL type equivalent for ulong
            if (type == typeof(byte[]))
            {
                return size == -1 ? "VARBINARY(MAX)" : $"VARBINARY({size})";
            }
            if (type == typeof(bool)) return "BIT";
            if (type == typeof(DateTime)) return "DATETIME2";
            if (type == typeof(DateTimeOffset)) return "DATETIMEOFFSET";
            if (type == typeof(decimal)) return $"DECIMAL({precision}, {scale})";
            if (type == typeof(float)) return "REAL";
            if (type == typeof(double)) return "FLOAT";
            if (type == typeof(Guid)) return "UNIQUEIDENTIFIER";
            if (type == typeof(TimeSpan)) return "TIME";
            if (type == typeof(object)) return "SQL_VARIANT";
            if (type == typeof(char[])) return "NVARCHAR(MAX)";
            if (type == typeof(XmlDocument)) return "XML"; // Needs `using System.Xml;`
            if (type == typeof(SqlGeography)) return "GEOGRAPHY"; // Needs `using Microsoft.SqlServer.Types;`
            if (type == typeof(SqlGeometry)) return "GEOMETRY"; // Needs `using Microsoft.SqlServer.Types;`

            throw new NotSupportedException($"Type {type} is not supported");
        }

        public static string CreateTableSql(DataTable tbl, string threePartTableName, string defaultColDataType)
        {
            var columnsSql = new List<string>();

            foreach (DataColumn col in tbl.Columns)
            {
                Type type = col.DataType;
                string sqlType = GetSqlServerDataType(type, defaultColDataType);
                columnsSql.Add($"[{col.ColumnName}] {sqlType} NULL");
            }

            string columnsJoined = string.Join(", ", columnsSql);
            return $"CREATE TABLE {threePartTableName} ({columnsJoined});";
        }
    }
}
