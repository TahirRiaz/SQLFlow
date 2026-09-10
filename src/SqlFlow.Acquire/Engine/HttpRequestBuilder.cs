using System.Text;
using SqlFlow.Acquire.Runtime;
using SqlFlow.Core;
using SqlFlow.Core.Acquire;

namespace SqlFlow.Acquire.Engine;

/// <summary>
/// Builds an <see cref="HttpRequestMessage"/> from a declarative <see cref="AcquireRequest"/>: renders the path,
/// query, headers, and body against the iteration variables, merges the applied auth and any pagination query, and
/// encodes the body per its kind (JSON / form / GraphQL / SOAP / raw). Returns a fresh message on each call so it
/// can back a retry factory (a message cannot be sent twice).
/// </summary>
public static class HttpRequestBuilder
{
    public static HttpRequestMessage Build(
        string baseUrl,
        AcquireRequest request,
        TemplateContext vars,
        AppliedAuth auth,
        IReadOnlyDictionary<string, string> extraQuery)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        ArgumentNullException.ThrowIfNull(request);

        var uri = ComposeUri(baseUrl, request, vars, auth, extraQuery);
        var message = new HttpRequestMessage(new HttpMethod(request.Method.ToUpperInvariant()), uri);

        foreach (var (name, value) in request.Headers)
        {
            message.Headers.TryAddWithoutValidation(name, TemplateEngine.Render(value, vars));
        }

        foreach (var (name, value) in auth.Headers)
        {
            message.Headers.TryAddWithoutValidation(name, value);
        }

        message.Content = BuildContent(request, vars);
        return message;
    }

    /// <summary>Builds a message for a fully-formed URL (link-header pagination's next url), carrying the same headers/auth/body.</summary>
    public static HttpRequestMessage BuildForUrl(
        Uri url, AcquireRequest request, TemplateContext vars, AppliedAuth auth)
    {
        var message = new HttpRequestMessage(new HttpMethod(request.Method.ToUpperInvariant()), url);
        foreach (var (name, value) in request.Headers)
        {
            message.Headers.TryAddWithoutValidation(name, TemplateEngine.Render(value, vars));
        }

        foreach (var (name, value) in auth.Headers)
        {
            message.Headers.TryAddWithoutValidation(name, value);
        }

        message.Content = BuildContent(request, vars);
        return message;
    }

    private static Uri ComposeUri(
        string baseUrl, AcquireRequest request, TemplateContext vars, AppliedAuth auth, IReadOnlyDictionary<string, string> extraQuery)
    {
        var renderedPath = TemplateEngine.Render(request.Path, vars);

        // Split any inline query already present on the path.
        var inlineQuery = new Dictionary<string, string>(StringComparer.Ordinal);
        var questionMark = renderedPath.IndexOf('?', StringComparison.Ordinal);
        if (questionMark >= 0)
        {
            ParseQuery(renderedPath[(questionMark + 1)..], inlineQuery);
            renderedPath = renderedPath[..questionMark];
        }

        var baseUri = new Uri(baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/", UriKind.Absolute);
        var combined = new Uri(baseUri, renderedPath.TrimStart('/'));

        // Merge query in a deterministic order: inline path query, declared query (templated), pagination, auth query.
        var query = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (k, v) in inlineQuery)
        {
            query[k] = v;
        }

        foreach (var (k, v) in request.Query)
        {
            query[k] = TemplateEngine.Render(v, vars);
        }

        foreach (var (k, v) in extraQuery)
        {
            query[k] = v;
        }

        foreach (var (k, v) in auth.Query)
        {
            query[k] = v;
        }

        var builder = new UriBuilder(combined);
        if (query.Count > 0)
        {
            builder.Query = string.Join('&', query.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
        }

        return builder.Uri;
    }

    private static HttpContent? BuildContent(AcquireRequest request, TemplateContext vars)
    {
        switch (request.BodyKind)
        {
            case AcquireBodyKind.None:
                return null;

            case AcquireBodyKind.Json:
                return new StringContent(TemplateEngine.Render(request.Body ?? "{}", vars), Encoding.UTF8, "application/json");

            case AcquireBodyKind.Form:
            {
                var fields = request.BodyFields.ToDictionary(kv => kv.Key, kv => TemplateEngine.Render(kv.Value, vars), StringComparer.Ordinal);
                return new FormUrlEncodedContent(fields);
            }

            case AcquireBodyKind.GraphQl:
            {
                var queryText = TemplateEngine.Render(request.Body ?? throw new SqlFlowException("A GraphQL request needs a 'body' (the query)."), vars);
                var payload = System.Text.Json.JsonSerializer.Serialize(new { query = queryText, variables = (object?)null });
                return new StringContent(payload, Encoding.UTF8, "application/json");
            }

            case AcquireBodyKind.Soap:
                return new StringContent(
                    TemplateEngine.Render(request.Body ?? throw new SqlFlowException("A SOAP request needs a 'body' (the envelope)."), vars),
                    Encoding.UTF8, request.ContentType ?? "text/xml");

            case AcquireBodyKind.Raw:
                return new StringContent(
                    TemplateEngine.Render(request.Body ?? string.Empty, vars),
                    Encoding.UTF8, request.ContentType ?? "application/octet-stream");

            default:
                throw new SqlFlowException($"Unsupported body kind '{request.BodyKind}'.");
        }
    }

    private static void ParseQuery(string query, IDictionary<string, string> into)
    {
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=', StringComparison.Ordinal);
            if (eq < 0)
            {
                into[Uri.UnescapeDataString(pair)] = string.Empty;
            }
            else
            {
                into[Uri.UnescapeDataString(pair[..eq])] = Uri.UnescapeDataString(pair[(eq + 1)..]);
            }
        }
    }
}
