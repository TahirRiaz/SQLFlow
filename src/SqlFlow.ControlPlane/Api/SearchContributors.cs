using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using SqlFlow.Core;
using SqlFlow.Core.Secrets;

namespace SqlFlow.ControlPlane.Api;

/// <summary>
/// A category a host module adds to the control plane's search, beside the built-in catalog surfaces. The combined
/// <c>GET /search/all</c> previews it under <see cref="Key"/> (a top-level member of the result, which is where a GUI
/// module's search category reads it), and <c>GET /search/categories/{key}</c> pages it. Contributors are registered
/// in the host's composition root and resolved per request, so a contributor may depend on scoped services.
/// </summary>
/// <remarks>
/// Contributors run one at a time, after the built-in categories, in registration order: they may share the
/// request's scoped services (a pooled <c>DbContext</c> is not safe for concurrent use). A contributor that throws or
/// returns a result outside the contract fails its own category only: the combined result carries the category with
/// no hits and an <see cref="ContributedSearchCategoryDto.Error"/>, and every other category is answered as usual.
/// </remarks>
public interface ISearchContributor
{
    /// <summary>The category's key: its member name in the combined result and its route segment. Lower camel case
    /// (<c>^[a-z][A-Za-z0-9]{0,39}$</c>), unique among contributors, and never a member the combined result already
    /// has (<c>flows</c>, <c>objects</c>, ...).</summary>
    string Key { get; }

    /// <summary>The category's display name ("Records").</summary>
    string Label { get; }

    /// <summary>An authorization policy the caller must satisfy to see the category, beyond the search surface's own
    /// <c>read</c> policy; null when the search surface's policy is enough. A caller who does not satisfy it never
    /// sees the category in the combined result and is refused on the paged endpoint.</summary>
    string? RequiredPolicy { get; }

    /// <summary>Answers one page of the category for the parsed query. The result holds at most
    /// <see cref="SearchContributionRequest.PageSize"/> hits; a total counted only as far as a bound sets
    /// <see cref="SearchContribution.TotalCapped"/>. A refusal a caller can act on is a
    /// <see cref="SqlFlowException"/>, whose redacted message is shown; any other exception is logged and reported
    /// without its message.</summary>
    Task<SearchContribution> SearchAsync(SearchContributionRequest request, CancellationToken ct);
}

/// <summary>What a contributor is asked: the query as the search parsed it (the trimmed phrase and the tokens every hit
/// must match), the one-based page and its size, and the caller, so a contributor can narrow hits to what the caller
/// may see.</summary>
public sealed record SearchContributionRequest(
    string Phrase, IReadOnlyList<string> Tokens, int Page, int PageSize, ClaimsPrincipal User);

/// <summary>One hit of a contributed category. <see cref="Route"/> is where the hit opens: a GUI route (an absolute
/// path, <c>/records/42</c>) or an absolute http(s) link, or null when it opens nowhere. <see cref="Data"/> is the
/// contributor's own detail for its GUI renderer, serialized as it is.</summary>
public sealed record SearchHitDto(string Id, string Title, string? Subtitle, string? Route, object? Data = null);

/// <summary>One page a contributor answers: the hits, the total match count, and whether that total stopped at a
/// bound (it is then a floor: that many match, and more).</summary>
public sealed record SearchContribution(IReadOnlyList<SearchHitDto> Items, long Total, bool TotalCapped = false);

/// <summary>A contributed category as the combined search returns it, under its key. <see cref="Error"/> is null when
/// the contributor answered; otherwise it says why the category holds no hits, and never carries a secret.</summary>
public sealed record ContributedSearchCategoryDto(
    string Label, long Total, IReadOnlyList<SearchHitDto> Items, bool TotalCapped, string? Error);

