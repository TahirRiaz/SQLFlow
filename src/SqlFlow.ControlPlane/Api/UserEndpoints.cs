using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using SqlFlow.Catalog;

namespace SqlFlow.ControlPlane.Api;

/// <summary>
/// User and role administration, mapped under the <c>admin</c> scope. Local users are created, credentialed,
/// renamed, and removed here; SSO users appear via their first sign-in (JIT) and are governed here (profile, role,
/// active, removal). Deactivation is the reversible removal and keeps the account attributable; delete is the
/// permanent one and takes the user's private rows (tokens, notification subscriptions and history, chat
/// conversations) with it, while run history and activity events keep naming them. The last active admin can never
/// be demoted, deactivated, or deleted (the store enforces it), so the catalog cannot be administered into a
/// lockout, and no admin can delete the account they are signed in as.
/// </summary>
public static class UserEndpoints
{
    /// <summary>Local passwords must be at least this long. The rule lives in <see cref="LocalPasswords"/> so the
    /// API and the CLI's offline <c>user</c> command enforce the same length; this alias keeps the existing call
    /// sites unchanged.</summary>
    public const int MinPasswordLength = LocalPasswords.MinLength;

    /// <summary>Bounds matching the catalog columns, so an over-long field is a 400 with a readable reason instead
    /// of a truncation error from the database.</summary>
    private const int MaxUsernameLength = 256;

    private const int MaxDisplayNameLength = 256;

    private const int MaxEmailLength = 320;

    public static RouteGroupBuilder MapUserEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        group.MapGet("/users", ListAsync).WithTags("Users").WithName("ListUsers");
        group.MapGet("/users/{id:guid}", GetAsync).WithTags("Users").WithName("GetUser");
        group.MapPost("/users", CreateAsync).WithTags("Users").WithName("CreateUser");
        group.MapPost("/users/{id:guid}/profile", SetProfileAsync).WithTags("Users").WithName("SetUserProfile");
        group.MapDelete("/users/{id:guid}", DeleteAsync).WithTags("Users").WithName("DeleteUser");
        group.MapPost("/users/{id:guid}/role", SetRoleAsync).WithTags("Users").WithName("SetUserRole");
        group.MapPost("/users/{id:guid}/activate", ActivateAsync).WithTags("Users").WithName("ActivateUser");
        group.MapPost("/users/{id:guid}/deactivate", DeactivateAsync).WithTags("Users").WithName("DeactivateUser");
        group.MapPost("/users/{id:guid}/password", SetPasswordAsync).WithTags("Users").WithName("SetUserPassword");
        group.MapGet("/roles", ListRolesAsync).WithTags("Users").WithName("ListRoles");

