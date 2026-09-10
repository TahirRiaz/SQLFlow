using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;

namespace SqlFlow.ControlPlane.Notifications;

/// <summary>
/// Resolves where a subscription's messages go, at the moment a message is produced: the per-subscription
/// override first, then the owning user's account email (so a directory-driven address change applies to the next
/// message without touching the subscription). One resolver serves both the dispatch loop and the test-send
/// endpoint, so a passing test send is a true rehearsal of the real path. Returns null when nothing resolves.
/// </summary>
public static class NotificationTargetResolver
{
    public static async Task<string?> ResolveAsync(
        CatalogDbContext catalog, CatalogNotificationSubscription subscription, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(subscription);

        if (subscription.Channel == Catalog.NotificationChannels.Slack
            && !string.IsNullOrWhiteSpace(subscription.SlackTarget))
        {
            return subscription.SlackTarget;
        }

        var email = subscription.EmailAddress;
        if (string.IsNullOrWhiteSpace(email))
        {
            email = await catalog.Users.AsNoTracking()
                .Where(u => u.Id == subscription.UserId)
                .Select(u => u.Email)
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(email))
        {
            return null;
        }

        return subscription.Channel == Catalog.NotificationChannels.Slack
            ? NotificationTargets.SlackDm(email)
            : email;
    }

    /// <summary>The owner-facing explanation for an unresolvable destination, phrased per channel so the failed
    /// delivery row tells the user exactly what to fix.</summary>
    public static string UnresolvedReason(string channel)
        => channel == Catalog.NotificationChannels.Email
            ? "no destination email address: the subscription has no address and your account has no email."
            : "no Slack destination: the subscription has no channel id and your account has no email to resolve a direct message from.";
}
