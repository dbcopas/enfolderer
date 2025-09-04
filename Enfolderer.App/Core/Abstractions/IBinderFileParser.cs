using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Enfolderer.App.Metadata;

namespace Enfolderer.App.Core.Abstractions;

/// <summary>
/// Abstraction over binder file parsing to decouple downstream code from concrete implementation.
/// </summary>
public interface IBinderFileParser
{
    Task<BinderParseResult> ParseAsync(string path, int slotsPerPage, CancellationToken ct = default);
}
