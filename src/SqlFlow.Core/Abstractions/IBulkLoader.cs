using System.Data.Common;
using SqlFlow.Core.Model;

namespace SqlFlow.Core.Abstractions;

/// <summary>Moves rows into the target. The reader is consumed forward-only, never buffered.</summary>
public interface IBulkLoader
{
    Task<long> LoadAsync(string connectionString, TargetSpec target, DbDataReader data, LoadPolicy load, CancellationToken ct = default);

    Task TruncateAsync(string connectionString, TargetSpec target, CancellationToken ct = default);
}
