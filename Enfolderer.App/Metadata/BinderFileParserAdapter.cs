using System.Threading;
using System.Threading.Tasks;
using Enfolderer.App.Core.Abstractions;

namespace Enfolderer.App.Metadata;

/// <summary>
/// Adapter that exposes existing BinderFileParser via IBinderFileParser.
/// </summary>
public sealed class BinderFileParserAdapter : IBinderFileParser
{
    private readonly BinderFileParser _inner;
    public BinderFileParserAdapter(BinderFileParser inner) { _inner = inner; }
    public Task<BinderParseResult> ParseAsync(string path, int slotsPerPage, CancellationToken ct = default) => _inner.ParseAsync(path, slotsPerPage, ct);
}
