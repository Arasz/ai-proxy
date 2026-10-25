using System.Text;

namespace AiProxy.Core.Content.Ccr;

/// <summary>Builds the identifier overlay. Use each transient instance for one operation at a time.</summary>
public sealed class CcrPreviewOverlay
{
    private readonly HashSet<string> _known = new(StringComparer.OrdinalIgnoreCase);
    private readonly StringBuilder _block = new();
    private int _count;

    /// <summary>Lists identifiers absent from the shown text, within the supplied character budget.</summary>
    public string Build(string shownText, string hiddenText, int room)
    {
        Reset();
        try
        {
            _known.UnionWith(MustKeepOverlay.Identifiers(shownText));
            _block.Append(MustKeepOverlay.BlockLabel);
            AddIdentifiers(hiddenText, room);
            return _count == 0 ? "" : _block.ToString();
        }
        finally
        {
            Reset();
        }
    }

    private void AddIdentifiers(string hiddenText, int room)
    {
        foreach (var token in MustKeepOverlay.Identifiers(hiddenText))
        {
            if (_known.Contains(token))
            {
                continue;
            }

            var added = token.Length + (_count > 0 ? 1 : 0);
            if (_block.Length + added > room)
            {
                break;
            }

            _block.Append(_count > 0 ? " " : "").Append(token);
            _count++;
        }
    }

    private void Reset()
    {
        _known.Clear();
        _block.Clear();
        _count = 0;
    }
}
