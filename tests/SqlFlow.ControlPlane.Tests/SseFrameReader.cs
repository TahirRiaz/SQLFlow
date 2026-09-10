using System.Text;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>Reads SSE frames off a live response stream for the streaming API tests: frames are blank-line
/// separated, comment lines (heartbeats) are skipped, and each read asserts the expected event name so a stray
/// frame fails loudly.</summary>
internal sealed class SseFrameReader(Stream stream)
{
    private readonly byte[] _chunk = new byte[4096];
    private string _buffer = string.Empty;

    public async Task<string> ReadAsync(string expectedEvent, CancellationToken ct)
    {
        while (true)
        {
            var frame = TakeFrame();
            if (frame is null)
            {
                var read = await stream.ReadAsync(_chunk, ct);
                Assert.True(read > 0, $"the stream ended while waiting for an '{expectedEvent}' frame");
                _buffer += Encoding.UTF8.GetString(_chunk, 0, read);
                continue;
            }

            var (name, data) = frame.Value;
            if (name is null)
            {
                continue; // a heartbeat comment frame
            }

            Assert.Equal(expectedEvent, name);
            return data;
        }
    }

    public async Task<bool> AtEndAsync(CancellationToken ct)
    {
        while (TakeFrame() is { } frame)
        {
            if (frame.Name is not null)
            {
                return false; // a data frame arrived after end
            }
        }

        return await stream.ReadAsync(_chunk, ct) == 0;
    }

    private (string? Name, string Data)? TakeFrame()
    {
        var separator = _buffer.IndexOf("\n\n", StringComparison.Ordinal);
        if (separator < 0)
        {
            return null;
        }

        var raw = _buffer[..separator];
        _buffer = _buffer[(separator + 2)..];
        string? name = null;
        var data = new List<string>();
        foreach (var line in raw.Split('\n'))
        {
            if (line.StartsWith("event:", StringComparison.Ordinal))
            {
                name = line["event:".Length..].Trim();
            }
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                data.Add(line["data:".Length..].TrimStart());
            }
        }

        return (name is null && data.Count == 0) ? (null, string.Empty) : (name, string.Join('\n', data));
    }
}
