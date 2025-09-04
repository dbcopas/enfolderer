using System;
using System.Collections.Generic;
using Enfolderer.App.Core;
using Enfolderer.App.Imaging;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

using Enfolderer.App.Layout;
using Enfolderer.App.Metadata;
using Enfolderer.App.Core.Abstractions;
namespace Enfolderer.App.Binder;

public record BinderLoadResult(
    string FileHash,
    string? CollectionDir,
    int? PagesPerBinderOverride,
    string? LayoutModeOverride,
    bool HttpDebugEnabled,
    string? LocalBackImagePath,
    bool CacheHit,
    List<CardEntry> CachedCards,
    List<BinderParsedSpec> Specs,
    List<(string set,string baseNum,string variantNum)> PendingVariantPairs,
    List<(string setCode,string number,string? nameOverride,int specIndex)> InitialFetchList,
    HashSet<int> InitialSpecIndexes
);

/// <summary>
/// Orchestrates the initial binder file load (parse + early spec resolution dispatch preparation).
/// ViewModel supplies services and then applies results to its own state.
/// </summary>
public class BinderLoadService
{
    private readonly BinderThemeService _binderTheme;
    private readonly ICardMetadataResolver _metadataResolver;
    private readonly CardMetadataResolverAdapter? _adapter; // transitional access
    private readonly CardBackImageService _backImageService;
    private readonly Func<string, bool> _isCacheComplete;

    public BinderLoadService(BinderThemeService binderTheme,
                             ICardMetadataResolver metadataResolver,
                             CardBackImageService backImageService,
                             Func<string,bool> isCacheComplete)
    { _binderTheme = binderTheme; _metadataResolver = metadataResolver; _backImageService = backImageService; _isCacheComplete = isCacheComplete; _adapter = metadataResolver as CardMetadataResolverAdapter; }

    public async Task<BinderLoadResult> LoadAsync(string path, int slotsPerPage)
    {
        try { var fi = new FileInfo(path); CardSlotTheme.Recalculate(path + fi.LastWriteTimeUtc.Ticks); } catch { CardSlotTheme.Recalculate(path); }
    var concrete = _adapter?.Inner as CardMetadataResolver ?? throw new InvalidOperationException("CardMetadataResolverAdapter expected");
    var parser = new BinderFileParser(_binderTheme, concrete, log => _backImageService.Resolve(null, log), _isCacheComplete);
        var parseResult = await parser.ParseAsync(path, slotsPerPage);
        return new BinderLoadResult(
            parseResult.FileHash,
            parseResult.CollectionDir,
            parseResult.PagesPerBinderOverride,
            parseResult.LayoutModeOverride,
            parseResult.HttpDebugEnabled,
            parseResult.LocalBackImagePath,
            parseResult.CacheHit,
            parseResult.CachedCards,
            parseResult.Specs,
            parseResult.PendingVariantPairs,
            parseResult.FetchList,
            parseResult.InitialSpecIndexes
        );
    }
}
