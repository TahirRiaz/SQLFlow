using Microsoft.EntityFrameworkCore;

namespace SqlFlow.Catalog;

/// <summary>
/// The retention an admin may set on the assistant's conversations: a conversation nobody has written in since the
/// cut-off goes, with every message in it, images included. Nothing is removed until an admin sets a limit on the
/// Maintenance page, and a conversation anyone wrote in within the limit keeps every message, its oldest included.
/// </summary>
public static class ChatRetention
{
    /// <summary>How many conversations one delete removes, with their messages: small enough that a conversation full of
    /// pasted screenshots does not hold a long lock, and a backlog is still a few round-trips.</summary>
    public const int DeleteBatchSize = 500;

    /// <summary>How many conversations were last written in before <paramref name="olderThanUtc"/>.</summary>
    public static Task<long> CountIdleAsync(CatalogDbContext catalog, DateTime olderThanUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return catalog.ChatConversations.LongCountAsync(c => c.UpdatedUtc < olderThanUtc, ct);
    }

    /// <summary>
    /// Deletes every conversation last written in before <paramref name="olderThanUtc"/>, and its messages, a batch of
    /// conversations at a time, each batch in one transaction. Both deletes re-read the conversation's last write, so a
    /// conversation someone writes in while a batch runs keeps its messages. Returns the conversations removed.
    /// </summary>
    public static async Task<long> RemoveIdleAsync(CatalogDbContext catalog, DateTime olderThanUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        long removed = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var batch = await catalog.ChatConversations.AsNoTracking()
                .Where(c => c.UpdatedUtc < olderThanUtc)
                .OrderBy(c => c.UpdatedUtc)
                .Select(c => c.Id)
                .Take(DeleteBatchSize)
                .ToListAsync(ct).ConfigureAwait(false);
            if (batch.Count == 0)
            {
                return removed;
            }

            // The catalog retries transient failures, so the batch's transaction runs as one retriable unit.
            var idle = catalog.ChatConversations.Where(c => batch.Contains(c.Id) && c.UpdatedUtc < olderThanUtc);
            removed += await catalog.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                await using var transaction = await catalog.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
                await catalog.ChatMessages
                    .Where(m => idle.Select(c => c.Id).Contains(m.ConversationId))
                    .ExecuteDeleteAsync(ct).ConfigureAwait(false);
                var conversations = await idle.ExecuteDeleteAsync(ct).ConfigureAwait(false);
                await transaction.CommitAsync(ct).ConfigureAwait(false);
                return conversations;
            }).ConfigureAwait(false);

            if (batch.Count < DeleteBatchSize)
            {
                return removed;
            }
        }
    }
}
