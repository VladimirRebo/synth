using System.Text;
using Synth.Domain;

namespace Synth.Core;

/// <summary>
/// Splits identifiers and free-text queries into lowercase word tokens, aware of
/// camelCase / PascalCase boundaries (<c>GetUserById</c> → <c>[get, user, by, id]</c>)
/// and acronyms (<c>HTMLParser</c> → <c>[html, parser]</c>), as well as the usual
/// non-alphanumeric delimiters (<c>_</c>, <c>.</c>, whitespace, …). Mirrors the
/// tokenization Sonar's reranker uses so keyword-boost matching lines up with how
/// developers actually name things rather than doing naive substring checks.
/// </summary>
public static class IdentifierTokenizer
{
    /// <summary>
    /// Returns the distinct-position word tokens of <paramref name="text"/>, lowercased.
    /// Empty input yields an empty list. Tokens are emitted in source order and may repeat.
    /// </summary>
    public static IReadOnlyList<string> Tokenize(string? text)
    {
        var tokens = new List<string>();
        if (string.IsNullOrEmpty(text))
            return tokens;

        var current = new StringBuilder();

        void Flush()
        {
            if (current.Length == 0)
                return;
            tokens.Add(current.ToString().ToLowerInvariant());
            current.Clear();
        }

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            // Any non-alphanumeric character (space, '_', '.', '/', …) ends the current token.
            if (!char.IsLetterOrDigit(c))
            {
                Flush();
                continue;
            }

            if (current.Length > 0 && char.IsUpper(c))
            {
                var prev = text[i - 1];
                var prevIsLowerOrDigit = char.IsLower(prev) || char.IsDigit(prev);
                var nextIsLower = i + 1 < text.Length && char.IsLower(text[i + 1]);

                // Boundary either at a lower→Upper transition ("getUser") or at the tail of
                // an acronym that runs into a new word ("HTMLParser" → "HTML" | "Parser").
                if (prevIsLowerOrDigit || (char.IsUpper(prev) && nextIsLower))
                    Flush();
            }

            current.Append(c);
        }

        Flush();
        return tokens;
    }
}
