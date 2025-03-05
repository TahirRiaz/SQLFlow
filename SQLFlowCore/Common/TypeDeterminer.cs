using System;
using System.Data;

namespace SQLFlowCore.Common
{
    internal class UnrecognizedTypeException : Exception
    {
        internal UnrecognizedTypeException(string value) : base($"The value '{value}' does not match any recognized types.")
        { }
    }

    internal enum SqlServerDataType
    {
        Int,
        SmallInt,
        BigInt,
        Float,
        Decimal,
        DateTime,
        Bit,
        NVarChar,
        // ... you can extend this enum with other data types as needed
    }

    internal class TypeInfo
    {
        internal Type DotNetType { get; set; }
        internal DbType DbType { get; set; }
        internal SqlServerDataType SqlServerType { get; set; }
        internal int Precision { get; set; }
        internal int Scale { get; set; }
        internal object ParsedValue { get; set; }
    }

    internal class TypeDeterminer
    {
        internal static TypeInfo GetValueType(string input, DataTable DateTimeFormats)
        {
            // CheckForError for DateTime
            DateTime checkValue = Functions.ExtractDateTimeFromString(input, DateTimeFormats);

            if (string.IsNullOrWhiteSpace(input))
            {
                throw new ArgumentException("Input string cannot be null or whitespace.", nameof(input));
            }

            // CheckForError for int
            if (int.TryParse(input, out int parsedInt))
            {
                return new TypeInfo { DotNetType = typeof(int), DbType = DbType.Int32, SqlServerType = SqlServerDataType.Int, ParsedValue = parsedInt };
            }

            // CheckForError for smallint
            if (short.TryParse(input, out short parsedShort))
            {
                return new TypeInfo { DotNetType = typeof(short), DbType = DbType.Int16, SqlServerType = SqlServerDataType.SmallInt, ParsedValue = parsedShort };
            }

            // CheckForError for bigint
            if (long.TryParse(input, out long parsedLong))
            {
                return new TypeInfo { DotNetType = typeof(long), DbType = DbType.Int64, SqlServerType = SqlServerDataType.BigInt, ParsedValue = parsedLong };
            }

            // CheckForError for float
            if (float.TryParse(input, out float parsedFloat))
            {
                return new TypeInfo { DotNetType = typeof(float), DbType = DbType.Single, SqlServerType = SqlServerDataType.Float, ParsedValue = parsedFloat };
            }

            // CheckForError for decimal
            if (decimal.TryParse(input, out decimal parsedDecimal))
            {
                int precision = input.Length - 1;
                int scale = input.Contains('.') ? input.Length - input.IndexOf('.') - 1 : 0;

                return new TypeInfo
                {
                    DotNetType = typeof(decimal),
                    DbType = DbType.Decimal,
                    SqlServerType = SqlServerDataType.Decimal,
                    ParsedValue = parsedDecimal,
                    Precision = precision,
                    Scale = scale
                };
            }

            // CheckForError for DateTime
            if (checkValue != FlowDates.Default)
            {
                return new TypeInfo { DotNetType = typeof(DateTime), DbType = DbType.DateTime, SqlServerType = SqlServerDataType.DateTime, ParsedValue = checkValue };
            }

            // CheckForError for bit (boolean)
            if (bool.TryParse(input, out bool parsedBool))
            {
                return new TypeInfo { DotNetType = typeof(bool), DbType = DbType.Boolean, SqlServerType = SqlServerDataType.Bit, ParsedValue = parsedBool };
            }

            // If it doesn't match numeric or date types, consider it as nvarchar for simplification
            if (input.Length <= 4000)
            {
                return new TypeInfo { DotNetType = typeof(string), DbType = DbType.String, SqlServerType = SqlServerDataType.NVarChar, ParsedValue = input };
            }

            // If the type is not recognized, throw an UnrecognizedTypeException.
            throw new UnrecognizedTypeException(input);
        }
    }
}
