using System.Text;
using SqlFlow.Acquire.Landing;
using SqlFlow.Core;
using Xunit;

namespace SqlFlow.Acquire.Tests;

/// <summary>
/// The local raw landing store's skip-if-unchanged write: an acquire fetch that returns the same file must not rewrite
/// the landed file, so its last-write time is not bumped and the downstream file flow is not re-triggered.
/// </summary>
public sealed class LandingStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sqlflow-landing-" + Guid.NewGuid().ToString("N")[..8]);

    public LandingStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task Put_UnchangedContent_SkipsRewriteAndKeepsModifiedTime()
    {
        var store = new LocalRawLandingStore();
        var location = Path.Combine(_dir, "sub", "payload.json");
        var payload = Encoding.UTF8.GetBytes("{\"a\":1}");

        var firstWrote = await store.PutAsync(location, payload, overwrite: true);
        Assert.True(firstWrote);
        var stamp = File.GetLastWriteTimeUtc(location);

        var secondWrote = await store.PutAsync(location, payload, overwrite: true);

        Assert.False(secondWrote);
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(location));
    }

    [Fact]
    public async Task Put_ChangedContent_Rewrites()
    {
        var store = new LocalRawLandingStore();
        var location = Path.Combine(_dir, "payload.json");
        await store.PutAsync(location, Encoding.UTF8.GetBytes("{\"a\":1}"), overwrite: true);

        var wrote = await store.PutAsync(location, Encoding.UTF8.GetBytes("{\"a\":2}"), overwrite: true);

        Assert.True(wrote);
        Assert.Equal("{\"a\":2}", await File.ReadAllTextAsync(location));
    }

    [Fact]
    public async Task Put_NoOverwrite_FailsWhenExists()
    {
        var store = new LocalRawLandingStore();
        var location = Path.Combine(_dir, "payload.json");
        await store.PutAsync(location, Encoding.UTF8.GetBytes("{\"a\":1}"), overwrite: true);

        await Assert.ThrowsAsync<SqlFlowException>(() =>
            store.PutAsync(location, Encoding.UTF8.GetBytes("{\"a\":1}"), overwrite: false));
    }
}
