namespace Barbaric.Core.Library;

/// <summary>Turns what the user typed into an FTS5 match expression.</summary>
public static class SearchText
{
    /// <summary>
    /// Every whitespace-separated word must match the start of a word in the title, artist,
    /// album, album artist, genre or file name. Returns null when there is nothing to search for.
    /// </summary>
    public static string? ToMatchExpression(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        // Quoting each word makes FTS treat operators and punctuation as plain text.
        // Pure-punctuation words would tokenize to nothing, which FTS rejects, so drop them.
        var terms = text
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Where(word => word.Any(char.IsLetterOrDigit))
            .Select(word => $"\"{word.Replace("\"", "\"\"")}\"*")
            .ToList();

        return terms.Count == 0 ? null : string.Join(" AND ", terms);
    }
}
