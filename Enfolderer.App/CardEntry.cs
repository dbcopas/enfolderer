using System;
using System.Text.RegularExpressions;

namespace Enfolderer.App;

public record CardEntry(string Name, string Number, string? Set, bool IsModalDoubleFaced, bool IsBackFace = false, string? FrontRaw = null, string? BackRaw = null, string? DisplayNumber = null, int Quantity = -1)
{
	public string EffectiveNumber => DisplayNumber ?? Number;
	public string Display => string.IsNullOrWhiteSpace(EffectiveNumber) ? Name : $"{EffectiveNumber} {Name}";
	public static CardEntry FromCsv(string line)
	{
		if (string.IsNullOrWhiteSpace(line)) throw new ArgumentException("Empty line");
		var raw = line.Split(';');
		if (raw.Length < 2) throw new ArgumentException("Must have at least name;number");
		string name = raw[0].Trim();
		string number = raw[1].Trim();
		string? set = raw.Length >= 3 ? raw[2].Trim() : null;
		bool mfc = false;
		string? front = null; string? back = null;
		if (name.Contains("|MFC", StringComparison.OrdinalIgnoreCase) || name.Contains("|DFC", StringComparison.OrdinalIgnoreCase))
		{
			mfc = true;
			var markerIndex = name.LastIndexOf('|');
			var pairPart = name.Substring(0, markerIndex).Trim();
			var splitNames = pairPart.Split('/', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
			if (splitNames.Length == 2)
			{
				front = splitNames[0];
				back = splitNames[1];
				name = $"{front} ({back})"; // display rule
			}
			else
			{
				name = pairPart; // fallback
			}
		}
		else
		{
			for (int i = 3; i < raw.Length; i++)
			{
				var f = raw[i].Trim();
				if (string.Equals(f, "MFC", StringComparison.OrdinalIgnoreCase) || string.Equals(f, "DFC", StringComparison.OrdinalIgnoreCase))
					mfc = true;
			}
		}
		return new CardEntry(name, number, string.IsNullOrWhiteSpace(set) ? null : set, mfc, false, front, back);
	}
}

