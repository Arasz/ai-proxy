namespace AiProxy.Core.Content;

/// <summary>The kind of content a part carries, which decides the reducer that runs on it.</summary>
public enum ContentType
{
    /// <summary>The kind could not be established, and nothing reduces it.</summary>
    Unknown,

    /// <summary>Running text with no structure to find.</summary>
    Prose,

    /// <summary>Lines that carry a timestamp and a level.</summary>
    Log,

    /// <summary>A JSON document.</summary>
    Json,

    /// <summary>An XML document.</summary>
    Xml,

    /// <summary>A TOML configuration file.</summary>
    Toml,

    /// <summary>A Markdown document.</summary>
    Markdown,

    /// <summary>The source of a program.</summary>
    SourceCode,

    /// <summary>The listing of a directory.</summary>
    DirectoryListing,

    /// <summary>The output of a shell command.</summary>
    ShellTranscript,
}