/// <summary>The rules every <see cref="ISearchContributor"/> is held to, and the one path that runs them.</summary>
public static partial class SearchContributors
{
    /// <summary>The members the combined result already has, which a contributed key would collide with.</summary>
    private static readonly HashSet<string> ReservedKeys = typeof(AllSearchDto)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.GetCustomAttribute<JsonExtensionDataAttribute>() is null)
        .Select(p => JsonNamingPolicy.CamelCase.ConvertName(p.Name))
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex("^[a-z][A-Za-z0-9]{0,39}$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyPattern();

    /// <summary>
    /// Refuses a registration the search could not serve: a key outside the pattern, a key the combined result already
    /// uses, two contributors sharing a key (compared without case), a blank label, or a policy the host does not
    /// define. Run once at startup, so a conflicting module fails with a message that names it.
    /// </summary>
    public static async Task ValidateAsync(
        IEnumerable<ISearchContributor> contributors, IAuthorizationPolicyProvider policies)
    {
        ArgumentNullException.ThrowIfNull(contributors);
        ArgumentNullException.ThrowIfNull(policies);

        var owners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var contributor in contributors)
        {
            var type = contributor.GetType().FullName ?? contributor.GetType().Name;
            var key = contributor.Key;
            if (key is null || !KeyPattern().IsMatch(key))
            {
                throw new InvalidOperationException(
                    $"Search contributor {type} has the key '{key}', which is not a lower camel case name of at most 40 letters and digits.");
            }

            if (ReservedKeys.Contains(key))
            {
                throw new InvalidOperationException(
                    $"Search contributor {type} has the key '{key}', which the combined search result already uses for a built-in category.");
            }

            if (!owners.TryAdd(key, type))
            {
                throw new InvalidOperationException(
                    $"The search category key '{key}' is registered by both {owners[key]} and {type}.");
            }

            if (string.IsNullOrWhiteSpace(contributor.Label))
            {
                throw new InvalidOperationException($"Search contributor {type} ('{key}') needs a non-blank label.");
            }

            if (contributor.RequiredPolicy is { } policy
                && await policies.GetPolicyAsync(policy).ConfigureAwait(false) is null)
            {
                throw new InvalidOperationException(
                    $"Search contributor {type} ('{key}') requires the authorization policy '{policy}', which the host does not define.");
            }
        }
    }

    /// <summary>The registered contributor with <paramref name="key"/> (compared without case), or null.</summary>
    internal static ISearchContributor? Find(IEnumerable<ISearchContributor> contributors, string key)
        => contributors.FirstOrDefault(c => string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether the caller may see the contributor's category.</summary>
    internal static async Task<bool> IsVisibleAsync(
        ISearchContributor contributor, ClaimsPrincipal user, IAuthorizationService authorization)
        => contributor.RequiredPolicy is null
           || (await authorization.AuthorizeAsync(user, contributor.RequiredPolicy).ConfigureAwait(false)).Succeeded;

    /// <summary>
    /// The contributed categories of one combined search, keyed as the result carries them: every contributor the
    /// caller may see, asked for the first <paramref name="previewSize"/> hits, one after another. A failing
    /// contributor yields its category with an error; the others are unaffected. Cancellation of the request itself
    /// propagates.
    /// </summary>
    internal static async Task<Dictionary<string, object>> CollectAsync(
        IEnumerable<ISearchContributor> contributors, string phrase, IReadOnlyList<string> tokens, int previewSize,
        ClaimsPrincipal user, IAuthorizationService authorization, string correlationId, ILogger logger,
        CancellationToken ct)
    {
        var categories = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var contributor in contributors)
        {
            if (!await IsVisibleAsync(contributor, user, authorization).ConfigureAwait(false))
            {
                continue;
            }

            var request = new SearchContributionRequest(phrase, tokens, 1, previewSize, user);
            var outcome = await RunAsync(contributor, request, correlationId, logger, ct).ConfigureAwait(false);
            categories[contributor.Key] = outcome.Result is { } result
                ? new ContributedSearchCategoryDto(contributor.Label, result.Total, result.Items, result.TotalCapped, Error: null)
                : new ContributedSearchCategoryDto(contributor.Label, 0, [], TotalCapped: false, outcome.Error);
        }

        return categories;
    }

    /// <summary>
    /// Runs one contributor and holds its answer to the contract. Exactly one of the outcome's members is set: the
    /// result, or an error that is safe to show (a <see cref="SqlFlowException"/>'s redacted message, a contract
    /// breach, or for any other failure only the correlation id the log carries it under).
    /// </summary>
    internal static async Task<ContributionOutcome> RunAsync(
        ISearchContributor contributor, SearchContributionRequest request, string correlationId, ILogger logger,
        CancellationToken ct)
    {
        SearchContribution? result;
        try
        {
            result = await contributor.SearchAsync(request, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (SqlFlowException ex)
        {
            var redacted = SecretHygiene.RedactedMessage(ex);
            logger.LogWarning(
                "Search category {Category} refused request {CorrelationId}: {Message}", contributor.Key, correlationId, redacted);
            return new ContributionOutcome(null, $"The {contributor.Label} search failed: {redacted}");
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex, "Search category {Category} failed for request {CorrelationId}: {Message}",
                contributor.Key, correlationId, SecretHygiene.RedactedMessage(ex));
            return new ContributionOutcome(
                null,
                $"The {contributor.Label} search failed unexpectedly; the control plane log has the details under correlation id {correlationId}.");
        }

        if (ContractViolation(result, request.PageSize) is { } violation)
        {
            logger.LogError(
                "Search category {Category} returned an invalid result for request {CorrelationId}: {Violation}",
                contributor.Key, correlationId, violation);
            return new ContributionOutcome(null, $"The {contributor.Label} search returned an invalid result: {violation}");
        }

        return new ContributionOutcome(result, null);
    }

    /// <summary>
    /// What is wrong with a contributor's result, or null when it keeps the contract: a result, a non-negative total, at
    /// most a page of hits, and every hit with an id, a title and a usable route. Public so a contributor's own tests
    /// hold it to the same rule the search enforces, rather than restating the rule and letting the two drift apart.
    /// </summary>
    public static string? ContractViolation(SearchContribution? result, int pageSize)
    {
        if (result is null)
        {
            return "no result.";
        }

        if (result.Items is null)
        {
            return "no hit list.";
        }

        if (result.Total < 0)
        {
            return $"a negative total ({result.Total}).";
        }

        if (result.Items.Count > pageSize)
        {
            return $"{result.Items.Count} hits for a page of {pageSize}.";
        }

        for (var i = 0; i < result.Items.Count; i++)
        {
            var hit = result.Items[i];
            if (hit is null)
            {
                return $"hit {i + 1} is missing.";
            }

            if (string.IsNullOrWhiteSpace(hit.Id) || string.IsNullOrWhiteSpace(hit.Title))
            {
                return $"hit {i + 1} has no id or no title.";
            }

            if (hit.Route is not null && !IsUsableRoute(hit.Route))
            {
                return $"hit {i + 1} has a route that is neither an absolute GUI path nor an http(s) link.";
            }
        }

        return null;
    }

    /// <summary>An absolute GUI path (<c>/x</c>, never the protocol-relative <c>//host</c>) or an absolute http(s)
    /// link.</summary>
    private static bool IsUsableRoute(string route)
    {
        if (route.StartsWith('/'))
        {
            return !route.StartsWith("//", StringComparison.Ordinal) && !route.Contains('\\', StringComparison.Ordinal);
        }

        return Uri.TryCreate(route, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);
    }

    /// <summary>One contributor's answer: its result, or the error its category reports.</summary>
    internal sealed record ContributionOutcome(SearchContribution? Result, string? Error);
}
