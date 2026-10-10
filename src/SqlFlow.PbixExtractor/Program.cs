using Microsoft.Extensions.Options;
using SqlFlow.Lineage.Collection;
using SqlFlow.PbixExtractor;

// The isolated Power BI extractor. A .pbix is untrusted input for a memory-unsafe decoder, so it is parsed here, in a
// process that holds nothing worth stealing (no catalog, warehouse, or git credentials) and is meant to run in its own
// container on a network only the control plane can reach. The control plane forwards an upload, and this answers
// with the report's specification, which the control plane validates again before storing it.
var builder = WebApplication.CreateBuilder(args);

var options = builder.Configuration.GetSection(ExtractorOptions.SectionName).Get<ExtractorOptions>() ?? new ExtractorOptions();
options.Validate();
builder.Services.AddOptions<ExtractorOptions>()
    .Bind(builder.Configuration.GetSection(ExtractorOptions.SectionName))
    .Validate(o => { try { o.Validate(); return true; } catch (InvalidOperationException) { return false; } },
        "PbixExtractor configuration is invalid; see the startup validation message for the specific field.")
    .ValidateOnStart();

// The body limit is enforced by the server as well as by the handler, so an oversized upload is cut off at the
// socket rather than read to the end first.
builder.WebHost.ConfigureKestrel(kestrel => kestrel.Limits.MaxRequestBodySize = options.MaxUploadBytes);

builder.Services.AddProblemDetails();
builder.Services.AddSingleton<IReportExtractor, PbixReportExtractor>();
builder.Services.AddSingleton<ExtractionGate>();

var app = builder.Build();

app.UseExceptionHandler();

app.MapGet("/health/live", () => Results.Ok());
app.MapGet("/health/ready", (IReportExtractor extractor) => extractor.IsAvailable
    ? Results.Ok()
    : Results.Problem(
        detail: $"The 'pbix-extract' tool was not found; set {ReportSpecs.ExtractorPathVariable}.",
        statusCode: StatusCodes.Status503ServiceUnavailable, title: "Not ready"));
app.MapPost(ReportExtractionProtocol.ExtractPath, ExtractionEndpoint.HandleAsync);

app.Run();

/// <summary>Exposed so the tests can host the service through WebApplicationFactory.</summary>
public partial class Program;
