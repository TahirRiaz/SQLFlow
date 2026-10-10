using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;

namespace SqlFlow.ControlPlane.Api;

/// <summary>One column of an object as the policy admin screen needs it: identity, type, whether it is currently
/// allowed, and its semantic annotations. Unlike <see cref="ObjectColumnDto"/> this is never filtered by policy, since
/// managing the policy requires seeing every column, allowed or not.</summary>
public sealed record ColumnPolicyStateDto(
    string ObjectKey, string ColumnName, int Ordinal, string? DataType, bool Nullable,
    bool IsAllowed, string? Reason, string? UpdatedBy, DateTime? UpdatedUtc,
    string? Description, IReadOnlyList<string> Synonyms);

/// <summary>One column currently NOT on the allow-list - either explicitly denied, or never reviewed at all -
/// with enough of its owning object to render a flat "everything currently blocked" list without a second
/// lookup per row. <see cref="UpdatedUtc"/> is null for a column that has no policy row at all: it was never
/// reviewed, as opposed to reviewed and denied.</summary>
public sealed record RestrictedColumnDto(
    string ObjectKey, string ObjectName, string? Database, string? Schema, string ColumnName,
    string? Reason, string? UpdatedBy, DateTime? UpdatedUtc);

/// <summary>Sets one column's allow flag and semantic annotations: a full upsert of the row, so every field is
/// written as given. Setting <see cref="IsAllowed"/> false denies the column again but keeps the row (and its audit
/// trail) rather than deleting it.</summary>
public sealed record SetColumnPolicyRequest(
    string? ObjectKey, string? ColumnName, bool IsAllowed, string? Reason,
    string? Description = null, IReadOnlyList<string>? Synonyms = null);

/// <summary>Allows or denies every catalogued column of one object at once, keeping each row's reason and
/// annotations.</summary>
public sealed record SetObjectColumnPoliciesRequest(string? ObjectKey, bool IsAllowed);

/// <summary>
/// Administers <see cref="CatalogColumnPolicy"/>: which columns an AI assistant (and the ad-hoc query surface
/// generally) may read at all. This is the write side of the allow-list that <see cref="SearchEndpoints"/>,
/// <see cref="LineageEndpoints"/>'s object columns/dossier, <see cref="SemanticLayerEndpoints"/>, and
/// <see cref="ColumnPolicyGuard"/> all enforce. The allow-list IS the semantic layer's membership: a column allowed
/// here is in the layer, and its description and synonyms are the layer's per-column annotations. The model is
/// default-deny: a column with no row here is blocked exactly like one explicitly denied, so a table the catalog has
/// just learned about (or a column a resync just added) starts out closed until an admin allows it.
///
/// It lives under the "admin" scope, not "operate": deciding what data an assistant may see is a governance
/// decision about the estate's access surface, the same category as user/role administration, not a day-to-day
/// operational action.
/// </summary>
public static class ColumnPolicyEndpoints
{
    private const int MaxReasonLength = 512;
    private const int MaxDescriptionLength = 2000;

