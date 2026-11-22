using System.Globalization;
using AiProxy.Core.Capture;

namespace AiProxy.Cli;

/// <summary>Plain-text wording for a capture state, shared by <c>status</c> and <c>doctor</c>.</summary>
internal static class CaptureText
{
    private const double BytesPerMebibyte = 1024 * 1024;

    public static string Size(CaptureState state)
    {
        return string.Create(CultureInfo.InvariantCulture, $"{state.LiveBytes / BytesPerMebibyte:F1} MiB of {state.CapBytes / BytesPerMebibyte:F1} MiB");
    }

    public static string Time(DateTimeOffset? at)
    {
        return at is { } value ? value.ToString("u", CultureInfo.InvariantCulture) : "none";
    }
}
