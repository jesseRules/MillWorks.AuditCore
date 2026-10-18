using System.Text;

namespace MillWorks.AuditCore.Services.Query;

/// <summary>Builds a literal SQL full-text condition: words are ANDed, quoted phrases stay together.</summary>
public static class AuditWordSearch
{
    public const int MaximumLength = 512;

    public static string? BuildCondition(string? text)
    {
        var terms = BuildTerms(text);
        return terms.Count == 0 ? null : string.Join(" AND ", terms);
    }

    internal static IReadOnlyList<string> BuildTerms(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        text = text.Trim();
        if (text.Length > MaximumLength)
            throw new ArgumentException($"Search must be at most {MaximumLength} characters.", nameof(text));

        var terms = new List<string>();
        var token = new StringBuilder();
        var quoted = false;
        foreach (var character in text)
        {
            if (character == '"')
            {
                Flush();
                quoted = !quoted;
            }
            else if (char.IsWhiteSpace(character) && !quoted) Flush();
            else token.Append(character);
        }
        if (quoted) throw new ArgumentException("Close the quoted search phrase.", nameof(text));
        Flush();
        if (terms.Count == 0) throw new ArgumentException("Enter a word or a quoted phrase.", nameof(text));
        return terms;

        void Flush()
        {
            var value = token.ToString().Trim();
            token.Clear();
            if (value.Length == 0) return;
            // Full-text grammar is generated here. Operators/wildcards from input are never executed.
            if (value.Contains('*') || !value.Any(char.IsLetterOrDigit))
                throw new ArgumentException("Use complete words or quoted phrases; wildcards are not supported.", nameof(text));
            terms.Add($"\"{value}\"");
        }
    }
}
