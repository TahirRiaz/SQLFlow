using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.Core;
using SqlFlow.Core.Runs;
using SqlFlow.Core.Secrets;
using SqlFlow.Yaml;

namespace SqlFlow.ControlPlane.Api;

/// <summary>
/// Looks up the values a parameter of a registered kind's flow may take, when its kind says it looks them up
/// (<see cref="DeclaredRunParameter.Lookup"/>): the values a column of the flow's source holds, say. A host module registers
/// one for the kinds it owns; the control plane hands it the flow's catalog row and what the client asks, and serves its
/// answer to the run form, so an operator picks a value instead of typing it.
/// </summary>
public interface IRunParameterLookup
{
    /// <summary>True for the flow kinds (<c>flowType</c>) whose parameters this looks up.</summary>
    bool Serves(string flowKind);

    /// <summary>
    /// The values parameter <see cref="RunParameterLookupRequest.Parameter"/> of the flow may take, the ones matching
    /// <see cref="RunParameterLookupRequest.Search"/> when it is given. A failure is an exception whose message names
    /// what could not be read; the control plane answers it as a problem, never as an empty list.
    /// </summary>
    Task<RunParameterValues> LookUpAsync(RunParameterLookupRequest request, CancellationToken ct);
}

/// <summary>What a run form asks a lookup for: the flow, the parameter, the text typed so far, and the run values it holds.</summary>
/// <param name="PipelineId">The flow's pipeline.</param>
/// <param name="RepoId">The repository the flow belongs to.</param>
/// <param name="FlowName">The flow's name.</param>
/// <param name="FlowKind">The flow's kind.</param>
/// <param name="RelativePath">The flow's document, relative to its repository.</param>
/// <param name="Yaml">The flow's document as the catalog holds it, secrets redacted.</param>
/// <param name="Parameter">The parameter whose values are looked up.</param>
/// <param name="Search">Text the values contain, compared ignoring case; null for every value.</param>
/// <param name="Values">The run values the form holds (the partition a run acts in, say), for a lookup that depends on them.</param>
/// <param name="Actor">Who asked, as a run's requester is recorded (<see cref="RequestActor.Label"/>), for the lookup's log.</param>
public sealed record RunParameterLookupRequest(
    Guid PipelineId, Guid RepoId, string FlowName, string FlowKind, string RelativePath, string Yaml, string Parameter, string? Search,
    IReadOnlyDictionary<string, string> Values, string Actor);

/// <summary>The values a parameter may take, as a lookup answers.</summary>
/// <param name="Parameter">The parameter.</param>
/// <param name="Source">Where the values were read, in words ("column region of sales.dbo.Customers").</param>
/// <param name="Values">The values, the most used first when the lookup knows how often each is used, then by value.</param>
/// <param name="More">True when the source holds more matching values than were listed.</param>
public sealed record RunParameterValues(string Parameter, string Source, IReadOnlyList<RunParameterValue> Values, bool More);

/// <summary>One value a parameter may take, and how many rows or records hold it when the lookup counts them.</summary>
public sealed record RunParameterValue(string Value, long? Count);

/// <summary>The body of a request for a parameter's values: the text typed so far, and the run values the form holds.</summary>
public sealed record RunParameterValuesRequestDto(
    string? Search,
    [property: JsonConverter(typeof(RunValuesJsonConverter))] IReadOnlyDictionary<string, string>? Values);

/// <summary>
/// What a pipeline's run may be given, read from its document as the catalog holds it, by its registered kind
/// (<see cref="IFlowDocumentKind.DescribeRun"/>): one reading for the run form, the lookups and a schedule's preview.
/// </summary>
public static class PipelineRunInputs
{
    /// <summary>
    /// The pipeline's run inputs and, when its document no longer reads as its kind does (the kind changed since the
    /// sync), why: the inputs are then <see cref="FlowRunInputs.None"/> and the problem names the document. A built-in kind
    /// describes none and has no problem.
    /// </summary>
    public static (FlowRunInputs Inputs, string? Problem) Describe(YamlDocumentLoader documents, string flowKind, string yaml, string relativePath)
    {
        ArgumentNullException.ThrowIfNull(documents);
        var kind = documents.FindKind(flowKind);
        if (kind is null)
        {
            return (FlowRunInputs.None, null);
        }

        try
        {
            return (kind.DescribeRun(yaml, relativePath), null);
        }
        catch (SqlFlowException ex)
        {
            return (FlowRunInputs.None, SecretHygiene.RedactedMessage(ex.Message));
        }
    }
}

