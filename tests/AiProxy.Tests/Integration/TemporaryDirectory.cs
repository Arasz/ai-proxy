namespace AiProxy.Tests.Integration;

/// <summary>A fresh directory under the system temp path, deleted on dispose.</summary>
internal sealed class TemporaryDirectory : IDisposable
{
    /// <summary>Creates the directory path; the directory itself is created by whoever uses it.</summary>
    public TemporaryDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ai-proxy-tests", Guid.CreateVersion7().ToString("N"));
    }

    /// <summary>The absolute directory path.</summary>
    public string Path { get; }

    /// <summary>Deletes the directory and everything in it.</summary>
    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
