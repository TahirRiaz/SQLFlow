using System.Globalization;
using SqlFlow.Acquire.Engine;
using SqlFlow.Core.Acquire;
using SqlFlow.Core.Runs;
using Xunit;

namespace SqlFlow.Acquire.Tests;

public sealed class DebugTests
{
    [Fact]
    public async Task Dry_run_captures_pages_without_writing_files()
    {
        var handler = new StubHttpHandler().Json("/items", req =>
        {
            var page = QueryPage(req.RequestUri!);
            return page <= 5 ? $"[{{\"id\":{page}}}]" : "[]";
        });
        var engine = TestEngine.Create(handler, new FakeSecrets(), new FixedClock(new DateTimeOffset(2026, 7, 9, 0, 0, 0, TimeSpan.Zero)), out var dir);
        var flow = new AcquireFlow
        {
            Name = "Debug_Flow",
            Items = [new AcquireItem
            {
                Source = new AcquireSource
                {
                    BaseUrl = "https://api.test.local",
                    Request = new AcquireRequest { Path = "/items" },
                    Pagination = new AcquirePagination { Strategy = AcquirePaginationStrategy.Page },
                },
                Landing = new AcquireLanding { Target = dir, PathTemplate = "data/{page}" },
            }],
        };

        var probe = new CollectingProbe();
        var result = await engine.RunAsync(flow, Guid.NewGuid(), NullRunEventSink.Instance, null, CancellationToken.None,
            new AcquireRunOverrides { DryRun = true, Probe = probe, MaxPagesOverride = 2 });

        Assert.True(result.Success);
        Assert.Empty(TestEngine.LandedFiles(dir));           // dry run wrote nothing to disk
        Assert.Equal(2, probe.Pages.Count);                  // capped at 2 pages
        Assert.All(probe.Pages, p => Assert.Equal(200, p.Status));
        Assert.Contains(probe.Pages, p => p.BodyPreview.Contains("\"id\":1", StringComparison.Ordinal));
        Assert.All(probe.Pages, p => Assert.NotNull(p.LandedTo));  // shows where it WOULD land
    }

    private static int QueryPage(Uri uri)
    {
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            if (pair.StartsWith("page=", StringComparison.Ordinal))
            {
                return int.Parse(pair["page=".Length..], CultureInfo.InvariantCulture);
            }
        }

        return 1;
    }
}
