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
        var builder = new EventBuilder();
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (line.Length == 0)
            {
                if (builder.Complete() is { } completed)
                {
                    yield return completed;
                }

                continue;
            }

            builder.Accept(line);
        }

        if (builder.Complete() is { } trailing)
        {
            yield return trailing;
        }
    }

    private sealed class EventBuilder
    {
        private readonly StringBuilder _data = new();
        private bool _any;
        private string _name = Protocol.DefaultEvent;

        public void Accept(string line)
        {
            if (line.StartsWith(':'))
            {
                return;
            }

            var colon = line.IndexOf(':');
            var field = colon < 0 ? line : line[..colon];
            var value = colon < 0 ? "" : line[(colon + 1)..].TrimStart(' ');
            switch (field)
            {
                case Protocol.EventField:
                    _name = value;
                    _any = true;
                    break;
                case Protocol.DataField:
                    AppendData(value);
                    break;
            }
        }

        private void AppendData(string value)
        {
            if (_data.Length > 0)
            {
                _data.Append('\n');
            }

            _data.Append(value);
            _any = true;
        }

        public SseEvent? Complete()
        {
            var completed = _any ? new SseEvent(_name, _data.ToString()) : null;
            _name = Protocol.DefaultEvent;
            _data.Clear();
            _any = false;
            return completed;
        }
    }

    private static class Protocol
    {
        public const string DefaultEvent = "message";
        public const string EventField = "event";
        public const string DataField = "data";
    }
}