using System.Globalization;
using System.Text;
using SqlFlow.Core.Abstractions;
using SqlFlow.Core.Model;

namespace SqlFlow.Tests;

/// <summary>
/// An in-memory file store that reports the high-water mark of simultaneously open files, so a test can prove
/// how many files a reader keeps open at once (the <c>readAhead</c> contract) rather than inferring it from
/// timing. Each open is held briefly, so overlapping opens genuinely coincide and a serial reader cannot
/// accidentally look concurrent.
/// </summary>
internal sealed class ConcurrencyTrackingFileStore(int fileCount, string extension, Func<string, string> content) : IFileStore
{
    public const string Root = "mem://files";

    private readonly Lock _gate = new();
    private int _open;

    public int MaxConcurrentOpens { get; private set; }

    public bool CanHandle(string location) => location.StartsWith("mem://", StringComparison.Ordinal);

    public Task<IReadOnlyList<FileRef>> ListAsync(string location, FileDiscovery discovery, CancellationToken ct = default)
    {
        var files = Enumerable.Range(1, fileCount)
            .Select(i =>
            {
                var name = $"f{i.ToString("00", CultureInfo.InvariantCulture)}.{extension}";
                return new FileRef
                {
                    Name = name,
                    Path = $"{Root}/{name}",
                    Size = 64,
                    Modified = new DateTimeOffset(2026, 1, i, 0, 0, 0, TimeSpan.Zero),
                };
            })
            .ToList();

        return Task.FromResult<IReadOnlyList<FileRef>>(files);
    }

    public async Task<Stream> OpenReadAsync(FileRef file, CancellationToken ct = default)
    {
        lock (_gate)
        {
            _open++;
            MaxConcurrentOpens = Math.Max(MaxConcurrentOpens, _open);
        }

        try
        {
            // Long enough that overlapping opens actually overlap, short enough to keep the test quick.
            await Task.Delay(50, ct).ConfigureAwait(false);
            return new MemoryStream(Encoding.UTF8.GetBytes(content(file.Name)));
        }
        finally
        {
            lock (_gate)
            {
                _open--;
            }
        }
    }
}
