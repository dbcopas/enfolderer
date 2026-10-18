using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Enfolderer.Ai.Contracts;
using Enfolderer.Ai.Infrastructure.Queueing;

namespace Enfolderer.Ai.Infrastructure.Allowances;

/// <summary>Process-local development store; intentionally not durable across process restarts.</summary>
public sealed class InMemoryScanAllowanceStore : IScanAllowanceStore
{
    private sealed class Account(string ownerKey)
    {
        internal ScanAllowance Allowance = AllowanceRules.Empty(ownerKey);
        internal readonly Dictionary<string, ScanDispatchReceipt> Receipts = new(StringComparer.Ordinal);
        internal readonly Dictionary<Guid, ScanAllowanceAdjustment> Adjustments = [];
    }

    private readonly ConcurrentDictionary<string, Account> _accounts = new(StringComparer.Ordinal);

    private Account AccountFor(string ownerKey, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ScanAllowanceOwner.Validate(ownerKey);
        return _accounts.GetOrAdd(ownerKey, key => new Account(key));
    }

    public Task<ScanAllowance> GetAsync(string ownerKey, CancellationToken ct = default)
    {
        var account = AccountFor(ownerKey, ct);
        lock (account) return Task.FromResult(account.Allowance);
    }

    public Task<ScanReservationResult> ReserveAsync(string ownerKey, ScanJobMessage message, CancellationToken ct = default)
    {
        AllowanceRules.ValidateMessage(message);
        var account = AccountFor(ownerKey, ct);
        lock (account)
        {
            ct.ThrowIfCancellationRequested();
            if (account.Receipts.TryGetValue(message.JobId, out var existing))
            {
                AllowanceRules.CheckMessage(existing, message);
                return Task.FromResult(new ScanReservationResult(ScanReservationOutcome.AlreadyReserved, account.Allowance, existing));
            }
            if (AllowanceRules.Denial(account.Allowance) is { } denial)
                return Task.FromResult(new ScanReservationResult(denial, account.Allowance, null));
            var receipt = new ScanDispatchReceipt(ownerKey, message, DateTimeOffset.UtcNow);
            var allowance = AllowanceRules.Charge(account.Allowance, receipt.CreatedAt);
            account.Receipts.Add(message.JobId, receipt);
            account.Allowance = allowance;
            return Task.FromResult(new ScanReservationResult(ScanReservationOutcome.Reserved, allowance, receipt));
        }
    }

    public Task<ScanAllowanceAdjustmentResult> AdjustAsync(string ownerKey, Guid operationId, long delta,
        bool unlimited, string actor, string reason, CancellationToken ct = default)
    {
        AllowanceRules.ValidateAdjustment(operationId, actor, reason);
        var account = AccountFor(ownerKey, ct);
        lock (account)
        {
            ct.ThrowIfCancellationRequested();
            if (account.Adjustments.TryGetValue(operationId, out var existing))
            {
                AllowanceRules.CheckReplay(existing, delta, unlimited, actor, reason);
                return Task.FromResult(new ScanAllowanceAdjustmentResult(existing, true));
            }
            var now = DateTimeOffset.UtcNow;
            var allowance = AllowanceRules.Apply(account.Allowance, delta, unlimited, now);
            var audit = new ScanAllowanceAdjustment(operationId, delta, unlimited, actor, reason, now, allowance);
            account.Adjustments.Add(operationId, audit);
            account.Allowance = allowance;
            return Task.FromResult(new ScanAllowanceAdjustmentResult(audit, false));
        }
    }

    public async IAsyncEnumerable<ScanDispatchReceipt> ListPendingAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        foreach (var account in _accounts.Values)
        {
            ct.ThrowIfCancellationRequested();
            ScanDispatchReceipt[] pending;
            lock (account) pending = account.Receipts.Values.Where(r => r.DispatchedAt is null).ToArray();
            foreach (var receipt in pending)
            {
                ct.ThrowIfCancellationRequested();
                yield return receipt;
            }
        }
        await Task.CompletedTask;
    }

    public Task<bool> MarkDispatchedAsync(string ownerKey, string jobId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        var account = AccountFor(ownerKey, ct);
        lock (account)
        {
            ct.ThrowIfCancellationRequested();
            if (!account.Receipts.TryGetValue(jobId, out var receipt)) return Task.FromResult(false);
            if (receipt.DispatchedAt is null)
                account.Receipts[jobId] = receipt with { DispatchedAt = DateTimeOffset.UtcNow };
            return Task.FromResult(true);
        }
    }
}
