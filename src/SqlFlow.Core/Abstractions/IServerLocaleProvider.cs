using SqlFlow.Core.Model;

namespace SqlFlow.Core.Abstractions;

/// <summary>
/// Resolves the single locale an inference run binds to: the locale configured on the target server
/// (its session date order plus collation decimal/grouping convention). Resolved once per run and
/// applied to every column so dates and numbers are interpreted consistently across a whole file.
/// </summary>
public interface IServerLocaleProvider
{
    Task<ServerLocale> GetServerLocaleAsync(string connectionString, CancellationToken ct = default);
}
