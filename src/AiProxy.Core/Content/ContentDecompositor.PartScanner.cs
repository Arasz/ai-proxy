using AiProxy.Core.Json;

namespace AiProxy.Core.Content;

public sealed partial class ContentDecompositor
{
    /// <summary>Walks a payload line by line and cuts it into the spans of its structure and its text blocks.</summary>
    /// <remarks>A transient component: one scanner reads one payload, once.</remarks>
    /// <param name="content">The payload to cut.</param>
    private sealed class PartScanner(string content)
    {
        private readonly List<Span> _spans = [];
        private int _blockEnd;
        private int _blockStart = -1;
        private int _failedAttempts;
        private int _position;

        /// <summary>Cuts the payload and joins the neighbouring text blocks of one kind.</summary>
        public List<Span> Scan()
        {
            while (_position < content.Length)
            {
                var line = LineAt(_position);

                if (TakeFence(line) || TakeStructure(line))
                {
                    continue;
                }

                if (line.IsBlank)
                {
                    EndBlock();
                }
                else
                {
                    ExtendBlock(line);
                }

                _position = line.Next;
            }

            EndBlock();

            return Merge(content, _spans);
        }

        private Line LineAt(int start)
        {
            var newline = content.IndexOf(StructureMarkers.NewLine, start);
            var end = newline < 0 ? content.Length : newline;
            var next = newline < 0 ? content.Length : newline + 1;

            return new Line(end, next, content[start..end].TrimStart());
        }

        private bool TakeFence(Line line)
        {
            if (!line.Trimmed.StartsWith(StructureMarkers.Fence.Backticks, StringComparison.Ordinal))
            {
                return false;
            }

            var fenceEnd = FindFenceEnd(content, line.Next, line.Trimmed[0]);

            return fenceEnd > 0 && Take(fenceEnd, ContentType.SourceCode);
        }

        /// <summary>It looks for a JSON value or an XML element, while the failed attempts are still within the cap.</summary>
        private bool TakeStructure(Line line) => _failedAttempts < MaxFailedStructureAttempts && (TakeJson(line) || TakeXml(line));

        private bool TakeJson(Line line)
        {
            if (!line.StartsJson)
            {
                return false;
            }

            var valueEnd = FindJsonEnd(content, _position);
            if (valueEnd > _position && content[_position..valueEnd].IsJson())
            {
                return Take(valueEnd, ContentType.Json);
            }

            _failedAttempts++;

            return false;
        }

        private bool TakeXml(Line line)
        {
            if (!line.StartsXml)
            {
                return false;
            }

            var elementEnd = FindXmlEnd(content, _position);
            if (elementEnd > _position && IsXml(content[_position..elementEnd]))
            {
                return Take(elementEnd, ContentType.Xml);
            }

            _failedAttempts++;

            return false;
        }

        /// <summary>Closes the open text block, then takes the payload up to <paramref name="end" /> as one structural span.</summary>
        private bool Take(int end, ContentType type)
        {
            EndBlock();
            _spans.Add(new Span(_position, end - _position, type, true));
            _position = end;

            return true;
        }

        private void ExtendBlock(Line line)
        {
            if (_blockStart < 0)
            {
                _blockStart = _position;
            }

            _blockEnd = line.End;
        }

        private void EndBlock()
        {
            if (_blockStart < 0)
            {
                return;
            }

            _spans.Add(new Span(_blockStart, _blockEnd - _blockStart, Classify(content[_blockStart.._blockEnd]), false));
            _blockStart = -1;
            _blockEnd = 0;
        }
    }

    /// <summary>One line of the payload: where its text ends, where the next line starts, and the text without its leading blanks.</summary>
    private readonly record struct Line(int End, int Next, string Trimmed)
    {
        public bool IsBlank => Trimmed.Length == 0;

        public bool StartsJson => Trimmed.StartsWith(StructureMarkers.Json.ObjectOpen) || Trimmed.StartsWith(StructureMarkers.Json.ArrayOpen);

        public bool StartsXml =>
            Trimmed.StartsWith(StructureMarkers.Xml.TagOpen) &&
            !Trimmed.StartsWith(StructureMarkers.Xml.CommentOpen, StringComparison.Ordinal);
    }
}
