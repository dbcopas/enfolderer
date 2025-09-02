// Moved into AppRoot for organization
using System;
using System.Linq;

namespace Enfolderer.App;

internal static class ScryfallUrlHelper
{
    public static string BuildCardApiUrl(string setCode, string number)
    {
        if (string.IsNullOrWhiteSpace(setCode)) return string.Empty;
        setCode = setCode.ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(number)) return $"https://api.scryfall.com/cards/{setCode}";
        var segments = number.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                             .Select(s => Uri.EscapeDataString(s));
        return $"https://api.scryfall.com/cards/{setCode}/" + string.Join('/', segments);
    }
}