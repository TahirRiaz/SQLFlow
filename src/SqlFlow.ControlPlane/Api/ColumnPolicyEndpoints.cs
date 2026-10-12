using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;

namespace SqlFlow.ControlPlane.Api;

/// <summary>One column of an object as the AI context pages show it: identity, type, whether an approved AI context
/// opens it (and which), and what that context says it means. Every column is listed, open or not.</summary>
public sealed record ColumnPolicyStateDto(
    string ObjectKey, string ColumnName, int Ordinal, string? DataType, bool Nullable,
    bool IsAllowed, string? Reason, string? UpdatedBy, DateTime? UpdatedUtc,
    string? Description, IReadOnlyList<string> Synonyms, string? Context);

/// <summary>One column currently NOT on the allow-list - either explicitly denied, or never reviewed at all -
/// with enough of its owning object to render a flat "everything currently blocked" list without a second
/// lookup per row. <see cref="UpdatedUtc"/> is null for a column that has no policy row at all: it was never
/// reviewed, as opposed to reviewed and denied.</summary>
public sealed record RestrictedColumnDto(
    string ObjectKey, string ObjectName, string? Database, string? Schema, string ColumnName,
    string? Reason, string? UpdatedBy, DateTime? UpdatedUtc);

/// <summary>
/// Reads <see cref="CatalogColumnPolicy"/>: which columns an AI assistant (and the ad-hoc query surface generally) may
/// read. The allow-list is the semantic layer's membership and is written only by approving an AI context
/// (<see cref="AiContextEndpoints"/>), so these are its read side: every column not open to an assistant, and one
/// object's columns with their state. The model is default-deny: a column no approved context lists is closed.
///
/// It lives under the "admin" scope, not "operate": what an assistant may see is a governance matter, the same category
/// as user and role administration.
/// </summary>
public static class ColumnPolicyEndpoints
{

    public static RouteGroupBuilder MapColumnPolicyEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        group.MapGet("/powerai/column-policies", ListRestrictedAsync)
            .WithTags("PowerAI").WithName("ListColumnPolicies");
        group.MapGet("/powerai/column-policies/objects/{key}", GetObjectPolicyStateAsync)
            .WithTags("PowerAI").WithName("GetObjectColumnPolicyState");

        return group;
    }

    /// <summary>Every column currently NOT allowed, across the whole catalog, for the policy screen's overview
    /// list: a column with an explicit denial, or one with no policy row at all (never reviewed).</summary>
    private static async Task<Results<Ok<PagedResult<RestrictedColumnDto>>, ProblemHttpResult>> ListRestrictedAsync(
        CatalogDbContext db, int? page, int? pageSize, CancellationToken ct)
    {
        var (p, size) = PageRequest.Normalize(page, pageSize);
        var rows = from c in db.ObjectColumns.AsNoTracking()
                   join o in db.Objects.AsNoTracking() on c.ObjectKey equals o.Key
                   join pol in db.ColumnPolicies.AsNoTracking()
                       on new { c.ObjectKey, ColumnName = c.Name } equals new { pol.ObjectKey, pol.ColumnName }
                       into policies
                   from pol in policies.DefaultIfEmpty()
                   where pol == null || !pol.IsAllowed
                   orderby o.Name, c.Name
                   select new RestrictedColumnDto(
                       c.ObjectKey, o.Name, o.Database, o.Schema, c.Name,
                       pol != null ? pol.Reason : null, pol != null ? pol.UpdatedBy : null,
                       pol != null ? pol.UpdatedUtc : (DateTime?)null);

        var total = await rows.LongCountAsync(ct).ConfigureAwait(false);
        var items = await rows.Skip((p - 1) * size).Take(size).ToListAsync(ct).ConfigureAwait(false);
        return TypedResults.Ok(new PagedResult<RestrictedColumnDto>(items, p, size, total));
    }

    /// <summary>Every column of one object with its current policy state, so the admin screen can render a
    /// toggle per column without the caller having to diff two separate lists.</summary>
    private static async Task<Results<Ok<IReadOnlyList<ColumnPolicyStateDto>>, ProblemHttpResult>> GetObjectPolicyStateAsync(
        string key, CatalogDbContext db, CancellationToken ct)
    {
        var exists = await db.Objects.AsNoTracking().AnyAsync(o => o.Key == key, ct).ConfigureAwait(false);
        if (!exists)
        {
            return NotFound("object", key);
        }

        return TypedResults.Ok(await LoadObjectStateAsync(db, key, ct).ConfigureAwait(false));
    }

    /// <summary>Every catalogued column of <paramref name="key"/> with its policy state, in column order. Shared with
    /// the semantic layer editor so both show the one reading of a column's state.</summary>
    internal static async Task<IReadOnlyList<ColumnPolicyStateDto>> LoadObjectStateAsync(
        CatalogDbContext db, string key, CancellationToken ct)
    {
        var rows = await (from c in db.ObjectColumns.AsNoTracking()
                          where c.ObjectKey == key
                          join pol in db.ColumnPolicies.AsNoTracking()
                              on new { c.ObjectKey, ColumnName = c.Name } equals new { pol.ObjectKey, pol.ColumnName }
                              into policies
                          from pol in policies.DefaultIfEmpty()
                          orderby c.Ordinal, c.Name
                          select new
                          {
                              c.ObjectKey, c.Name, c.Ordinal, c.DataType, c.Nullable,
                              IsAllowed = pol != null && pol.IsAllowed,
                              Reason = pol != null ? pol.Reason : null,
                              UpdatedBy = pol != null ? pol.UpdatedBy : null,
                              UpdatedUtc = pol != null ? pol.UpdatedUtc : (DateTime?)null,
                              Description = pol != null ? pol.Description : null,
                              Synonyms = pol != null ? pol.Synonyms : null,
                              Context = pol != null ? pol.AiContext : null,
                          })
            .ToListAsync(ct).ConfigureAwait(false);

        return rows
            .Select(r => new ColumnPolicyStateDto(
                r.ObjectKey, r.Name, r.Ordinal, r.DataType, r.Nullable, r.IsAllowed, r.Reason, r.UpdatedBy, r.UpdatedUtc,
                r.Description, SemanticLayer.SplitSynonyms(r.Synonyms), r.Context))
            .ToList();
    }

    private static ProblemHttpResult NotFound(string resource, string key)
        => TypedResults.Problem(detail: $"No {resource} with key '{key}'.", statusCode: StatusCodes.Status404NotFound, title: "Not found");

    private static ProblemHttpResult Problem(string detail, int statusCode, string title)
        => TypedResults.Problem(detail: detail, statusCode: statusCode, title: title);
}
