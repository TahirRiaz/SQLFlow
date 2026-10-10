using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SqlFlow.Lineage.Collection;
using SqlFlow.Tests;
using Xunit;

// Several tests below change SQLFLOW_PBIX_EXTRACT for the whole process or depend on it, so the assembly runs serially.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace SqlFlow.PbixExtractor.Tests;

/// <summary>
/// The isolated extractor at its HTTP boundary: only a caller holding the key gets anything extracted, an upload is
/// bounded and always removed afterwards, a report the tool refuses is a 422 carrying why, and a burst beyond the
/// configured slots is turned away instead of queued without limit.
/// </summary>
public sealed class ExtractorServiceTests
{
    private const string Key = "extractor-test-key-0123456789-abcdefghijklmnop";

    private static readonly string Spec = """
        subscribers:
          r:
            nodes:
            - id: "r#Sales.pbix#page:1"
              kind: "page"
              displayName: "Page 1"
              ordinal: 1
        """;

    private static WebApplicationFactory<Program> Host(
        IReportExtractor extractor, int maxUploadMegabytes = 1, int maxConcurrent = 2, int queueLimit = 8)
        => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("PbixExtractor:ApiKey", Key);
            builder.UseSetting("PbixExtractor:MaxUploadMegabytes", maxUploadMegabytes.ToString(System.Globalization.CultureInfo.InvariantCulture));
            builder.UseSetting("PbixExtractor:MaxConcurrent", maxConcurrent.ToString(System.Globalization.CultureInfo.InvariantCulture));
            builder.UseSetting("PbixExtractor:QueueLimit", queueLimit.ToString(System.Globalization.CultureInfo.InvariantCulture));
            builder.ConfigureServices(services => services.Replace(ServiceDescriptor.Singleton(extractor)));
        });

    private static async Task<HttpResponseMessage> PostAsync(
        HttpClient client, byte[] body, string? reportFile = "Sales.pbix", string? key = Key)
    {
        var path = reportFile is null
            ? ReportExtractionProtocol.ExtractPath
            : $"{ReportExtractionProtocol.ExtractPath}?reportFile={Uri.EscapeDataString(reportFile)}";
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(path, UriKind.Relative))
        {
            Content = new ByteArrayContent(body),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        if (key is not null)
        {
            request.Headers.Add(ReportExtractionProtocol.KeyHeader, key);
        }

        return await client.SendAsync(request);
    }

    [Fact]
    public async Task AReport_IsExtractedFromAPrivateCopy_ThatIsRemovedAfterwards()
    {
        var extractor = new RecordingExtractor((_, _) => Spec);
        await using var host = Host(extractor);
        using var client = host.CreateClient();
        var report = Encoding.UTF8.GetBytes("the report's bytes");

        using var response = await PostAsync(client, report, "team/Sales Q3.pbix");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(ReportExtractionProtocol.SpecMediaType, response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(Spec, await response.Content.ReadAsStringAsync());

        var call = Assert.Single(extractor.Calls);
        Assert.Equal("team/Sales Q3.pbix", call.ReportFile);
        Assert.Equal(report, call.Bytes);
        Assert.False(File.Exists(call.Path));
        Assert.False(Directory.Exists(Path.GetDirectoryName(call.Path)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("wrong-key-0123456789-abcdefghijklmnop-0123")]
    [InlineData("")]
    public async Task WithoutTheKey_NothingIsExtracted(string? key)
    {
        var extractor = new RecordingExtractor((_, _) => Spec);
        await using var host = Host(extractor);
        using var client = host.CreateClient();

        using var response = await PostAsync(client, [1, 2, 3], key: key);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(extractor.Calls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("../Sales.pbix")]
    [InlineData("Sales#1.pbix")]
    public async Task ALabelThatCannotKeyARow_IsRefused(string? reportFile)
    {
        var extractor = new RecordingExtractor((_, _) => Spec);
        await using var host = Host(extractor);
        using var client = host.CreateClient();

        using var response = await PostAsync(client, [1, 2, 3], reportFile);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(extractor.Calls);
    }

    [Fact]
    public async Task AnEmptyUpload_IsRefused()
    {
        var extractor = new RecordingExtractor((_, _) => Spec);
        await using var host = Host(extractor);
        using var client = host.CreateClient();

        using var response = await PostAsync(client, []);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(extractor.Calls);
    }

    [Fact]
    public async Task AnOversizedUpload_IsRefused()
    {
        var extractor = new RecordingExtractor((_, _) => Spec);
        await using var host = Host(extractor, maxUploadMegabytes: 1);
        using var client = host.CreateClient();

        using var response = await PostAsync(client, new byte[(1024 * 1024) + 1]);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Empty(extractor.Calls);
    }

    [Fact]
    public async Task AReportTheToolRefuses_IsA422_SayingWhy_AndStillCleansUp()
    {
        var extractor = new RecordingExtractor((_, _) => throw new PbixExtractException("the file has no readable report layer"));
        await using var host = Host(extractor);
        using var client = host.CreateClient();

        using var response = await PostAsync(client, [1, 2, 3]);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("no readable report layer", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.False(File.Exists(Assert.Single(extractor.Calls).Path));
    }

    [Fact]
    public async Task ABurstBeyondTheSlots_IsTurnedAway()
    {
        using var release = new ManualResetEventSlim();
        using var entered = new SemaphoreSlim(0);
        var extractor = new RecordingExtractor((_, _) =>
        {
            entered.Release();
            release.Wait(TimeSpan.FromSeconds(30));
            return Spec;
        });
        await using var host = Host(extractor, maxConcurrent: 1, queueLimit: 0);
        using var client = host.CreateClient();

        var first = PostAsync(client, [1]);
        Assert.True(await entered.WaitAsync(TimeSpan.FromSeconds(30)));

        using (var busy = await PostAsync(client, [2]))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, busy.StatusCode);
            Assert.True(busy.Headers.Contains("Retry-After"));
        }

        release.Set();
        using var done = await first;
        Assert.Equal(HttpStatusCode.OK, done.StatusCode);

        // The slot is free again once the first report is done.
        using var after = await PostAsync(client, [3]);
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
    }

    [Fact]
    public async Task Readiness_ReportsAMissingTool()
    {
        await using var host = Host(new RecordingExtractor((_, _) => Spec) { Available = false });
        using var client = host.CreateClient();

        using var live = await client.GetAsync(new Uri("/health/live", UriKind.Relative));
        using var ready = await client.GetAsync(new Uri("/health/ready", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("too-short")]
    public void AMissingOrWeakKey_FailsAtStartup(string? key)
    {
        var options = new ExtractorOptions { ApiKey = key };

        var ex = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains("ApiKey", ex.Message, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task TheRealTool_ExtractsAReport()
    {
        Skip.If(ReportSpecs.LocateExtractor() is null,
            $"The 'pbix-extract' tool was not found. Build it with 'make -C tools/pbix-extract' and set {ReportSpecs.ExtractorPathVariable}.");
        var path = Path.Combine(Path.GetTempPath(), "sqlflow-extractor-" + Guid.NewGuid().ToString("N"), "revenue.pbix");
        try
        {
            PbixFixture.Write(path);
            await using var host = new WebApplicationFactory<Program>().WithWebHostBuilder(
                builder => builder.UseSetting("PbixExtractor:ApiKey", Key));
            using var client = host.CreateClient();

            using var response = await PostAsync(client, await File.ReadAllBytesAsync(path), "revenue.pbix");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var summary = ReportSpecs.Inspect(await response.Content.ReadAsStringAsync(), "the answer");
            Assert.Equal(1, summary.Pages);
            Assert.Equal(1, summary.Visuals);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    private sealed record ExtractCall(string Path, string ReportFile, byte[] Bytes);

    private sealed class RecordingExtractor(Func<string, string, string> extract) : IReportExtractor
    {
        public List<ExtractCall> Calls { get; } = [];

        public bool Available { get; init; } = true;

        public bool IsAvailable => Available;

        public Task<string> ExtractAsync(string pbixPath, string reportFile, CancellationToken ct)
        {
            lock (Calls)
            {
                Calls.Add(new ExtractCall(pbixPath, reportFile, File.ReadAllBytes(pbixPath)));
            }

            return Task.Run(() => extract(pbixPath, reportFile), ct);
        }
    }
}
