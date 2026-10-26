namespace AiProxy.Core.Content;

/// <summary>Follows the characters of a JSON value and says which one closes it.</summary>
/// <remarks>A transient component: feed it the characters of one value, in order, from its first bracket.</remarks>
internal sealed class JsonValueScanner
{
    private int _depth;
    private bool _escaped;
    private bool _inString;

    /// <summary>Takes the next character, and says whether it closes the outermost value.</summary>
    /// <param name="character">The next character of the text.</param>
    public bool Closes(char character)
    {
        if (!_inString)
        {
            return ReadOutsideString(character);
        }

        // Nothing inside a string can close the value.
        ReadInsideString(character);

        return false;
    }

    private void ReadInsideString(char character)
    {
        if (_escaped)
        {
            _escaped = false;
        }
        else
        {
            switch (character)
            {
                case StructureMarkers.Json.Escape:
                    _escaped = true;
                    break;
                case StructureMarkers.Json.Quote:
                    _inString = false;
                    break;
            }
        }
    }

    private bool ReadOutsideString(char character)
    {
        switch (character)
        {
            case StructureMarkers.Json.Quote:
                _inString = true;
                return false;
            case StructureMarkers.Json.ObjectOpen:
            case StructureMarkers.Json.ArrayOpen:
                _depth++;
                return false;
            case StructureMarkers.Json.ObjectClose:
            case StructureMarkers.Json.ArrayClose:
                _depth--;
                return _depth == 0;
            default:
                return false;
        }
    }
}