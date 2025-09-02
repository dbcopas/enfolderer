using System.Collections.Generic;
using System.Collections.Concurrent;

namespace Enfolderer.App;

public class BinderSession
{
    public List<CardEntry> Cards { get; } = new();
    public List<CardEntry> OrderedFaces { get; } = new();
    public List<CardSpec> Specs { get; } = new();
    public ConcurrentDictionary<int, CardEntry> MfcBacks { get; } = new();
    public Dictionary<string,string> ExplicitVariantPairKeys { get; } = new(); // Set:Number -> pair key id
    public List<(string set,string baseNum,string variantNum)> PendingExplicitVariantPairs { get; } = new();
    public string? CurrentFileHash { get; set; }
    public string? LocalBackImagePath { get; set; }
}
