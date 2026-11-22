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

    /// <summary>How a client finds the data directory, for messages that tell a person where to look.</summary>
    public const string DirectoryRule = "Clients find the data directory from --data-directory, then Proxy__DataDirectory, then ~/.ai-proxy; point it at the directory the running proxy uses.";

    private readonly byte[] _expected = Encoding.UTF8.GetBytes(value);

    /// <summary>The token text a client sends after <c>Bearer</c>.</summary>
    public string Value => value;

    /// <summary>A new token from the platform's cryptographic random source.</summary>
    public static ApiToken Create()
    {
        return new ApiToken(Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)));
    }

    /// <summary>The token a running proxy wrote to <paramref name="dataDirectory" />, or null when there is none; an unreadable file is an <see cref="IOException" /> that names the file.</summary>
    public static string? Read(string dataDirectory)
    {
        var path = Path.Combine(dataDirectory, FileName);
        try
        {
            var text = File.ReadAllText(path).Trim();
            return text.Length == 0 ? null : text;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            throw new IOException($"Cannot read the API token file {path} ({exception.GetType().Name}). Check its owner and mode; the proxy writes it for the user that runs it.", exception);
        }
    }

    /// <summary>What to tell a person whose request the API refused with 401: which token file the client read, or that it found none.</summary>
    public static string Refused(string tokenFile, bool found)
    {
        var what = found ? $"The proxy refused the API token from {tokenFile} (HTTP 401)." : $"The proxy needs an API token (HTTP 401), and there is no API token at {tokenFile}.";
        return $"{what} {DirectoryRule}";
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

    /// <summary>True when <paramref name="authorization" /> is <c>Bearer</c>, in any case, and this token; compares the token in constant time.</summary>
    public bool Accepts(string? authorization)
    {
        return authorization is not null
               && authorization.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase)
               && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(authorization[Scheme.Length..]), _expected);
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
