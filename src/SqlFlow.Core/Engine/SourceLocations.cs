using SqlFlow.Core.Model;
using SqlFlow.Core.Secrets;

namespace SqlFlow.Core.Engine;

/// <summary>
/// A file flow's source location that names a reference (<c>location: ${env:LANDING_URL}/logtype</c>), resolved when the
/// flow runs or is planned, with the run's own resolver, and so with the values the control plane supplied with the run:
/// a flow reads the storage its deployment configures, and moves from one deployment to another unchanged. A location
/// without a reference is used as written.
/// </summary>
public static class SourceLocations
{
    /// <summary>The source option a file reader takes the location from when <c>location</c> itself is not set.</summary>
    public const string SrcPathOption = "srcPath";

    /// <summary>Whether <paramref name="location"/> names a <c>${scheme:locator}</c> reference anywhere in it.</summary>
    public static bool NamesReference(string? location)
        => !string.IsNullOrEmpty(location) && location.Contains("${", StringComparison.Ordinal);

    /// <summary>
    /// <paramref name="flow"/> with its source location (and the <c>srcPath</c> option, which file readers take the location
    /// from when <c>location</c> is not set) resolved through <paramref name="secrets"/> where it names a reference. A
    /// resolved location must be an absolute path or a URI: nothing anchors it to the flow's own folder, as a written
    /// relative location is.
    /// </summary>
    /// <exception cref="SqlFlowException">A reference does not resolve, or resolves to a relative path; the message names the location as written.</exception>
    public static async Task<FlowDefinition> ResolveAsync(FlowDefinition flow, ISecretResolver secrets, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(secrets);
        var source = flow.Source;
        if (NamesReference(source.Location))
        {
            source = source with { Location = await ResolveOneAsync(source.Location!, "source.location", secrets, ct).ConfigureAwait(false) };
        }

        if (source.Options.TryGetValue(SrcPathOption, out var srcPath) && NamesReference(srcPath))
        {
            var options = new Dictionary<string, string?>(source.Options, StringComparer.OrdinalIgnoreCase)
            {
                [SrcPathOption] = await ResolveOneAsync(srcPath!, $"source.options.{SrcPathOption}", secrets, ct).ConfigureAwait(false),
            };
            source = source with { Options = options };
        }

        return ReferenceEquals(source, flow.Source) ? flow : flow with { Source = source };
    }

    /// <summary>
    /// One location as <paramref name="written"/> under the key <paramref name="field"/>, resolved through
    /// <paramref name="secrets"/> when it names a reference and returned as written when it does not: how a host module
    /// resolves a location of its own documents, by the same rule as a file flow's source location. Text that is not a
    /// reference, such as a <c>{parameter}</c> token, is left for the caller to fill.
    /// </summary>
    /// <exception cref="SqlFlowException">A reference does not resolve, or resolves to a relative path; the message names the location as written.</exception>
    public static async Task<string> ResolveAsync(string written, string field, ISecretResolver secrets, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(written);
        ArgumentException.ThrowIfNullOrWhiteSpace(field);
        ArgumentNullException.ThrowIfNull(secrets);
        return NamesReference(written) ? await ResolveOneAsync(written, field, secrets, ct).ConfigureAwait(false) : written;
    }

    private static async Task<string> ResolveOneAsync(string written, string field, ISecretResolver secrets, CancellationToken ct)
    {
        string resolved;
        try
        {
            resolved = (await secrets.ResolveAsync(written, ct).ConfigureAwait(false)).Trim();
        }
        catch (SqlFlowException ex)
        {
            throw new SqlFlowException($"The {field} '{written}' names a reference that does not resolve: {ex.Message}", ex);
        }

        if (resolved.Length == 0 || (!resolved.Contains("://", StringComparison.Ordinal) && !Path.IsPathRooted(resolved)))
        {
            throw new SqlFlowException(
                $"The {field} '{written}' resolves to a relative or empty location. A location that names a reference must resolve to an absolute path or a URI (abfss://<container>@<account>.dfs.core.windows.net/...), since nothing anchors it to the flow's folder.");
        }

        return resolved;
    }
}
