using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Enfolderer.App.Importing;

namespace Enfolderer.App.Core.Abstractions;

/// <summary>
/// Abstraction for set import operations (Scryfall) so UI may trigger imports without concrete dependencies.
/// </summary>
public interface IImportService
{
    Task<ScryfallSetImporter.ImportSummary> ImportSetAsync(string setCode, bool forceReimport, string dbPath, Action<string>? statusCallback, CancellationToken ct = default);
    Task<AutoImportMissingSetsService.AutoImportResult> AutoImportMissingAsync(HashSet<string> binderSetCodes, string dbPath, bool confirm, Func<string, bool>? confirmPrompt, IStatusSink sink, CancellationToken ct = default);
}
