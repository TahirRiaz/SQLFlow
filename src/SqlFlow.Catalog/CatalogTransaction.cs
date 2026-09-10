using System.Data;
using Microsoft.EntityFrameworkCore;

namespace SqlFlow.Catalog;

/// <summary>
/// Runs a unit of catalog work inside one serializable transaction, wrapped in the context's execution strategy so
/// the whole unit is retried atomically. This is required whenever the context enables connection resiliency
/// (<c>EnableRetryOnFailure</c>, as the control plane's pooled context does): EF Core forbids a user-initiated
/// transaction outside an execution strategy, because a transient retry must replay the entire transaction, not
/// half of it. The change tracker is reset before each attempt so a unit retried after a failed attempt never
/// double-adds the entities the prior attempt left tracked; the supplied work must therefore rebuild its staged
/// changes from source each call. When resiliency is off the strategy simply runs the work once.
/// </summary>
internal static class CatalogTransaction
{
    public static Task<T> InSerializableAsync<T>(
        CatalogDbContext context, Func<Task<T>> work, CancellationToken ct)
    {
        var strategy = context.Database.CreateExecutionStrategy();
        return strategy.ExecuteAsync(async () =>
        {
            context.ChangeTracker.Clear();
            await using var transaction = await context.Database
                .BeginTransactionAsync(IsolationLevel.Serializable, ct).ConfigureAwait(false);
            var result = await work().ConfigureAwait(false);
            await context.SaveChangesAsync(ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return result;
        });
    }
}
