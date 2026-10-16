using System.Text;

namespace Enfolderer.Ai.Worker.Agents;

/// <summary>
/// Helpers for reading JSON out of agent replies. Models routinely wrap JSON in markdown fences or
/// add a sentence of prose despite instructions, so the extraction has to be forgiving.
/// </summary>
public static class AgentJson
{
    /// <summary>
    /// Returns the outermost balanced JSON object found in <paramref name="reply"/>, stripping
    /// markdown fences and surrounding prose. Throws when no object is present.
    /// </summary>
    public static string ExtractJsonObject(string? reply)
    {
        if (string.IsNullOrWhiteSpace(reply))
            throw new InvalidOperationException("Agent reply was empty.");

        var text = reply.Trim();

        // Strip a leading ```json / ``` fence and its trailing counterpart.
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewline = text.IndexOf('\n');
            if (firstNewline >= 0) text = text[(firstNewline + 1)..];
            var closingFence = text.LastIndexOf("```", StringComparison.Ordinal);
            if (closingFence >= 0) text = text[..closingFence];
            text = text.Trim();
        }

        var start = text.IndexOf('{');
        if (start < 0)
            throw new InvalidOperationException("Agent reply did not contain a JSON object.");

        var depth = 0;
        var inString = false;
        var escaped = false;

        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];

            if (inString)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
                continue;
            }

            switch (c)
            {
                case '"': inString = true; break;
                case '{': depth++; break;
                case '}':
                    depth--;
                    if (depth == 0) return text[start..(i + 1)];
                    break;
            }
        }

        throw new InvalidOperationException("Agent reply contained an unterminated JSON object.");
    }

    /// <summary>Truncates an agent reply for safe inclusion in logs and error fields.</summary>
    public static string Summarize(string? reply, int maxLength = 300)
    {
        if (string.IsNullOrWhiteSpace(reply)) return string.Empty;
        var collapsed = new StringBuilder(reply.Length);
        var lastWasSpace = false;
        foreach (var c in reply)
        {
            var isSpace = char.IsWhiteSpace(c);
            if (isSpace && lastWasSpace) continue;
            collapsed.Append(isSpace ? ' ' : c);
            lastWasSpace = isSpace;
        }
        var text = collapsed.ToString().Trim();
        return text.Length <= maxLength ? text : text[..maxLength] + "…";
    }
}