/// <summary>What a run of a flow would take for each parameter, and everything that would refuse it.</summary>
/// <param name="Inputs">What the flow's document declares a run may be given.</param>
/// <param name="Values">What the run takes for each declared parameter, then the kind's own values and any undeclared one given.</param>
/// <param name="Problems">What would refuse the run, each naming the parameter or argument; empty when it would start.</param>
public sealed record RunExplained(FlowRunInputs Inputs, IReadOnlyList<ResolvedRunValue> Values, IReadOnlyList<string> Problems);

/// <summary>
/// The one explanation of a run's inputs, for every place that shows one before a run starts (a run form as an operator
/// fills it in, a schedule's preview of each member): the flow's document read by its kind, the values resolved by the one
/// rule (<see cref="RunValueResolution"/>), and the check every trust boundary applies to a run's arguments
/// (<see cref="YamlDocumentLoader.ValidateRunParameters"/>), so what is shown is what a trigger or a fire meets. A run of an
/// operation that does not resolve the parameters (<see cref="FlowRunInputs.ParameterOperations"/>) takes the values it is
/// given as they are, so they are listed as given and refuse nothing.
/// </summary>
public static class RunExplanation
{
    public static RunExplained Explain(
        YamlDocumentLoader documents, string flowKind, string yaml, string relativePath, string? operation,
        IReadOnlyDictionary<string, string> values, string? payload)
    {
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(values);
        var (inputs, problem) = PipelineRunInputs.Describe(documents, flowKind, yaml, relativePath);
        var performed = operation ?? (documents.FindKind(flowKind) is { Operations.Count: > 0 } kind ? kind.Operations[0].Name : null);
        var resolved = inputs.TakesParameters(performed)
            ? RunValueResolution.Explain(inputs, values)
            : values.OrderBy(v => v.Key, StringComparer.Ordinal)
                .Select(v => new ResolvedRunValue(
                    v.Key, v.Value,
                    inputs.KindValues.Any(k => string.Equals(k.Name, v.Key, StringComparison.Ordinal)) ? RunValueOrigin.Kind : RunValueOrigin.Given,
                    null))
                .ToList();
        var problems = new List<string>();
        if (problem is not null)
        {
            problems.Add(problem);
        }

        problems.AddRange(resolved.Where(v => v.Problem is not null).Select(v => v.Problem!));
        try
        {
            documents.ValidateRunParameters(flowKind, RunParameters.None with { Operation = operation, Values = values, Payload = payload });
        }
        catch (SqlFlowException ex)
        {
            problems.Add(SecretHygiene.RedactedMessage(ex.Message));
        }

        return new RunExplained(inputs, resolved, problems.Distinct(StringComparer.Ordinal).ToList());
    }
}

/// <summary>The body of a request to explain a run's inputs: the operation, the values and the payload a run form holds.</summary>
public sealed record RunExplainRequestDto(
    string? Operation,
    [property: JsonConverter(typeof(RunValuesJsonConverter))] IReadOnlyDictionary<string, string>? Values,
    System.Text.Json.JsonElement? Payload);

/// <summary>What a run of the pipeline with the asked inputs would take for each parameter, and what would refuse it.</summary>
public sealed record RunExplainedDto(IReadOnlyList<ResolvedRunValue> Values, IReadOnlyList<string> Problems);

