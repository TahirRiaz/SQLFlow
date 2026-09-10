using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using SqlFlow.Core;
using SqlFlow.Core.Acquire;
using SqlFlow.Core.Secrets;

namespace SqlFlow.Acquire.Runtime;

/// <summary>
/// Turns an <see cref="AcquireAuth"/> plus its resolved secrets into the concrete <see cref="AppliedAuth"/> a
/// transport merges onto each request. Static schemes (api key, bearer, basic) resolve their <c>${...}</c> secret
/// references directly; the OAuth / token-exchange schemes perform the token request (optionally after OIDC
/// discovery) and read the access token out of the response by path. Secret material never leaves this class except
/// as the final header/query value.
/// </summary>
public sealed class AuthResolver
{
    private readonly ISecretResolver _secrets;

    public AuthResolver(ISecretResolver secrets)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        _secrets = secrets;
    }

    public async Task<AppliedAuth> ResolveAsync(AcquireAuth auth, HttpExecutor authHttp, TemplateContext vars, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(auth);
        ArgumentNullException.ThrowIfNull(authHttp);
        ArgumentNullException.ThrowIfNull(vars);

        switch (auth.Type)
        {
            case AcquireAuthType.None:
                return AppliedAuth.None;

            case AcquireAuthType.ApiKeyHeader:
            {
                var header = Require(auth.HeaderName, "auth.headerName", "api_key_header");
                var value = await Secret(auth.SecretRef, "auth.secretRef", "api_key_header", ct).ConfigureAwait(false);
                return AppliedAuth.Header(header, (auth.ValuePrefix ?? string.Empty) + value);
            }

            case AcquireAuthType.ApiKeyQuery:
            {
                var param = Require(auth.ParamName, "auth.paramName", "api_key_query");
                var value = await Secret(auth.SecretRef, "auth.secretRef", "api_key_query", ct).ConfigureAwait(false);
                return AppliedAuth.QueryParam(param, value);
            }

            case AcquireAuthType.Bearer:
            {
                var token = await Secret(auth.SecretRef, "auth.secretRef", "bearer", ct).ConfigureAwait(false);
                return AppliedAuth.Header("Authorization", (auth.ValuePrefix ?? "Bearer ") + token);
            }

            case AcquireAuthType.Basic:
            {
                var user = await Secret(auth.SecondarySecretRef, "auth.secondarySecretRef (username)", "basic", ct).ConfigureAwait(false);
                var password = await Secret(auth.SecretRef, "auth.secretRef (password)", "basic", ct).ConfigureAwait(false);
                return AppliedAuth.Header("Authorization", AppliedAuth.BasicHeader(user, password));
            }

            case AcquireAuthType.OAuth2ClientCredentials:
            case AcquireAuthType.OAuth2RefreshToken:
            case AcquireAuthType.TokenExchange:
            {
                var token = auth.Token ?? throw new SqlFlowException($"Auth type '{auth.Type}' requires a 'token' block with the token endpoint.");
                var accessToken = await AcquireTokenAsync(token, authHttp, vars, ct).ConfigureAwait(false);
                var headerName = string.IsNullOrWhiteSpace(token.ApplyHeaderName) ? "Authorization" : token.ApplyHeaderName!;
                return AppliedAuth.Header(headerName, token.ApplyPrefix + accessToken);
            }

            default:
                throw new SqlFlowException($"Unsupported auth type '{auth.Type}'.");
        }
    }

    private async Task<string> AcquireTokenAsync(AcquireTokenEndpoint token, HttpExecutor authHttp, TemplateContext vars, CancellationToken ct)
    {
        var tokenUrl = await ResolveTokenUrlAsync(token, authHttp, ct).ConfigureAwait(false);

        // Resolve the request body fields (secrets + templates), lifting the client id/secret into a Basic header
        // when the endpoint authenticates the client that way rather than in the body.
        var body = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in token.Body)
        {
            body[key] = await ResolveAsync(value, vars, ct).ConfigureAwait(false);
        }

        string? basicHeader = null;
        if (token.BasicAuthClient)
        {
            var clientId = Take(body, "client_id") ?? throw new SqlFlowException("basicAuthClient requires 'client_id' in the token body.");
            var clientSecret = Take(body, "client_secret") ?? throw new SqlFlowException("basicAuthClient requires 'client_secret' in the token body.");
            basicHeader = AppliedAuth.BasicHeader(clientId, clientSecret);
        }

        var rawBody = token.RawBody is null ? null : await ResolveAsync(token.RawBody, vars, ct).ConfigureAwait(false);

        var result = await authHttp.SendAsync(() => BuildTokenRequest(token, tokenUrl, body, rawBody, basicHeader), allowStatuses: null, ct: ct).ConfigureAwait(false);

        JsonElement root;
        try
        {
            root = JsonDocument.Parse(result.Body).RootElement;
        }
        catch (JsonException ex)
        {
            throw new SqlFlowException($"Token endpoint '{tokenUrl}' did not return JSON: {ex.Message}", ex);
        }

        return JsonPathReader.SelectValue(root, token.TokenPath)
               ?? throw new SqlFlowException($"Token endpoint '{tokenUrl}' response has no value at path '{token.TokenPath}'.");
    }

    private async Task<string> ResolveTokenUrlAsync(AcquireTokenEndpoint token, HttpExecutor authHttp, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(token.DiscoveryUrl))
        {
            var discoveryUrl = await _secrets.ResolveAsync(token.DiscoveryUrl!, ct).ConfigureAwait(false);
            var result = await authHttp.SendAsync(() => new HttpRequestMessage(HttpMethod.Get, discoveryUrl), allowStatuses: null, ct: ct).ConfigureAwait(false);
            JsonElement root;
            try
            {
                root = JsonDocument.Parse(result.Body).RootElement;
            }
            catch (JsonException ex)
            {
                throw new SqlFlowException($"OIDC discovery document '{discoveryUrl}' is not valid JSON: {ex.Message}", ex);
            }

            return JsonPathReader.SelectValue(root, "token_endpoint")
                   ?? throw new SqlFlowException($"OIDC discovery document '{discoveryUrl}' has no 'token_endpoint'.");
        }

        var url = token.Url ?? throw new SqlFlowException("The token block needs a 'url' (or a 'discoveryUrl' to resolve it).");
        return await _secrets.ResolveAsync(url, ct).ConfigureAwait(false);
    }

    private static HttpRequestMessage BuildTokenRequest(
        AcquireTokenEndpoint token, string tokenUrl, IReadOnlyDictionary<string, string> body, string? rawBody, string? basicHeader)
    {
        var request = new HttpRequestMessage(new HttpMethod(token.Method.ToUpperInvariant()), tokenUrl);

        if (basicHeader is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", basicHeader);
        }

        foreach (var (name, value) in token.Headers)
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        if (rawBody is not null)
        {
            var mediaType = token.BodyKind == AcquireBodyKind.Json ? "application/json" : "application/x-www-form-urlencoded";
            request.Content = new StringContent(rawBody, Encoding.UTF8, mediaType);
        }
        else if (token.BodyKind == AcquireBodyKind.Json)
        {
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        }
        else
        {
            request.Content = new FormUrlEncodedContent(body);
        }

        return request;
    }

    private async Task<string> Secret(string? reference, string field, string authType, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            throw new SqlFlowException($"Auth type '{authType}' requires '{field}'.");
        }

        return await _secrets.ResolveAsync(reference, ct).ConfigureAwait(false);
    }

    private async Task<string> ResolveAsync(string value, TemplateContext vars, CancellationToken ct)
    {
        var templated = TemplateEngine.Render(value, vars);
        return await _secrets.ResolveAsync(templated, ct).ConfigureAwait(false);
    }

    private static string Require(string? value, string field, string authType)
        => string.IsNullOrWhiteSpace(value) ? throw new SqlFlowException($"Auth type '{authType}' requires '{field}'.") : value;

    private static string? Take(IDictionary<string, string> body, string key)
    {
        if (body.TryGetValue(key, out var value))
        {
            body.Remove(key);
            return value;
        }

        return null;
    }
}