    public static RouteGroupBuilder MapColumnPolicyEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        group.MapGet("/powerai/column-policies", ListRestrictedAsync)
            .WithTags("PowerAI").WithName("ListColumnPolicies");
        group.MapGet("/powerai/column-policies/objects/{key}", GetObjectPolicyStateAsync)
            .WithTags("PowerAI").WithName("GetObjectColumnPolicyState");
        group.MapPut("/powerai/column-policies", SetAsync)
            .WithTags("PowerAI").WithName("SetColumnPolicy");
        group.MapPut("/powerai/column-policies/objects", SetObjectAsync)
            .WithTags("PowerAI").WithName("SetObjectColumnPolicies");

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
                          })
            .ToListAsync(ct).ConfigureAwait(false);

        return rows
            .Select(r => new ColumnPolicyStateDto(
                r.ObjectKey, r.Name, r.Ordinal, r.DataType, r.Nullable, r.IsAllowed, r.Reason, r.UpdatedBy, r.UpdatedUtc,
                r.Description, SemanticLayer.SplitSynonyms(r.Synonyms)))
            .ToList();
    }

    /// <summary>Sets one column's allow flag and annotations. Upserts on (ObjectKey, ColumnName), so calling it
    /// twice for the same column updates the one row rather than accumulating history.</summary>
    private static async Task<Results<Ok<ColumnPolicyStateDto>, ProblemHttpResult>> SetAsync(
        SetColumnPolicyRequest request, CatalogDbContext db, TimeProvider clock, ClaimsPrincipal user,
        CancellationToken ct)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.ObjectKey) || string.IsNullOrWhiteSpace(request.ColumnName))
        {
            return Problem(
                "objectKey and columnName are both required.", StatusCodes.Status400BadRequest, "Invalid request");
        }

        var objectKey = request.ObjectKey.Trim();
        var columnName = request.ColumnName.Trim();

        var column = await db.ObjectColumns.AsNoTracking()
            .FirstOrDefaultAsync(c => c.ObjectKey == objectKey && c.Name == columnName, ct).ConfigureAwait(false);
        if (column is null)
        {
            return Problem(
                $"No column '{columnName}' is known on object '{objectKey}'. Sync the catalog first, or check " +
                "the spelling.",
                StatusCodes.Status404NotFound, "Not found");
        }

        var reason = SemanticLayer.TrimToNull(request.Reason);
        if (reason is { Length: > MaxReasonLength })
        {
            return Problem($"reason is longer than {MaxReasonLength} characters.", StatusCodes.Status400BadRequest, "Invalid request");
        }

        var description = SemanticLayer.TrimToNull(request.Description);
        if (description is { Length: > MaxDescriptionLength })
        {
            return Problem(
                $"description is longer than {MaxDescriptionLength} characters.", StatusCodes.Status400BadRequest, "Invalid request");
        }

        if (!SemanticLayer.TryJoinSynonyms(request.Synonyms, out var synonyms, out var synonymProblem))
        {
            return Problem(synonymProblem!, StatusCodes.Status400BadRequest, "Invalid request");
        }

        var updatedBy = SemanticLayer.Actor(user);
        var now = clock.GetUtcNow().UtcDateTime;

        // Tracked explicitly: the catalog context defaults to no-tracking, under which the update below would be
        // silently dropped by SaveChanges.
        var policy = await db.ColumnPolicies.AsTracking()
            .FirstOrDefaultAsync(p => p.ObjectKey == objectKey && p.ColumnName == columnName, ct)
            .ConfigureAwait(false);
        if (policy is null)
        {
            policy = new CatalogColumnPolicy { ObjectKey = objectKey, ColumnName = column.Name };
            db.ColumnPolicies.Add(policy);
        }

        policy.IsAllowed = request.IsAllowed;
        policy.Reason = reason;
        policy.Description = description;
        policy.Synonyms = synonyms;
        policy.UpdatedBy = updatedBy;
        policy.UpdatedUtc = now;

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return TypedResults.Ok(new ColumnPolicyStateDto(
            objectKey, column.Name, column.Ordinal, column.DataType, column.Nullable,
            policy.IsAllowed, policy.Reason, policy.UpdatedBy, policy.UpdatedUtc,
            policy.Description, SemanticLayer.SplitSynonyms(policy.Synonyms)));
    }

    /// <summary>
    /// Allows or denies every catalogued column of one object in a single save: bringing a whole table into the
    /// semantic layer, or taking it out, without a request per column. Existing rows keep their reason and
    /// annotations; columns never reviewed gain a row. A concurrent single-column save can insert one of those rows
    /// first, so a unique-index conflict re-reads the rows and applies the change once more.
    /// </summary>
    private static async Task<Results<Ok<IReadOnlyList<ColumnPolicyStateDto>>, ProblemHttpResult>> SetObjectAsync(
        SetObjectColumnPoliciesRequest request, CatalogDbContext db, TimeProvider clock, ClaimsPrincipal user,
        CancellationToken ct)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.ObjectKey))
        {
            return Problem("objectKey is required.", StatusCodes.Status400BadRequest, "Invalid request");
        }

        var objectKey = request.ObjectKey.Trim();
        if (!await db.Objects.AsNoTracking().AnyAsync(o => o.Key == objectKey, ct).ConfigureAwait(false))
        {
            return NotFound("object", objectKey);
        }

        var columnNames = await db.ObjectColumns.AsNoTracking()
            .Where(c => c.ObjectKey == objectKey)
            .Select(c => c.Name)
            .ToListAsync(ct).ConfigureAwait(false);

        var updatedBy = SemanticLayer.Actor(user);
        var now = clock.GetUtcNow().UtcDateTime;

        for (var attempt = 0; ; attempt++)
        {
            var existing = await db.ColumnPolicies.AsTracking()
                .Where(p => p.ObjectKey == objectKey)
                .ToListAsync(ct).ConfigureAwait(false);

            foreach (var name in columnNames)
            {
                var policy = existing.Find(p => string.Equals(p.ColumnName, name, StringComparison.OrdinalIgnoreCase));
                if (policy is null)
                {
                    policy = new CatalogColumnPolicy { ObjectKey = objectKey, ColumnName = name };
                    db.ColumnPolicies.Add(policy);
                }
                else if (policy.IsAllowed == request.IsAllowed)
                {
                    continue;
                }

                policy.IsAllowed = request.IsAllowed;
                policy.UpdatedBy = updatedBy;
                policy.UpdatedUtc = now;
            }

            try
            {
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                break;
            }
            catch (DbUpdateException) when (attempt == 0)
            {
                db.ChangeTracker.Clear();
            }
        }

        return TypedResults.Ok(await LoadObjectStateAsync(db, objectKey, ct).ConfigureAwait(false));
    }

    private static ProblemHttpResult NotFound(string resource, string key)
        => TypedResults.Problem(detail: $"No {resource} with key '{key}'.", statusCode: StatusCodes.Status404NotFound, title: "Not found");

    private static ProblemHttpResult Problem(string detail, int statusCode, string title)
        => TypedResults.Problem(detail: detail, statusCode: statusCode, title: title);
}
