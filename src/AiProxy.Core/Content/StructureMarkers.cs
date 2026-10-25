namespace AiProxy.Core.Content;

/// <summary>The characters and strings that mark the structure the decompositor looks for.</summary>
internal static class StructureMarkers
{
    /// <summary>The end of a line.</summary>
    public const char NewLine = '\n';

    /// <summary>The character that separates a file name from its extension.</summary>
    public const char ExtensionSeparator = '.';

    /// <summary>The marker of a fenced block.</summary>
    public static class Fence
    {
        public const string Backticks = "```";
    }

    /// <summary>The characters a JSON value is made of.</summary>
    public static class Json
    {
        public const char ObjectOpen = '{';
        public const char ObjectClose = '}';
        public const char ArrayOpen = '[';
        public const char ArrayClose = ']';
        public const char Quote = '"';
        public const char Escape = '\\';
    }

    /// <summary>The characters and strings an XML element is made of.</summary>
    public static class Xml
    {
        public const char TagOpen = '<';
        public const char TagClose = '>';
        public const char SelfClosingSlash = '/';
        public const string CommentOpen = "<!--";
        public const string ClosingTagOpen = "</";
    }

    /// <summary>The marks of a shell transcript.</summary>
    public static class Shell
    {
        public const char Prompt = '$';
    }
}
