using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Enfolderer.App.Core;
using Enfolderer.App.Core.Abstractions;
using Enfolderer.App.Metadata;

namespace Enfolderer.App.Tests;

/// <summary>
/// Characterization test: ensure metadata provider loads a synthetic cached face list.
/// </summary>
public static class MetadataProviderCacheTests
{
    public static int RunAll()
    {
        int failures = 0; void Check(bool c){ if(!c) failures++; }
        try
        {
            var cacheRoot = Path.Combine(Path.GetTempPath(), "enfolderer_provider_test_cache");
            Directory.CreateDirectory(cacheRoot);
            var resolver = new CardMetadataResolver(cacheRoot, new[]{"transform"}, 5);
            var adapter = new CardMetadataResolverAdapter(resolver);
            IMetadataProvider provider = new MetadataProviderAdapter(adapter);
            var hash = Guid.NewGuid().ToString("N");
            var metaDir = Path.Combine(cacheRoot, "meta");
            Directory.CreateDirectory(metaDir);
            var path = Path.Combine(metaDir, hash + ".json");
            var sample = new List<CardMetadataResolver.CachedFace>{
                new("Sample Card","123","setx",false,false,"FrontRaw","BackRaw","http://front","http://back","transform",5)
            };
            File.WriteAllText(path, JsonSerializer.Serialize(sample));
            var list = new List<CardEntry>();
            bool loaded = provider.TryLoadMetadata(hash, list);
            Check(loaded);
            Check(list.Count == 1);
            if (list.Count == 1)
            {
                Check(list[0].Name == "Sample Card");
                Check(list[0].Number == "123");
            }
        }
        catch { failures++; }
        return failures;
    }
}
