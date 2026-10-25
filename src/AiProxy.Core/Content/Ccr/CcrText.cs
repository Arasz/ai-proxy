using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace AiProxy.Core.Content.Ccr;

internal static class CcrText
{
    public static string IdOf(string original) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(original)))[..16];

    public static string[] LinesOf(string text)
    {
        if (text.Length == 0)
        {
            return [];
        }

        var lines = text.Split('\n');
        return text.EndsWith('\n') ? lines[..^1] : lines;
    }

    public static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
