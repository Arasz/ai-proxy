using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace AiProxy.Core.Content.Ccr;

/// <summary>A large tool result shortened to a budget of its own lines, each hidden run named by its line range.</summary>
/// <param name="Id">The first 16 hex characters of the SHA-256 of the original's UTF-8 text.</param>
/// <param name="Text">The preview the model reads in place of the original.</param>
/// <param name="ShownLines">How many original lines the preview shows, a cut first line included.</param>
/// <param name="TotalLines">How many lines the original has.</param>
public sealed record CcrPreview(string Id, string Text, int ShownLines, int TotalLines)
{
    /// <summary>The text every preview starts with; a region that already holds it is never previewed again.</summary>
    public const string Marker = "[proxy preview ";

    /// <summary>The id of an original: the first 16 hex characters of the SHA-256 of its UTF-8 text.</summary>
    public static string IdOf(string original) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(original)))[..16];

    /// <summary>The preview of a whole region, or null when the region is under the minimum or already a preview.</summary>
    /// <remarks>A pure function of the original and the options: no clock, counter, randomness or culture.</remarks>
    public static CcrPreview? Build(string original, CcrOptions options)
    {
        if (options.TokensOf(original.Length) < options.MinimumTokens
            || original.Contains(Marker, StringComparison.Ordinal))
        {
            return null;
        }

        var id = IdOf(original);
        var lines = LinesOf(original);
        var total = lines.Length;
        var budget = options.CharactersOf(options.BudgetTokens);
        var gapCost = Gap(total, total).Length + 1;
        var overlayRoom = (int)(budget * options.OverlayShare);
        var room = budget - (Header(id, total, total).Length + 1) - overlayRoom - gapCost;
        if (room <= 0)
        {
            return null;
        }

        var shown = new bool[total];
        var signalRoom = (int)(room * options.SignalShare);
        var spent = 0;
        for (var index = 0; index < total; index++)
        {
            var cost = lines[index].Length + 1 + gapCost;
            if (spent + cost <= signalRoom && MustKeepOverlay.CarriesSignal(lines[index]))
            {
                shown[index] = true;
                spent += cost;
            }
        }

        var rest = room - spent;
        var headRoom = (int)(rest * options.HeadShare);
        var headSpent = Take(lines, shown, headRoom, Enumerable.Range(0, total));

        string? cut = null;
        if (headSpent == 0 && !shown[0] && lines[0].Length > budget)
        {
            var length = headRoom - (Cut(lines[0].Length, lines[0].Length).Length + 2);
            if (length > 0 && char.IsHighSurrogate(lines[0][length - 1]))
            {
                length--;
            }

            if (length > 0)
            {
                cut = lines[0][..length];
                shown[0] = true;
                headSpent = headRoom;
            }
        }

        Take(lines, shown, rest - headSpent, Enumerable.Range(0, total).Reverse());

        var body = new List<string>();
        var hidden = new StringBuilder();
        if (cut is not null)
        {
            hidden.Append(lines[0], cut.Length, lines[0].Length - cut.Length).Append('\n');
        }

        for (var index = 0; index < total;)
        {
            if (shown[index])
            {
                body.Add(index == 0 && cut is not null ? cut : lines[index]);
                if (index == 0 && cut is not null)
                {
                    body.Add(Cut(cut.Length, lines[0].Length));
                }

                index++;
                continue;
            }

            var end = index;
            while (end < total && !shown[end])
            {
                hidden.Append(lines[end]).Append('\n');
                end++;
            }

            body.Add(Gap(index + 1, end));
            index = end;
        }

        if (hidden.Length == 0)
        {
            return null;
        }

        var shownCount = shown.Count(line => line);
        var text = Header(id, shownCount, total) + "\n" + string.Join("\n", body);
        return new CcrPreview(id, text + Overlay(text, hidden.ToString(), overlayRoom), shownCount, total);
    }

    /// <summary>The lines of a text, split on line feeds; a final line feed does not start another line.</summary>
    internal static string[] LinesOf(string text)
    {
        if (text.Length == 0)
        {
            return [];
        }

        var lines = text.Split('\n');
        return text.EndsWith('\n') ? lines[..^1] : lines;
    }

    // Head and tail lines extend a block that touches an edge or a kept line, so they never open a new gap.
    private static int Take(string[] lines, bool[] shown, int room, IEnumerable<int> order)
    {
        var spent = 0;
        foreach (var index in order)
        {
            if (shown[index])
            {
                continue;
            }

            var cost = lines[index].Length + 1;
            if (spent + cost > room)
            {
                break;
            }

            shown[index] = true;
            spent += cost;
        }

        return spent;
    }

    private static string Overlay(string shownText, string hiddenText, int room)
    {
        var known = new HashSet<string>(MustKeepOverlay.Identifiers(shownText), StringComparer.OrdinalIgnoreCase);
        var block = new StringBuilder(MustKeepOverlay.BlockLabel);
        var count = 0;

        foreach (var token in MustKeepOverlay.Identifiers(hiddenText))
        {
            if (known.Contains(token))
            {
                continue;
            }

            var added = token.Length + (count > 0 ? 1 : 0);
            if (block.Length + added > room)
            {
                break;
            }

            block.Append(count > 0 ? " " : "").Append(token);
            count++;
        }

        return count == 0 ? "" : block.ToString();
    }

    private static string Header(string id, int shown, int total) => string.Create(
        CultureInfo.InvariantCulture,
        $"{Marker}{id}: {shown} of {total} lines shown. Call proxy_retrieve with this id for a line range, a pattern, or the whole text.]");

    private static string Gap(int first, int last) =>
        string.Create(CultureInfo.InvariantCulture, $"[lines {first}-{last} not shown]");

    private static string Cut(int at, int length) =>
        string.Create(CultureInfo.InvariantCulture, $"[line 1 cut at character {at} of {length}]");
}
