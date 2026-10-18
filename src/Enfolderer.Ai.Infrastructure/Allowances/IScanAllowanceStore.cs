using Enfolderer.Ai.Contracts;
using Enfolderer.Ai.Infrastructure.Queueing;

namespace Enfolderer.Ai.Infrastructure.Allowances;

/// <summary>A durable outbox entry. CreatedAt is the original reservation time, never a retry time.</summary>
public sealed record ScanDispatchReceipt(
    string OwnerKey, ScanJobMessage Message, DateTimeOffset CreatedAt, DateTimeOffset? DispatchedAt = null);

public sealed record ScanReservationResult(
    ScanReservationOutcome Outcome, ScanAllowance Allowance, ScanDispatchReceipt? Receipt)
{
    public bool Allowed => Outcome is ScanReservationOutcome.Reserved or ScanReservationOutcome.AlreadyReserved;
}

/// <summary>
/// Owner keys must be made from validated authenticated tid+oid claims with ScanAllowanceOwner.Key.
/// No automatic grant is issued. Each unique job consumes one finite credit (none when unlimited);
/// successful reservations and dispatch receipts are permanent and atomic. Reusing a job with a
/// different message is an error. Delivery is at least once; consumers must deduplicate job IDs.
/// </summary>
public interface IScanAllowanceStore
{
    Task<ScanAllowance> GetAsync(string ownerKey, CancellationToken ct = default);
    Task<ScanReservationResult> ReserveAsync(string ownerKey, ScanJobMessage message, CancellationToken ct = default);

    /// <summary>
    /// First adjustment explicitly grants the account; delta is the initial balance. Later calls add
    /// delta (positive top-up/refund, negative administrative debit) and set the unlimited flag.
    /// Balances cannot become negative or overflow, including while unlimited. Operation IDs are
    /// unique per owner: exact replays return the original audit, changed payloads throw.
    /// Actor and reason are required audit text. The caller must enforce administrator authorization.
    /// </summary>
    Task<ScanAllowanceAdjustmentResult> AdjustAsync(string ownerKey, Guid operationId, long delta,
        bool unlimited, string actor, string reason, CancellationToken ct = default);

    /// <summary>
    /// Cross-owner recovery stream, oldest-first ordering is not guaranteed. Cosmos fetches at most
    /// 100 items per page and traverses all continuation pages, without an overall prefix limit.
    /// Recovery callers should handle individual delivery failures and continue enumeration rather
    /// than repeatedly taking only the first N entries (which can starve later entries).
    /// </summary>
    IAsyncEnumerable<ScanDispatchReceipt> ListPendingAsync(CancellationToken ct = default);

    /// <summary>
    /// Idempotently marks a known receipt delivered without deleting the receipt or reservation.
    /// Returns false only if the reservation does not exist. Mark only after queue acceptance.
    /// </summary>
    Task<bool> MarkDispatchedAsync(string ownerKey, string jobId, CancellationToken ct = default);
}

public static class ScanAllowanceOwner
{
    public static string Key(string tenantId, string objectId) =>
        $"{Parse(tenantId):D}:{Parse(objectId):D}";

    internal static void Validate(string ownerKey)
    {
        ArgumentNullException.ThrowIfNull(ownerKey);
        var parts = ownerKey.Split(':');
        if (parts.Length != 2 || Key(parts[0], parts[1]) != ownerKey)
            throw new ArgumentException("Use the canonical tenant/object key from ScanAllowanceOwner.Key.", nameof(ownerKey));
    }

    private static Guid Parse(string value) =>
        Guid.TryParse(value, out var id) && id != Guid.Empty ? id
            : throw new ArgumentException("Tenant and object identifiers must be nonempty GUIDs.");
}
