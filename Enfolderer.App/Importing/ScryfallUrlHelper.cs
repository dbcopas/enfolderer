// Moved into AppRoot for organization
using System;
using System.Linq;

namespace Enfolderer.App.Importing;

internal static class ScryfallUrlHelper
{
    public static string BuildCardApiUrl(string setCode, string number)
    {
        if (string.IsNullOrWhiteSpace(setCode)) return string.Empty;
        setCode = setCode.ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(number)) return $"https://api.scryfall.com/cards/{setCode}";
        // Normalize each number segment by removing internal whitespace so "5 J-b" -> "5J-b"
        static string NormalizeSegment(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            // Remove all Unicode whitespace characters within the segment
            var noWs = new string(s.Where(c => !char.IsWhiteSpace(c)).ToArray());
            return noWs;
        }
        var segments = number
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeSegment)
            .Select(s => Uri.EscapeDataString(s));
        return $"https://api.scryfall.com/cards/{setCode}/" + string.Join('/', segments);
    }
}