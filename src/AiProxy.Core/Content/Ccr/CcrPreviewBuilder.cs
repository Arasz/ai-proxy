using System.Globalization;
using System.Text;

namespace AiProxy.Core.Content.Ccr;

/// <summary>Builds deterministic previews. Use each transient instance for one operation at a time.</summary>
public sealed class CcrPreviewBuilder(CcrPreviewOverlay overlay)
{
    private readonly List<string> _body = [];
    private readonly StringBuilder _hidden = new();
    private int _budget;
    private string? _cut;
    private int _gapCost;
    private string _id = "";
    private int _lineRoom;
    private string[] _lines = [];
    private int _overlayRoom;
    private bool[] _shown = [];

    /// <summary>Returns a preview, or null for a small region, an existing preview, or insufficient budget.</summary>
    public CcrPreview? Build(string original, CcrOptions options)
    {
        try
        {
            if (options.TokensOf(original.Length) < options.MinimumTokens
                || CcrPreview.IsIn(original))
            {
                return null;
            }

            if (!Initialize(original, options))
            {
                return null;
            }

            var signalSpent = SelectSignals((int)(_lineRoom * options.SignalShare));
            SelectHeadAndTail(_lineRoom - signalSpent, options.HeadShare);
            AssembleBody();
            return Finish();
        }
        finally
        {
            Reset();
        }
    }

    private bool Initialize(string original, CcrOptions options)
    {
        _id = CcrText.IdOf(original);
        _lines = CcrText.LinesOf(original);
        _shown = new bool[_lines.Length];
        _budget = options.CharactersOf(options.BudgetTokens);
        _gapCost = Gap(_lines.Length, _lines.Length).Length + 1;
        _overlayRoom = (int)(_budget * options.OverlayShare);
        _lineRoom = _budget - (Header(_lines.Length).Length + 1) - _overlayRoom - _gapCost;
        return _lineRoom > 0;
    }

    private int SelectSignals(int room)
    {
        var spent = 0;
        for (var index = 0; index < _lines.Length; index++)
        {
            var cost = _lines[index].Length + 1 + _gapCost;
            if (spent + cost <= room && MustKeepOverlay.CarriesSignal(_lines[index]))
            {
                _shown[index] = true;
                spent += cost;
            }
        }

        return spent;
    }

    private void SelectHeadAndTail(int room, decimal headShare)
    {
        var headRoom = (int)(room * headShare);
        var headSpent = Take(headRoom, Enumerable.Range(0, _lines.Length));
        if (headSpent == 0 && !_shown[0] && _lines[0].Length > _budget && CutFirstLine(headRoom))
        {
            headSpent = headRoom;
        }

        Take(room - headSpent, Enumerable.Range(0, _lines.Length).Reverse());
    }

    private bool CutFirstLine(int room)
    {
        var length = room - (Cut(_lines[0].Length, _lines[0].Length).Length + 2);
        if (length > 0 && char.IsHighSurrogate(_lines[0][length - 1]))
        {
            length--;
        }

        if (length <= 0)
        {
            return false;
        }

        _cut = _lines[0][..length];
        _shown[0] = true;
        return true;
    }

    // Edge selection extends existing blocks and never opens a new gap.
    private int Take(int room, IEnumerable<int> order)
    {
        var spent = 0;
        foreach (var index in order)
        {
            if (_shown[index])
            {
                continue;
            }

            var cost = _lines[index].Length + 1;
            if (spent + cost > room)
            {
                break;
            }

            _shown[index] = true;
            spent += cost;
        }

        return spent;
    }

    private void AssembleBody()
    {
        if (_cut is not null)
        {
            _hidden.Append(_lines[0], _cut.Length, _lines[0].Length - _cut.Length).Append('\n');
        }

        for (var index = 0; index < _lines.Length;)
        {
            index = _shown[index] ? AddShownLine(index) : AddHiddenRun(index);
        }
    }

    private int AddShownLine(int index)
    {
        if (index == 0 && _cut is not null)
        {
            _body.Add(_cut);
            _body.Add(Cut(_cut.Length, _lines[0].Length));
        }
        else
        {
            _body.Add(_lines[index]);
        }

        return index + 1;
    }

    private int AddHiddenRun(int first)
    {
        var end = first;
        while (end < _lines.Length && !_shown[end])
        {
            _hidden.Append(_lines[end]).Append('\n');
            end++;
        }

        _body.Add(Gap(first + 1, end));
        return end;
    }

    private CcrPreview? Finish()
    {
        if (_hidden.Length == 0)
        {
            return null;
        }

        var count = _shown.Count(line => line);
        var text = $"{Header(count)}\n{string.Join("\n", _body)}";
        return new CcrPreview(_id, text + overlay.Build(text, _hidden.ToString(), _overlayRoom), count, _lines.Length);
    }

    private void Reset()
    {
        _lines = [];
        _shown = [];
        _body.Clear();
        _hidden.Clear();
        _id = "";
        _cut = null;
        _budget = 0;
        _gapCost = 0;
        _overlayRoom = 0;
        _lineRoom = 0;
    }

    private string Header(int shown)
    {
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{CcrPreview.Marker}{_id}: {shown} of {_lines.Length} lines shown. Call proxy_retrieve with this id for a line range, a pattern, or the whole text.]");
    }

    private static string Gap(int first, int last)
    {
        return string.Create(CultureInfo.InvariantCulture, $"[lines {first}-{last} not shown]");
    }

    private static string Cut(int at, int length)
    {
        return string.Create(CultureInfo.InvariantCulture, $"[line 1 cut at character {at} of {length}]");
    }
}