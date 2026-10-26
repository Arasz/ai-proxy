namespace AiProxy.Core.Content;

/// <summary>The names a <see cref="ContentType" /> goes by in a template.</summary>
public static class ContentTypeExtensions
{
    /// <param name="type">The kind of the part.</param>
    extension(ContentType type)
    {
        /// <summary>The short name of the kind, as it appears in a placeholder.</summary>
        public string ShortName =>
            type switch
            {
                ContentType.Prose => "prose",
                ContentType.Log => "log",
                ContentType.Json => "json",
                ContentType.Xml => "xml",
                ContentType.Toml => "toml",
                ContentType.Markdown => "markdown",
                ContentType.SourceCode => "code",
                ContentType.DirectoryListing => "listing",
                ContentType.ShellTranscript => "shell",
                _ => "unknown"
            };

        /// <summary>The placeholder that holds a part's place in the template.</summary>
        /// <param name="chunkId">The part's number within the request.</param>
        public string Placeholder(int chunkId)
        {
            return $"{{{type.ShortName}_{chunkId}}}";
        }
    }
}