        return group;
    }

    private static async Task<Ok<PagedResult<UserDto>>> ListAsync(
        CatalogDbContext catalog, int? page, int? pageSize, string? username, string? role, string? provider,
        bool? active, CancellationToken ct)
    {
        var (p, size) = PageRequest.Normalize(page, pageSize);
        var (items, total) = await UserStore.ListAsync(catalog, p, size, username, role, provider, active, ct)
            .ConfigureAwait(false);
        return TypedResults.Ok(new PagedResult<UserDto>(items.Select(ToDto).ToList(), p, size, total));
    }

    private static async Task<Results<Ok<UserDto>, ProblemHttpResult>> GetAsync(
        Guid id, CatalogDbContext catalog, CancellationToken ct)
    {
        var user = await UserStore.FindByIdAsync(catalog, id, ct).ConfigureAwait(false);
        return user is null
            ? NotFound($"No user with id '{id}'.")
            : TypedResults.Ok(ToDto(user));
    }

    private static async Task<Ok<IReadOnlyList<RoleDto>>> ListRolesAsync(CatalogDbContext catalog, CancellationToken ct)
    {
        var roles = await UserStore.ListRolesAsync(catalog, ct).ConfigureAwait(false);
        IReadOnlyList<RoleDto> dto = roles.Select(r => new RoleDto(r.Name, r.Scopes, r.Description)).ToList();
        return TypedResults.Ok(dto);
    }

    private static async Task<Results<Created<UserDto>, ProblemHttpResult>> CreateAsync(
        CreateUserRequest request, CatalogDbContext catalog, IPasswordHasher<CatalogUser> hasher,
        TimeProvider clock, CancellationToken ct)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Role))
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest,
                title: "Username and role are required");
        }

        if (request.Password is null || request.Password.Length < MinPasswordLength)
        {
            return PasswordTooShort();
        }

        if (TooLong(request.Username, request.Email, request.DisplayName) is { } tooLong)
        {
            return tooLong;
        }

        var nowUtc = clock.GetUtcNow().UtcDateTime;
        var hash = hasher.HashPassword(new CatalogUser { Username = request.Username.Trim() }, request.Password);
        var (status, id) = await UserStore.CreateLocalAsync(
            catalog, request.Username, hash, request.Role, request.Email, request.DisplayName, nowUtc, ct)
            .ConfigureAwait(false);
        switch (status)
        {
            case UserCreateStatus.UnknownRole:
                return UnknownRole(request.Role);
            case UserCreateStatus.UsernameTaken:
                return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict,
                    title: "Username already in use",
                    detail: $"A user named '{request.Username.Trim()}' already exists.");
            default:
                var created = await UserStore.FindByIdAsync(catalog, id, ct).ConfigureAwait(false);
                return TypedResults.Created($"/api/v1/users/{id}", ToDto(created!));
        }
    }

    /// <summary>Renames a user and rewrites their display name and email. Blank optional fields clear the stored
    /// value; the sign-in name is required and, for an SSO account, immutable here (Entra owns it).</summary>
    private static async Task<Results<Ok<UserDto>, ProblemHttpResult>> SetProfileAsync(
        Guid id, UpdateUserProfileRequest request, CatalogDbContext catalog, TimeProvider clock,
        CancellationToken ct)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Username))
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest, title: "A username is required");
        }

        if (TooLong(request.Username, request.Email, request.DisplayName) is { } tooLong)
        {
            return tooLong;
        }

        var result = await UserStore.UpdateProfileAsync(
            catalog, id, request.Username, request.Email, request.DisplayName, clock.GetUtcNow().UtcDateTime, ct)
            .ConfigureAwait(false);
        return result switch
        {
            UserMutation.NotFound => NotFound($"No user with id '{id}'."),
            UserMutation.UsernameTaken => TypedResults.Problem(statusCode: StatusCodes.Status409Conflict,
                title: "Username already in use",
                detail: $"A user named '{request.Username.Trim()}' already exists."),
            UserMutation.NotLocal => TypedResults.Problem(statusCode: StatusCodes.Status409Conflict,
                title: "Not a local user",
                detail: "This account signs in through single sign-on; its sign-in name is the Microsoft Entra ID account name and is refreshed at every sign-in. Its display name and email can still be edited here."),
            _ => await CurrentDtoAsync(catalog, id, ct).ConfigureAwait(false),
        };
    }

    /// <summary>Deletes a user permanently, together with their private rows. Refused for the caller's own account
    /// and for the last active admin; deactivation is the reversible alternative.</summary>
    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteAsync(
        Guid id, CatalogDbContext catalog, HttpContext httpContext, CancellationToken ct)
    {
        if (Guid.TryParse(httpContext.User.FindFirst("uid")?.Value, out var callerId) && callerId == id)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict,
                title: "That is your own account",
                detail: "An admin cannot delete the account they are signed in as; sign in as another admin to remove it.");
        }

        var result = await UserStore.DeleteAsync(catalog, id, ct).ConfigureAwait(false);
        return result switch
        {
            UserMutation.NotFound => NotFound($"No user with id '{id}'."),
            UserMutation.LastAdmin => LastAdmin("deleted"),
            _ => TypedResults.NoContent(),
        };
    }

    private static async Task<Results<Ok<UserDto>, ProblemHttpResult>> SetRoleAsync(
        Guid id, SetRoleRequest request, CatalogDbContext catalog, TimeProvider clock, CancellationToken ct)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Role))
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest, title: "A role is required");
        }

        var result = await UserStore.SetRoleAsync(catalog, id, request.Role, clock.GetUtcNow().UtcDateTime, ct)
            .ConfigureAwait(false);
        return result switch
        {
            UserMutation.NotFound => NotFound($"No user with id '{id}'."),
            UserMutation.UnknownRole => UnknownRole(request.Role),
            UserMutation.LastAdmin => LastAdmin("demoted"),
            _ => await CurrentDtoAsync(catalog, id, ct).ConfigureAwait(false),
        };
    }

    private static async Task<Results<Ok<UserDto>, ProblemHttpResult>> ActivateAsync(
        Guid id, CatalogDbContext catalog, TimeProvider clock, CancellationToken ct)
    {
        var result = await UserStore.SetActiveAsync(catalog, id, active: true, clock.GetUtcNow().UtcDateTime, ct)
            .ConfigureAwait(false);
        return result switch
        {
            UserMutation.NotFound => NotFound($"No user with id '{id}'."),
            _ => await CurrentDtoAsync(catalog, id, ct).ConfigureAwait(false),
        };
    }

    private static async Task<Results<Ok<UserDto>, ProblemHttpResult>> DeactivateAsync(
        Guid id, CatalogDbContext catalog, TimeProvider clock, CancellationToken ct)
    {
        var result = await UserStore.SetActiveAsync(catalog, id, active: false, clock.GetUtcNow().UtcDateTime, ct)
            .ConfigureAwait(false);
        return result switch
        {
            UserMutation.NotFound => NotFound($"No user with id '{id}'."),
            UserMutation.LastAdmin => LastAdmin("deactivated"),
            _ => await CurrentDtoAsync(catalog, id, ct).ConfigureAwait(false),
        };
    }

    private static async Task<Results<Ok<UserDto>, ProblemHttpResult>> SetPasswordAsync(
        Guid id, SetPasswordRequest request, CatalogDbContext catalog, IPasswordHasher<CatalogUser> hasher,
        TimeProvider clock, CancellationToken ct)
    {
        if (request?.Password is null || request.Password.Length < MinPasswordLength)
        {
            return PasswordTooShort();
        }

        var user = await UserStore.FindByIdAsync(catalog, id, ct).ConfigureAwait(false);
        if (user is null)
        {
            return NotFound($"No user with id '{id}'.");
        }

        var hash = hasher.HashPassword(user, request.Password);
        var result = await UserStore.SetPasswordHashAsync(catalog, id, hash, clock.GetUtcNow().UtcDateTime, ct)
            .ConfigureAwait(false);
        return result switch
        {
            UserMutation.NotFound => NotFound($"No user with id '{id}'."),
            UserMutation.NotLocal => TypedResults.Problem(statusCode: StatusCodes.Status409Conflict,
                title: "Not a local user",
                detail: "This account signs in through single sign-on; its credential is managed in Microsoft Entra ID."),
            _ => await CurrentDtoAsync(catalog, id, ct).ConfigureAwait(false),
        };
    }

    private static async Task<Results<Ok<UserDto>, ProblemHttpResult>> CurrentDtoAsync(
        CatalogDbContext catalog, Guid id, CancellationToken ct)
    {
        var user = await UserStore.FindByIdAsync(catalog, id, ct).ConfigureAwait(false);
        return user is null
            ? NotFound($"No user with id '{id}'.")
            : TypedResults.Ok(ToDto(user));
    }

    /// <summary>The 400 for an over-long profile field, or null when every field fits its column.</summary>
    private static ProblemHttpResult? TooLong(string? username, string? email, string? displayName)
    {
        if (username?.Trim().Length > MaxUsernameLength)
        {
            return FieldTooLong("username", MaxUsernameLength);
        }

        if (email?.Trim().Length > MaxEmailLength)
        {
            return FieldTooLong("email", MaxEmailLength);
        }

        return displayName?.Trim().Length > MaxDisplayNameLength
            ? FieldTooLong("display name", MaxDisplayNameLength)
            : null;
    }

    private static ProblemHttpResult FieldTooLong(string field, int max)
        => TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Field too long",
            detail: $"The {field} must be at most {max} characters.");

    private static ProblemHttpResult NotFound(string detail)
        => TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "Not found", detail: detail);

    private static ProblemHttpResult UnknownRole(string role)
        => TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Unknown role",
            detail: $"The role '{role.Trim()}' does not exist.");

    private static ProblemHttpResult LastAdmin(string action)
        => TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "Last active admin",
            detail: $"This user is the only active admin and cannot be {action}; grant another user the admin role first.");

    private static ProblemHttpResult PasswordTooShort()
        => TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Password too short",
            detail: $"Passwords must be at least {MinPasswordLength} characters.");

    private static UserDto ToDto(CatalogUser user) => new(
        user.Id, user.Username, user.Email, user.DisplayName, user.Role, user.Provider, user.Active,
        user.CreatedUtc, user.UpdatedUtc, user.LastLoginUtc);
}
