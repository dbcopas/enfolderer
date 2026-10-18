namespace Enfolderer.Ai.Contracts;

/// <summary>
/// An explicit grant for one authenticated tenant/object identity. Missing accounts are represented
/// by Granted=false, zero credits, and Unlimited=false. Credits remain banked while unlimited.
/// </summary>
public sealed record ScanAllowance
{
    public required string OwnerKey { get; init; }
    public bool Granted { get; init; }
    public long CreditsRemaining { get; init; }
    public bool Unlimited { get; init; }
    public long TotalReserved { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
}

public enum ScanReservationOutcome
{
    Reserved,
    AlreadyReserved,
    Ungranted,
    Exhausted
}

public sealed record ScanAllowanceAdjustment(
    Guid OperationId,
    long Delta,
    bool Unlimited,
    string Actor,
    string Reason,
    DateTimeOffset CreatedAt,
    ScanAllowance Allowance);

/// <summary>Replays return the original adjustment, not the account's possibly newer balance.</summary>
public sealed record ScanAllowanceAdjustmentResult(ScanAllowanceAdjustment Adjustment, bool AlreadyApplied);
