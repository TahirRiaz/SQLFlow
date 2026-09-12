using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using SqlFlow.Copy;
using SqlFlow.Core;
using SqlFlow.Core.Copy;
using SqlFlow.Core.Runs;
using SqlFlow.Yaml;
using Xunit;

namespace SqlFlow.Copy.Tests;

/// <summary>
/// The copy engine over the local endpoint (deterministic, no cloud): verbatim copy with and without structure,
/// pattern and modified-within filtering, and a zip -> unzip round trip, plus the cpy YAML loader's mapping and
/// validation. The Azure and SFTP endpoints share the same engine seam; only the endpoint I/O differs.
/// </summary>
public sealed class CopyEngineTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sqlflow-cpy-" + Guid.NewGuid().ToString("N")[..8]);

    public CopyEngineTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static CopyEngine Engine() => new([new LocalCopyEndpoint()], TimeProvider.System);

    private string Write(string relative, string content)
    {
        var path = Path.Combine(_dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private CopyFlow Flow(CopyOperation op, string src, string trg, string pattern = "*", bool preserve = true, string? zipName = null)
        => new()
        {
            Name = "T",
            Operation = op,
            Steps = [new CopyStep
            {
                Source = new CopyEndpoint { Location = Path.Combine(_dir, src), Pattern = pattern },
                Target = new CopyEndpoint { Location = Path.Combine(_dir, trg) },
            }],
            Options = new CopyOptions { PreserveStructure = preserve, ZipName = zipName },
        };

    private CopyStep Step(string src, string trg, string pattern = "*")
        => new()
        {
            Source = new CopyEndpoint { Location = Path.Combine(_dir, src), Pattern = pattern },
            Target = new CopyEndpoint { Location = Path.Combine(_dir, trg) },
        };

    [Fact]
    public async Task Copy_PreservesFolderStructure()
    {
        Write("src/detail.json", "{\"o\":1}");
        Write("src/sub/sess.json", "{\"o\":2}");

        var result = await Engine().RunAsync(Flow(CopyOperation.Copy, "src", "dst"), Guid.NewGuid(), NullRunEventSink.Instance, default);

        Assert.True(result.Success);
        Assert.Equal(2, result.Matched);
        Assert.Equal(2, result.FilesWritten);
        Assert.True(File.Exists(Path.Combine(_dir, "dst", "detail.json")));
        Assert.True(File.Exists(Path.Combine(_dir, "dst", "sub", "sess.json")));
    }

    [Fact]
    public async Task Copy_Flatten_DropsStructure()
    {
        Write("src/a/one.txt", "1");
        Write("src/b/two.txt", "2");

        var result = await Engine().RunAsync(Flow(CopyOperation.Copy, "src", "flat", preserve: false), Guid.NewGuid(), NullRunEventSink.Instance, default);

        Assert.True(result.Success);
        Assert.True(File.Exists(Path.Combine(_dir, "flat", "one.txt")));
        Assert.True(File.Exists(Path.Combine(_dir, "flat", "two.txt")));
        Assert.False(Directory.Exists(Path.Combine(_dir, "flat", "a")));
    }

    [Fact]
    public async Task Copy_Unchanged_SkipsRewriteAndKeepsModifiedTime()
    {
        // The whole point: a re-run of an unchanged file must not rewrite the target, because bumping its last-write
        // time re-triggers downstream ingestion for a file that has not changed.
        Write("src/detail.json", "{\"o\":1}");
        var flow = Flow(CopyOperation.Copy, "src", "dst");

        var first = await Engine().RunAsync(flow, Guid.NewGuid(), NullRunEventSink.Instance, default);
        Assert.Equal(1, first.FilesWritten);
        Assert.Equal(0, first.FilesSkipped);

        var target = Path.Combine(_dir, "dst", "detail.json");
        var stampBefore = File.GetLastWriteTimeUtc(target);

        var second = await Engine().RunAsync(flow, Guid.NewGuid(), NullRunEventSink.Instance, default);

        Assert.True(second.Success);
        Assert.Equal(1, second.Matched);
        Assert.Equal(0, second.FilesWritten);
        Assert.Equal(1, second.FilesSkipped);
        Assert.Empty(second.Files);
        Assert.Equal(stampBefore, File.GetLastWriteTimeUtc(target));
    }

    [Fact]
    public async Task Copy_MetadataHashMatch_SkipsWithoutTransferring()
    {
        // The efficiency guarantee: when the source lists a content hash (as Azure blobs do), an unchanged re-run must
        // transfer nothing - no download of the source, no write to the target - deciding purely from metadata.
        var mem = new MemoryEndpoint();
        mem.Store["mem://src/detail.json"] = Encoding.UTF8.GetBytes("{\"o\":1}");
        var engine = new CopyEngine([mem], TimeProvider.System);
        var flow = new CopyFlow
        {
            Name = "T",
            Steps = [new CopyStep
            {
                Source = new CopyEndpoint { Location = "mem://src" },
                Target = new CopyEndpoint { Location = "mem://dst" },
            }],
        };

        var first = await engine.RunAsync(flow, Guid.NewGuid(), NullRunEventSink.Instance, default);
        Assert.Equal(1, first.FilesWritten);
        Assert.Equal(1, mem.Reads);
        Assert.Equal(1, mem.Writes);

        var second = await engine.RunAsync(flow, Guid.NewGuid(), NullRunEventSink.Instance, default);
        Assert.True(second.Success);
        Assert.Equal(0, second.FilesWritten);
        Assert.Equal(1, second.FilesSkipped);
        // No further read and no further write: the unchanged file moved zero bytes on the re-run.
        Assert.Equal(1, mem.Reads);
        Assert.Equal(1, mem.Writes);
    }

    [Fact]
    public async Task Copy_SkipUnchangedDisabled_RewritesEveryRun()
    {
        // The gate: options.skipUnchanged=false forces every matched file to be rewritten, paying no comparison cost.
        var mem = new MemoryEndpoint();
        mem.Store["mem://src/detail.json"] = Encoding.UTF8.GetBytes("{\"o\":1}");
        var engine = new CopyEngine([mem], TimeProvider.System);
        var flow = new CopyFlow
        {
            Name = "T",
            Options = new CopyOptions { SkipUnchanged = false },
            Steps = [new CopyStep
            {
                Source = new CopyEndpoint { Location = "mem://src" },
                Target = new CopyEndpoint { Location = "mem://dst" },
            }],
        };

        await engine.RunAsync(flow, Guid.NewGuid(), NullRunEventSink.Instance, default);
        var second = await engine.RunAsync(flow, Guid.NewGuid(), NullRunEventSink.Instance, default);

        Assert.Equal(1, second.FilesWritten);
        Assert.Equal(0, second.FilesSkipped);
        Assert.Equal(2, mem.Writes); // rewritten on the second run despite identical content
    }

    [Fact]
    public async Task Copy_BackfillWindow_RewritesUnchangedFile()
    {
        // A backfill is an explicit "reprocess these files" request, so unchanged-detection is disabled for the run:
        // an identical file in the window is re-copied (overwritten), not skipped, re-landing it with a fresh
        // timestamp so the downstream incremental flows pick it up again.
        var mem = new MemoryEndpoint();
        mem.Store["mem://src/detail.json"] = Encoding.UTF8.GetBytes("{\"o\":1}");
        var engine = new CopyEngine([mem], TimeProvider.System);
        var flow = new CopyFlow
        {
            Name = "T",
            // Deduplication is ON by default; the backfill window is what suppresses it for this run.
            Steps = [new CopyStep
            {
                Source = new CopyEndpoint { Location = "mem://src" },
                Target = new CopyEndpoint { Location = "mem://dst" },
            }],
        };
        var backfill = new RunParameters { BackfillFrom = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc) };

        await engine.RunAsync(flow, Guid.NewGuid(), NullRunEventSink.Instance, default, backfill);
        var second = await engine.RunAsync(flow, Guid.NewGuid(), NullRunEventSink.Instance, default, backfill);

        // Rewritten on the second run despite identical content: the backfill re-lands the file rather than skipping it.
        Assert.Equal(1, second.FilesWritten);
        Assert.Equal(0, second.FilesSkipped);
        Assert.Equal(2, mem.Writes);
    }

    [Fact]
    public async Task Copy_MetadataHashDiffers_DownloadsAndRewrites()
    {
        var mem = new MemoryEndpoint();
        mem.Store["mem://src/detail.json"] = Encoding.UTF8.GetBytes("{\"o\":1}");
        var engine = new CopyEngine([mem], TimeProvider.System);
        var flow = new CopyFlow
        {
            Name = "T",
            Steps = [new CopyStep
            {
                Source = new CopyEndpoint { Location = "mem://src" },
                Target = new CopyEndpoint { Location = "mem://dst" },
            }],
        };

        await engine.RunAsync(flow, Guid.NewGuid(), NullRunEventSink.Instance, default);

        // The source changed: the re-run must download and rewrite it.
        mem.Store["mem://src/detail.json"] = Encoding.UTF8.GetBytes("{\"o\":2}");
        var second = await engine.RunAsync(flow, Guid.NewGuid(), NullRunEventSink.Instance, default);

        Assert.Equal(1, second.FilesWritten);
        Assert.Equal(0, second.FilesSkipped);
        Assert.Equal(2, mem.Reads);
        Assert.Equal(2, mem.Writes);
        Assert.Equal("{\"o\":2}", Encoding.UTF8.GetString(mem.Store["mem://dst/detail.json"]));
    }

    [Fact]
    public async Task Copy_ChangedContent_RewritesTarget()
    {
        Write("src/detail.json", "{\"o\":1}");
        var flow = Flow(CopyOperation.Copy, "src", "dst");
        await Engine().RunAsync(flow, Guid.NewGuid(), NullRunEventSink.Instance, default);

        // Same file name, different bytes: the target must be rewritten, not skipped.
        Write("src/detail.json", "{\"o\":2}");
        var result = await Engine().RunAsync(flow, Guid.NewGuid(), NullRunEventSink.Instance, default);

        Assert.True(result.Success);
        Assert.Equal(1, result.FilesWritten);
        Assert.Equal(0, result.FilesSkipped);
        Assert.Equal("{\"o\":2}", await File.ReadAllTextAsync(Path.Combine(_dir, "dst", "detail.json")));
    }

    [Fact]
    public async Task Copy_SameLengthDifferentBytes_RewritesTarget()
    {
        // A same-length change must still be detected: the compare is byte-for-byte, not length-only.
        Write("src/detail.json", "AAAA");
        var flow = Flow(CopyOperation.Copy, "src", "dst");
        await Engine().RunAsync(flow, Guid.NewGuid(), NullRunEventSink.Instance, default);

        Write("src/detail.json", "AABA");
        var result = await Engine().RunAsync(flow, Guid.NewGuid(), NullRunEventSink.Instance, default);

        Assert.Equal(1, result.FilesWritten);
        Assert.Equal(0, result.FilesSkipped);
        Assert.Equal("AABA", await File.ReadAllTextAsync(Path.Combine(_dir, "dst", "detail.json")));
    }

    [Fact]
    public async Task Copy_Pattern_FiltersByGlob()
    {
        Write("src/keep.json", "{}");
        Write("src/skip.csv", "x");

        var result = await Engine().RunAsync(Flow(CopyOperation.Copy, "src", "dst", pattern: "*.json"), Guid.NewGuid(), NullRunEventSink.Instance, default);

        Assert.Equal(1, result.FilesWritten);
        Assert.True(File.Exists(Path.Combine(_dir, "dst", "keep.json")));
        Assert.False(File.Exists(Path.Combine(_dir, "dst", "skip.csv")));
    }

    [Fact]
    public async Task Zip_ThenUnzip_RoundTrips()
    {
        Write("src/detail.json", "{\"o\":1}");
        Write("src/sub/sess.json", "{\"o\":2}");

        var zip = await Engine().RunAsync(Flow(CopyOperation.Zip, "src", "zipout", zipName: "bundle.zip"), Guid.NewGuid(), NullRunEventSink.Instance, default);
        Assert.True(zip.Success);
        var archivePath = Path.Combine(_dir, "zipout", "bundle.zip");
        Assert.True(File.Exists(archivePath));
        using (var archive = ZipFile.OpenRead(archivePath))
        {
            Assert.Equal(2, archive.Entries.Count);
            Assert.Contains(archive.Entries, e => e.FullName is "detail.json" or "sub/sess.json");
        }

        var unzip = await Engine().RunAsync(Flow(CopyOperation.Unzip, "zipout", "extracted", pattern: "*.zip"), Guid.NewGuid(), NullRunEventSink.Instance, default);
        Assert.True(unzip.Success);
        // Preserve-structure unzip nests each archive under a folder named for it.
        Assert.True(File.Exists(Path.Combine(_dir, "extracted", "bundle", "detail.json")));
        Assert.True(File.Exists(Path.Combine(_dir, "extracted", "bundle", "sub", "sess.json")));
    }

    [Fact]
    public async Task Cancelled_Propagates_RatherThanReportingFailure()
    {
        Write("src/detail.json", "{\"o\":1}");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Cancelled is its own terminal state, so the engine must NOT swallow the cancellation into a
        // Success = false result: that artifact would record an operator's cancel as a failed run. The callers
        // (RunWorker, the CLI) distinguish an operator cancel from a shutdown, so the cancellation reaches them.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Engine().RunAsync(Flow(CopyOperation.Copy, "src", "dst"), Guid.NewGuid(), NullRunEventSink.Instance, cts.Token));
    }

    [Fact]
    public async Task UnknownEndpoint_FailsCleanly()
    {
        var flow = new CopyFlow
        {
            Name = "T",
            Steps = [new CopyStep
            {
                Source = new CopyEndpoint { Location = Path.Combine(_dir, "src") },
                Target = new CopyEndpoint { Location = "abfss://fs@acct.dfs.core.windows.net/x" },
            }],
        };
        Write("src/a.txt", "x");
        // A local engine with no Azure endpoint cannot handle an abfss target.
        var engine = new CopyEngine([new LocalCopyEndpoint()], TimeProvider.System);
        var result = await engine.RunAsync(flow, Guid.NewGuid(), NullRunEventSink.Instance, default);
        Assert.False(result.Success);
        Assert.Contains("No copy endpoint handles the target", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Loader_MapsAndValidates()
    {
        var flow = new YamlCopyFlowLoader().Parse("""
            flowType: cpy
            name: BB_Boatbooking_00_cpy
            batch: BB
            operation: zip
            source:
              location: abfss://boatbooking@acct.dfs.core.windows.net
              pattern: "*.json"
              modifiedWithinDays: 14
            target:
              location: ./out
            options:
              zipName: bundle.zip
            """);

        Assert.Equal("BB_Boatbooking_00_cpy", flow.Name);
        Assert.Equal(CopyOperation.Zip, flow.Operation);
        var step = Assert.Single(flow.Steps);
        Assert.Equal("*.json", step.Source.Pattern);
        Assert.Equal(14, step.Source.ModifiedWithinDays);
        Assert.Equal("bundle.zip", flow.Options.ZipName);
    }

    [Fact]
    public void Loader_MissingSource_Fails()
    {
        var ex = Assert.Throws<FlowValidationException>(() => new YamlCopyFlowLoader().Parse("flowType: cpy\nname: x\ntarget: { location: ./out }\n"));
        Assert.Contains("'source' is required", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Loader_ItemsList_MapsEachStep()
    {
        var flow = new YamlCopyFlowLoader().Parse("""
            flowType: cpy
            name: BB_Boatbooking_00_cpy
            batch: BB
            items:
              - source: { location: abfss://boatbooking@acct.dfs.core.windows.net/DETAIL, pattern: "*.json", modifiedWithinDays: 14 }
                target: { location: abfss://datalakev2@acct.dfs.core.windows.net/raw/boatbooking/history/detail }
              - source: { location: abfss://boatbooking@acct.dfs.core.windows.net/SESS }
                target: { location: abfss://datalakev2@acct.dfs.core.windows.net/raw/boatbooking/history/sess }
            """);

        Assert.Equal(2, flow.Steps.Count);
        Assert.EndsWith("/DETAIL", flow.Steps[0].Source.Location, StringComparison.Ordinal);
        Assert.EndsWith("/detail", flow.Steps[0].Target.Location, StringComparison.Ordinal);
        Assert.Equal("*.json", flow.Steps[0].Source.Pattern);
        Assert.EndsWith("/sess", flow.Steps[1].Target.Location, StringComparison.Ordinal);
    }

    [Fact]
    public void Loader_ItemsAndSingleSource_Fails()
    {
        var ex = Assert.Throws<FlowValidationException>(() => new YamlCopyFlowLoader().Parse("""
            flowType: cpy
            name: x
            source: { location: ./a }
            target: { location: ./b }
            items:
              - source: { location: ./c }
                target: { location: ./d }
            """));
        Assert.Contains("not both", ex.Message, StringComparison.Ordinal);
    }

    private CopyFlow FlowWithWindow(string src, string trg, int modifiedWithinDays)
        => new()
        {
            Name = "T",
            Operation = CopyOperation.Copy,
            Steps = [new CopyStep
            {
                Source = new CopyEndpoint { Location = Path.Combine(_dir, src), ModifiedWithinDays = modifiedWithinDays },
                Target = new CopyEndpoint { Location = Path.Combine(_dir, trg) },
            }],
        };

    [Fact]
    public async Task Copy_ModifiedWithinDays_ExcludesOlderFilesByDefault()
    {
        // The flow's declared window (the boatbooking runbook's 14 days) is the default when no override is supplied.
        var oldFile = Write("src/old.json", "{}");
        var newFile = Write("src/new.json", "{}");
        File.SetLastWriteTimeUtc(oldFile, DateTime.UtcNow.AddDays(-30));
        File.SetLastWriteTimeUtc(newFile, DateTime.UtcNow.AddDays(-1));

        var result = await Engine().RunAsync(FlowWithWindow("src", "dst", 14), Guid.NewGuid(), NullRunEventSink.Instance, default);

        Assert.True(result.Success);
        Assert.Equal(1, result.FilesWritten);
        Assert.True(File.Exists(Path.Combine(_dir, "dst", "new.json")));
        Assert.False(File.Exists(Path.Combine(_dir, "dst", "old.json")));
    }

    [Fact]
    public async Task Copy_BackfillWindow_OverridesModifiedWithinDays_ToReachHistory()
    {
        // A trigger-time backfill window replaces the 14-day default, reaching files it would otherwise exclude.
        var oldFile = Write("src/old.json", "{}");
        var newFile = Write("src/new.json", "{}");
        File.SetLastWriteTimeUtc(oldFile, DateTime.UtcNow.AddDays(-30));
        File.SetLastWriteTimeUtc(newFile, DateTime.UtcNow.AddDays(-1));

        var parameters = new RunParameters
        {
            BackfillFrom = DateTime.UtcNow.AddDays(-40),
            BackfillTo = DateTime.UtcNow.AddDays(-20),
        };
        var result = await Engine().RunAsync(
            FlowWithWindow("src", "dst", 14), Guid.NewGuid(), NullRunEventSink.Instance, default, parameters);

        Assert.True(result.Success);
        Assert.Equal(1, result.FilesWritten);
        Assert.True(File.Exists(Path.Combine(_dir, "dst", "old.json")));
        Assert.False(File.Exists(Path.Combine(_dir, "dst", "new.json")));
    }

    [Fact]
    public async Task Copy_FullLoad_IgnoresModifiedWithinDays()
    {
        var oldFile = Write("src/old.json", "{}");
        var newFile = Write("src/new.json", "{}");
        File.SetLastWriteTimeUtc(oldFile, DateTime.UtcNow.AddDays(-100));
        File.SetLastWriteTimeUtc(newFile, DateTime.UtcNow.AddDays(-1));

        var result = await Engine().RunAsync(
            FlowWithWindow("src", "dst", 14), Guid.NewGuid(), NullRunEventSink.Instance, default,
            new RunParameters { FullLoad = true });

        Assert.True(result.Success);
        Assert.Equal(2, result.FilesWritten);
    }

    [Fact]
    public async Task Copy_MultipleSteps_CopyEachSourceToItsTarget()
    {
        // One pipeline, several copies: each step's files land under its own target folder, counts aggregate.
        Write("detailsrc/d1.json", "{}");
        Write("detailsrc/2024/d2.json", "{}");
        Write("sesssrc/s1.json", "{}");

        var flow = new CopyFlow
        {
            Name = "BB",
            Steps = [Step("detailsrc", "lake/detail"), Step("sesssrc", "lake/sess")],
        };
        var result = await Engine().RunAsync(flow, Guid.NewGuid(), NullRunEventSink.Instance, default);

        Assert.True(result.Success);
        Assert.Equal(3, result.Matched);
        Assert.Equal(3, result.FilesWritten);
        Assert.True(File.Exists(Path.Combine(_dir, "lake", "detail", "d1.json")));
        Assert.True(File.Exists(Path.Combine(_dir, "lake", "detail", "2024", "d2.json")));
        Assert.True(File.Exists(Path.Combine(_dir, "lake", "sess", "s1.json")));
    }

    /// <summary>An in-memory endpoint (scheme <c>mem://</c>) that, like the Azure endpoint, reports each file's
    /// content hash from its listing, and counts reads and writes so a test can prove an unchanged re-run transfers
    /// nothing. MD5 here is a content fingerprint, matching the algorithm Azure records as a blob's ContentHash.</summary>
#pragma warning disable CA5351 // Do Not Use Broken Cryptographic Algorithms
    private sealed class MemoryEndpoint : ICopyEndpoint
    {
        public readonly Dictionary<string, byte[]> Store = new(StringComparer.Ordinal);
        public int Reads;
        public int Writes;

        public bool CanHandle(string location) => location.StartsWith("mem://", StringComparison.Ordinal);

        public async IAsyncEnumerable<CopyItem> ListAsync(
            CopyEndpoint endpoint, CopyModifiedWindow window, [EnumeratorCancellation] CancellationToken ct)
        {
            var prefix = endpoint.Location.TrimEnd('/') + "/";
            foreach (var (key, bytes) in Store)
            {
                if (!key.StartsWith(prefix, StringComparison.Ordinal))
                {
                    continue;
                }

                var relative = key[prefix.Length..];
                var leaf = relative.Contains('/') ? relative[(relative.LastIndexOf('/') + 1)..] : relative;
                yield return new CopyItem(key, relative, leaf, DateTimeOffset.UnixEpoch, bytes.Length, MD5.HashData(bytes));
            }

            await Task.CompletedTask;
        }

        public Task<byte[]> ReadAsync(CopyEndpoint endpoint, string absolutePath, CancellationToken ct)
        {
            Reads++;
            return Task.FromResult(Store[absolutePath]);
        }

        public Task<IReadOnlyDictionary<string, byte[]?>> TargetHashIndexAsync(CopyEndpoint endpoint, CancellationToken ct)
        {
            var prefix = endpoint.Location.TrimEnd('/') + "/";
            var index = new Dictionary<string, byte[]?>(StringComparer.Ordinal);
            foreach (var (key, bytes) in Store)
            {
                if (key.StartsWith(prefix, StringComparison.Ordinal))
                {
                    index[key[prefix.Length..]] = MD5.HashData(bytes);
                }
            }

            return Task.FromResult<IReadOnlyDictionary<string, byte[]?>>(index);
        }

        public Task<byte[]?> TargetContentHashAsync(CopyEndpoint endpoint, string relativePath, CancellationToken ct)
        {
            var key = endpoint.Location.TrimEnd('/') + "/" + relativePath;
            return Task.FromResult(Store.TryGetValue(key, out var bytes) ? MD5.HashData(bytes) : null);
        }

        public Task<string> WriteAsync(
            CopyEndpoint endpoint, string relativePath, ReadOnlyMemory<byte> content, bool overwrite, byte[] contentHash, CancellationToken ct)
        {
            Writes++;
            var key = endpoint.Location.TrimEnd('/') + "/" + relativePath;
            Store[key] = content.ToArray();
            return Task.FromResult(key);
        }
    }
#pragma warning restore CA5351
}
