using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Acquire.Engine;
using SqlFlow.Catalog;
using SqlFlow.Core;
using SqlFlow.Core.Runs;
using SqlFlow.Yaml;

namespace SqlFlow.ControlPlane.Api;

/// <summary>One acquisition integration (<c>flowType: acq</c>) in the estate, for the Integrations list page. The
/// id is the catalog pipeline id, so the GUI reads the stored (secret-redacted) YAML through the pipeline detail.</summary>
public sealed record IntegrationDto(Guid Id, string Name, string? Batch, string RelativePath, bool Active);

/// <summary>A debugger Test request: the integration YAML to run, how many pages to cap the safe preview at, and
/// runtime values for the flow's declared <c>params:</c> (overriding their defaults for this test).</summary>
public sealed record IntegrationDebugRequest(string Yaml, int? MaxPages, Dictionary<string, string>? Params);

/// <summary>A describe request: the integration YAML to introspect through the real loader.</summary>
public sealed record IntegrationDescribeRequest(string Yaml);

/// <summary>One declared runtime parameter, for the dynamically generated parameter form.</summary>
public sealed record IntegrationParamDto(string Name, string? Default, bool Required);

/// <summary>What the loader sees in an integration document: identity, transport, and the declared parameters.
/// The GUI renders its parameter form from this, so the form always matches the engine's own parsing.</summary>
public sealed record IntegrationDescribeResponse(
    string Name, string? Batch, string Transport, bool HasDateWindow, IReadOnlyList<IntegrationParamDto> Params);

/// <summary>One captured request/response for the debugger's inspector.</summary>
public sealed record IntegrationDebugPageDto(
    int Iteration, int Page, string Method, string Url, IReadOnlyDictionary<string, string> RequestHeaders,
    int Status, IReadOnlyDictionary<string, string> ResponseHeaders, string? ContentType, long Bytes,
    int RecordCount, double DurationMs, string BodyPreview, string? WouldLandTo);

/// <summary>The debugger Test result: the run summary plus the per-page capture. Nothing was written to the lake.</summary>
public sealed record IntegrationDebugResponse(
    bool Success, string? Error, int Iterations, int PagesFetched, int FilesWritten, int Skipped,
    long BytesWritten, string? LandedBase, string? WatermarkBefore, string? WatermarkAfter,
    IReadOnlyList<IntegrationDebugPageDto> Pages);

/// <summary>
/// The Integrations surface: the read side lists the estate's <c>flowType: api</c> flows (HTTP APIs, SFTP drops,
/// storage tables), and the operate side runs a safe Test invoke - a dry run that fetches (honoring
/// auth, pagination, and iteration) but lands nothing, capturing every request/response for the debugger. The
/// production Invoke goes through the normal run-trigger path, so there is one execution pathway; this endpoint is
/// only the non-writing preview.
/// </summary>
public static class IntegrationEndpoints
{
    private const int DefaultDebugPageCap = 3;
    private const int MaxDebugPageCap = 20;

    public static RouteGroupBuilder MapIntegrationReadEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);
        group.MapGet("/integrations", ListAsync).WithTags("Integrations").WithName("ListIntegrations");
        return group;
    }

    public static RouteGroupBuilder MapIntegrationDebugEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);
        group.MapPost("/integrations/debug", DebugAsync).WithTags("Integrations").WithName("DebugIntegration");
        group.MapPost("/integrations/describe", Describe).WithTags("Integrations").WithName("DescribeIntegration");
        return group;
    }

    /// <summary>Parses an integration document through the real loader and returns its declared parameters, so the
    /// GUI can render a parameter form that always matches the engine's parsing. No network I/O.</summary>
    private static Results<Ok<IntegrationDescribeResponse>, BadRequest<string>> Describe(
        IntegrationDescribeRequest request, YamlAcquireFlowLoader loader)
    {
        if (string.IsNullOrWhiteSpace(request.Yaml))
        {
            return TypedResults.BadRequest("The request body must include the integration 'yaml'.");
        }

        Core.Acquire.AcquireFlow flow;
        try
        {
            flow = loader.Parse(request.Yaml, "<describe>");
        }
        catch (FlowValidationException ex)
        {
            return TypedResults.BadRequest(ex.Message);
        }

        var parameters = flow.Params
            .OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => new IntegrationParamDto(p.Key, p.Value, p.Value is null))
            .ToList();

        return TypedResults.Ok(new IntegrationDescribeResponse(
            flow.Name,
            flow.Batch,
            flow.Source.Transport.ToString().ToLowerInvariant(),
            flow.Items.Any(item => item.Source.Iterations.Any(i => i.Kind == Core.Acquire.AcquireIterationKind.DateWindow)),
            parameters));
    }

    private static async Task<Ok<IReadOnlyList<IntegrationDto>>> ListAsync(CatalogDbContext db, CancellationToken ct)
    {
        var flows = await db.Pipelines
            .Where(p => p.Kind == "api")
            .OrderBy(p => p.Name)
            .Select(p => new IntegrationDto(p.Id, p.Name, p.Batch, p.RelativePath, p.Active))
            .ToListAsync(ct).ConfigureAwait(false);

        return TypedResults.Ok<IReadOnlyList<IntegrationDto>>(flows);
    }

    private static async Task<Results<Ok<IntegrationDebugResponse>, BadRequest<string>>> DebugAsync(
        IntegrationDebugRequest request, YamlAcquireFlowLoader loader, AcquireEngine engine, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Yaml))
        {
            return TypedResults.BadRequest("The request body must include the integration 'yaml'.");
        }

        Core.Acquire.AcquireFlow flow;
        try
        {
            flow = loader.Parse(request.Yaml, "<debug>");
        }
        catch (FlowValidationException ex)
        {
            return TypedResults.BadRequest(ex.Message);
        }

        var cap = Math.Clamp(request.MaxPages ?? DefaultDebugPageCap, 1, MaxDebugPageCap);
        var probe = new CollectingProbe();
        AcquireRunResult result;
        try
        {
            result = await engine.RunAsync(
                flow, Guid.NewGuid(), NullRunEventSink.Instance, priorWatermark: null, ct,
                new AcquireRunOverrides
                {
                    DryRun = true,
                    Probe = probe,
                    MaxPagesOverride = cap,
                    Params = request.Params is { Count: > 0 } ? request.Params : null,
                }).ConfigureAwait(false);
        }
        catch (SqlFlowException ex)
        {
            return TypedResults.BadRequest(ex.Message);
        }

        var pages = probe.Pages.Select(p => new IntegrationDebugPageDto(
            p.Iteration, p.Page, p.Method, p.Url, p.RequestHeaders, p.Status, p.ResponseHeaders,
            p.ContentType, p.Bytes, p.RecordCount, p.DurationMs, p.BodyPreview, p.LandedTo)).ToList();

        return TypedResults.Ok(new IntegrationDebugResponse(
            result.Success, result.Error, result.Iterations, result.PagesFetched, result.FilesWritten,
            result.Skipped, result.BytesWritten, result.LandedBase, result.WatermarkBefore, result.WatermarkAfter, pages));
    }
}
