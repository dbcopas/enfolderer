using Enfolderer.Ai.Contracts;
using Enfolderer.Ai.Infrastructure.Queueing;

namespace Enfolderer.Ai.Infrastructure.Allowances;

internal static class AllowanceRules
{
    internal static ScanAllowance Empty(string ownerKey) => new() { OwnerKey = ownerKey };

    internal static void ValidateMessage(ScanJobMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentException.ThrowIfNullOrWhiteSpace(message.JobId);
        ArgumentException.ThrowIfNullOrWhiteSpace(message.BlobPath);
    }

    internal static void ValidateAdjustment(Guid operationId, string actor, string reason)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("An operation GUID is required.", nameof(operationId));
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
    }

    internal static void CheckReplay(ScanAllowanceAdjustment audit, long delta, bool unlimited, string actor, string reason)
    {
        if (audit.Delta != delta || audit.Unlimited != unlimited || audit.Actor != actor || audit.Reason != reason)
            throw new InvalidOperationException("The operation GUID was already used with a different adjustment.");
    }

    internal static ScanAllowance Apply(ScanAllowance current, long delta, bool unlimited, DateTimeOffset now)
    {
        var balance = checked(current.CreditsRemaining + delta);
        if (balance < 0) throw new InvalidOperationException("An adjustment cannot make scan credits negative.");
        return current with { Granted = true, CreditsRemaining = balance, Unlimited = unlimited, UpdatedAt = now };
    }

    internal static ScanReservationOutcome? Denial(ScanAllowance current) =>
        !current.Granted ? ScanReservationOutcome.Ungranted
        : !current.Unlimited && current.CreditsRemaining == 0 ? ScanReservationOutcome.Exhausted : null;

    internal static ScanAllowance Charge(ScanAllowance current, DateTimeOffset now) => current with
    {
        CreditsRemaining = current.Unlimited ? current.CreditsRemaining : checked(current.CreditsRemaining - 1),
        TotalReserved = checked(current.TotalReserved + 1),
        UpdatedAt = now
    };

    internal static void CheckMessage(ScanDispatchReceipt receipt, ScanJobMessage message)
    {
        if (receipt.Message != message)
            throw new InvalidOperationException("The job ID was already reserved with a different message.");
    }
}
