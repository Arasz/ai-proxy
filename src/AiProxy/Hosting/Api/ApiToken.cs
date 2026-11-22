using System.Security.Cryptography;
using System.Text;
using AiProxy.Core.Doctor;

namespace AiProxy.Hosting.Api;

/// <summary>The bearer token every loopback API route but <c>/health</c> requires; the proxy makes a new one at each start and keeps it in its data directory.</summary>
public sealed class ApiToken(string value)
{
    /// <summary>The token file's name inside the data directory.</summary>
    public const string FileName = "api-token";

    private const string Scheme = "Bearer ";

    private readonly byte[] _expected = Encoding.UTF8.GetBytes(Scheme + value);

    /// <summary>The token text a client sends after <c>Bearer</c>.</summary>
    public string Value => value;

    /// <summary>A new token from the platform's cryptographic random source.</summary>
    public static ApiToken Create()
    {
        return new ApiToken(Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)));
    }

    /// <summary>The token a running proxy wrote to <paramref name="dataDirectory" />, or null when there is none.</summary>
    public static string? Read(string dataDirectory)
    {
        try
        {
            var text = File.ReadAllText(Path.Combine(dataDirectory, FileName)).Trim();
            return text.Length == 0 ? null : text;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    /// <summary>Replaces the token file in <paramref name="dataDirectory" />; on POSIX only the owner can read it.</summary>
    public void Write(string dataDirectory)
    {
        var path = Path.Combine(dataDirectory, FileName);
        var temporary = $"{path}.{Guid.CreateVersion7():N}.tmp";
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using (var writer = new StreamWriter(temporary, Encoding.ASCII, options))
        {
            writer.Write(value);
        }

        File.Move(temporary, path, true);
    }

    /// <summary>True when <paramref name="authorization" /> is <c>Bearer</c> and this token; compares in constant time.</summary>
    public bool Accepts(string? authorization)
    {
        return authorization is not null && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(authorization), _expected);
    }

    /// <summary>The API middleware: answers 401 to a request without this token, except <c>/health</c>.</summary>
    public Task CheckAsync(HttpContext context, RequestDelegate next)
    {
        if (context.Request.Path.Equals(HealthApi.Path, StringComparison.OrdinalIgnoreCase) || Accepts(context.Request.Headers.Authorization))
        {
            return next(context);
        }

        context.Response.Headers.WWWAuthenticate = "Bearer";
        return Results.Problem(
            title: "missing_api_token",
            detail: $"Send the token from {FileName} in the proxy's data directory as a Bearer token. Clients find it through --data-directory, then Proxy__DataDirectory, then ~/.ai-proxy.",
            statusCode: StatusCodes.Status401Unauthorized).ExecuteAsync(context);
    }
}
