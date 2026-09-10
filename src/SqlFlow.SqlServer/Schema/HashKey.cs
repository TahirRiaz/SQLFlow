using SqlFlow.Core;

namespace SqlFlow.SqlServer.Schema;

/// <summary>The injected change-detection hash key column (legacy HashKey_DW), sized to the algorithm.</summary>
public static class HashKey
{
    public const string ColumnName = "HashKey_DW";

    /// <summary>The legacy default algorithm when none is configured.</summary>
    public const string DefaultAlgorithm = "SHA2_256";

    /// <summary>The exact <c>binary(N)</c> target type for a HASHBYTES algorithm: SHA2_512 to binary(64),
    /// SHA2_256 to binary(32), SHA1 to binary(20), MD5 to binary(16). An unknown algorithm fails fast.</summary>
    public static SqlDataType BinaryTypeFor(string? algorithm)
    {
        var name = string.IsNullOrWhiteSpace(algorithm) ? DefaultAlgorithm : algorithm.Trim().ToUpperInvariant();
        var bytes = name switch
        {
            "SHA2_512" => 64,
            "SHA2_256" => 32,
            "SHA" or "SHA1" => 20,
            "MD2" or "MD4" or "MD5" => 16,
            _ => throw new SqlFlowException(
                $"Unknown hash algorithm '{algorithm}'. Valid values: SHA2_512, SHA2_256, SHA1, MD5 (also MD2, MD4, SHA)."),
        };

        return new SqlDataType { BaseType = "binary", Length = bytes };
    }
}
