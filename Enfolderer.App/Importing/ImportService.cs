using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Enfolderer.App.Core.Abstractions;

namespace Enfolderer.App.Importing;

/// <summary>
/// Concrete implementation bridging legacy importer classes to unified abstraction.
/// </summary>
public sealed class ImportService : IImportService
{
    private readonly ScryfallSetImporter _setImporter = new();
    private readonly AutoImportMissingSetsService _autoImporter = new();

    public Task<ScryfallSetImporter.ImportSummary> ImportSetAsync(string setCode, bool forceReimport, string dbPath, Action<string>? statusCallback, CancellationToken ct = default)
        => _setImporter.ImportAsync(setCode, forceReimport, dbPath, statusCallback, ct);

    public Task<AutoImportMissingSetsService.AutoImportResult> AutoImportMissingAsync(HashSet<string> binderSetCodes, string dbPath, bool confirm, Func<string, bool>? confirmPrompt, IStatusSink sink, CancellationToken ct = default)
        => _autoImporter.AutoImportAsync(binderSetCodes, dbPath, null, confirm, confirmPrompt, sink);
}
