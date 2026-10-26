using System.Runtime.CompilerServices;
using System.Text;

namespace AiProxy.Core.Content.Ccr.Loop;

/// <summary>Reads server-sent events from a stream.</summary>
public sealed class SseReader
{
    /// <summary>Yields each complete event; a trailing event without its blank line is yielded at the end.</summary>
    public async IAsyncEnumerable<SseEvent> ReadAsync(Stream stream, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, false, leaveOpen: true);
        var name = "message";
        var data = new StringBuilder();
        var any = false;
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (line.Length == 0)
            {
                if (any)
                {
                    yield return new SseEvent(name, data.ToString());
                }

                name = "message";
                data.Clear();
                any = false;
                continue;
            }

            if (line.StartsWith(':'))
            {
                continue;
            }

            var colon = line.IndexOf(':');
            var field = colon < 0 ? line : line[..colon];
            var value = colon < 0 ? "" : line[(colon + 1)..].TrimStart(' ');
            if (field == "event")
            {
                name = value;
                any = true;
            }
            else if (field == "data")
            {
                if (data.Length > 0)
                {
                    data.Append('\n');
                }

                data.Append(value);
                any = true;
            }
        }

        if (any)
        {
            yield return new SseEvent(name, data.ToString());
        }
    }
}