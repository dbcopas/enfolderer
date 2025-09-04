using Enfolderer.App.Core.Abstractions;

namespace Enfolderer.App.Core;

public interface IRuntimeFlagService
{
    bool QtyDebug { get; }
}

public sealed class RuntimeFlagService : IRuntimeFlagService
{
    private readonly IRuntimeFlags _flags;
    public RuntimeFlagService(IRuntimeFlags flags) { _flags = flags; }
    public bool QtyDebug => _flags.QtyDebug;
}