/// <summary>
/// The run-input routes of a run form, under the operate scope: a parameter's values to pick from (which read the flow's own
/// data, as a run does), and what a run with the form's inputs would take and what would refuse it.
/// </summary>
public static class RunInputEndpoints
{
    public static RouteGroupBuilder MapRunInputOperateEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);
        var pipelines = group.MapGroup("/pipelines").WithTags("Pipelines");
        pipelines.MapPost("/{id:guid}/parameters/{name}/values", LookUpParameterValuesAsync).WithName("LookUpPipelineParameterValues");
        pipelines.MapPost("/{id:guid}/parameters/explain", ExplainRunInputsAsync).WithName("ExplainPipelineRunInputs");
        return group;
    }

    /// <summary>
    /// What a run of the pipeline given the body's operation, values and payload would take for each parameter, and what
    /// would refuse it (<see cref="RunExplanation"/>): the check a run form shows as the operator fills it in. Nothing is
    /// queued. 404 for an unknown pipeline.
    /// </summary>
    private static async Task<Results<Ok<RunExplainedDto>, ProblemHttpResult>> ExplainRunInputsAsync(
        Guid id, RunExplainRequestDto? body, CatalogDbContext db, YamlDocumentLoader documents, CancellationToken ct)
    {
        var row = await db.Pipelines.AsNoTracking().Where(p => p.Id == id)
            .Select(p => new { p.Kind, p.RelativePath, p.Yaml })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (row is null)
        {
            return Problem(StatusCodes.Status404NotFound, "Not found", $"No pipeline '{id}'.");
        }

        var payload = body?.Payload is { ValueKind: not (System.Text.Json.JsonValueKind.Null or System.Text.Json.JsonValueKind.Undefined) } given
            ? given.GetRawText()
            : null;
        var explained = RunExplanation.Explain(
            documents, row.Kind, row.Yaml, row.RelativePath, string.IsNullOrWhiteSpace(body?.Operation) ? null : body.Operation.Trim(),
            body?.Values ?? new Dictionary<string, string>(StringComparer.Ordinal), payload);
        return TypedResults.Ok(new RunExplainedDto(explained.Values, explained.Problems));
    }

    /// <summary>
    /// The values a parameter of the pipeline may take: its document's <c>choices</c> when it lists them, else what the
    /// kind's lookup reads. 404 for an unknown pipeline or a parameter its document does not declare; 422 when the
    /// parameter neither lists choices nor is looked up, or the document no longer reads; a lookup's failure is a 502
    /// naming what could not be read.
    /// </summary>
    private static async Task<Results<Ok<RunParameterValues>, ProblemHttpResult>> LookUpParameterValuesAsync(
        Guid id, string name, RunParameterValuesRequestDto? body, CatalogDbContext db, YamlDocumentLoader documents,
        [Microsoft.AspNetCore.Mvc.FromServices] IEnumerable<IRunParameterLookup> lookups, System.Security.Claims.ClaimsPrincipal user, CancellationToken ct)
    {
        var row = await db.Pipelines.AsNoTracking().Where(p => p.Id == id)
            .Select(p => new { p.Id, p.RepoId, p.Name, p.Kind, p.RelativePath, p.Yaml })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (row is null)
        {
            return Problem(StatusCodes.Status404NotFound, "Not found", $"No pipeline '{id}'.");
        }

        var (inputs, problem) = PipelineRunInputs.Describe(documents, row.Kind, row.Yaml, row.RelativePath);
        if (problem is not null)
        {
            return Problem(StatusCodes.Status422UnprocessableEntity, "Document does not read", problem);
        }

        var parameter = inputs.Parameter(name);
        if (parameter is null)
        {
            return Problem(StatusCodes.Status404NotFound, "Not found", $"Flow '{row.Name}' declares no parameter '{name}'.");
        }

        var search = string.IsNullOrWhiteSpace(body?.Search) ? null : body.Search.Trim();
        if (parameter.Choices.Count > 0)
        {
            var listed = parameter.Choices
                .Where(choice => search is null || choice.Contains(search, StringComparison.OrdinalIgnoreCase))
                .Select(choice => new RunParameterValue(choice, null))
                .ToList();
            return TypedResults.Ok(new RunParameterValues(parameter.Name, "the choices the flow's document lists", listed, More: false));
        }

        var lookup = parameter.Lookup is null ? null : lookups.FirstOrDefault(l => l.Serves(row.Kind));
        if (lookup is null)
        {
            return Problem(
                StatusCodes.Status422UnprocessableEntity, "No values to offer",
                $"Parameter '{name}' of flow '{row.Name}' lists no choices and its values are not looked up; a run takes any value for it.");
        }

        RunParameterValues values;
        try
        {
            values = await lookup.LookUpAsync(
                new RunParameterLookupRequest(
                    row.Id, row.RepoId, row.Name, row.Kind, row.RelativePath, row.Yaml, parameter.Name, search,
                    body?.Values ?? new Dictionary<string, string>(StringComparer.Ordinal), RequestActor.Label(user)),
                ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SqlFlowException or System.Data.Common.DbException or TimeoutException)
        {
            // What the source said, redacted, as an operator can act on it; anything else is a defect and reaches the error handler.
            return Problem(
                StatusCodes.Status502BadGateway, "Lookup failed",
                $"The values of parameter '{name}' could not be read: {SecretHygiene.RedactedMessage(ex)}");
        }

        return TypedResults.Ok(values);
    }

    private static ProblemHttpResult Problem(int status, string title, string detail)
        => TypedResults.Problem(detail: detail, statusCode: status, title: title);
}
