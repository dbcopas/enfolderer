using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Enfolderer.Ai.Contracts;
using Enfolderer.Ai.Infrastructure.Queueing;
using Microsoft.Azure.Cosmos;

namespace Enfolderer.Ai.Infrastructure.Allowances;

/// <summary>
/// Requires a dedicated Cosmos container partitioned by /ownerKey (not the jobs container).
/// All records explicitly disable TTL, even if container TTL is enabled. Provision the container
/// before constructing this store. Supports the default and SystemTextJsonCosmosSerializer.
/// </summary>
public sealed class CosmosScanAllowanceStore(Container container) : IScanAllowanceStore
{
    private const int Attempts = 12;
    private const string StateId = "allowance";

    // Explicit names for both supported serializers keep partition paths and recovery queries stable.
    private sealed record Document
    {
        [JsonPropertyName("id"), Newtonsoft.Json.JsonProperty("id")]
        public required string Id { get; init; }
        [JsonPropertyName("ownerKey"), Newtonsoft.Json.JsonProperty("ownerKey")]
        public required string OwnerKey { get; init; }
        [JsonPropertyName("kind"), Newtonsoft.Json.JsonProperty("kind")]
        public required string Kind { get; init; }
        [JsonPropertyName("ttl"), Newtonsoft.Json.JsonProperty("ttl")]
        public int Ttl { get; init; } = -1;
        [JsonPropertyName("pending"), Newtonsoft.Json.JsonProperty("pending")]
        public bool Pending { get; init; }
        public ScanAllowance? Allowance { get; init; }
        public ScanDispatchReceipt? Receipt { get; init; }
        public ScanAllowanceAdjustment? Adjustment { get; init; }
    }

    private static string ReceiptId(string jobId) =>
        "job-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(jobId)));

    private static string AuditId(Guid operationId) => $"adjustment-{operationId:D}";

    private async Task<ItemResponse<Document>?> ReadAsync(string ownerKey, string id, CancellationToken ct)
    {
        try
        {
            return await container.ReadItemAsync<Document>(id, new PartitionKey(ownerKey), cancellationToken: ct);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<ScanAllowance> GetAsync(string ownerKey, CancellationToken ct = default)
    {
        ScanAllowanceOwner.Validate(ownerKey);
        ct.ThrowIfCancellationRequested();
        var state = await ReadAsync(ownerKey, StateId, ct);
        return state is null ? AllowanceRules.Empty(ownerKey) : State(state);
    }

    public async Task<ScanReservationResult> ReserveAsync(string ownerKey, ScanJobMessage message, CancellationToken ct = default)
    {
        ScanAllowanceOwner.Validate(ownerKey);
        AllowanceRules.ValidateMessage(message);
        var createdAt = DateTimeOffset.UtcNow;
        var receiptId = ReceiptId(message.JobId);
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var existing = await ReadAsync(ownerKey, receiptId, ct);
            if (existing is not null)
            {
                var receipt = Receipt(existing);
                AllowanceRules.CheckMessage(receipt, message);
                return new(ScanReservationOutcome.AlreadyReserved, await GetAsync(ownerKey, ct), receipt);
            }
            var state = await ReadAsync(ownerKey, StateId, ct);
            var current = state is null ? AllowanceRules.Empty(ownerKey) : State(state);
            if (AllowanceRules.Denial(current) is { } denial)
            {
                // A concurrent reservation may have consumed the last credit after our first read.
                existing = await ReadAsync(ownerKey, receiptId, ct);
                if (existing is not null)
                {
                    var existingReceipt = Receipt(existing);
                    AllowanceRules.CheckMessage(existingReceipt, message);
                    return new(ScanReservationOutcome.AlreadyReserved, current, existingReceipt);
                }
                return new(denial, current, null);
            }
            var updated = AllowanceRules.Charge(current, createdAt);
            var reserved = new ScanDispatchReceipt(ownerKey, message, createdAt);
            var batch = WriteState(container.CreateTransactionalBatch(new PartitionKey(ownerKey)), state, updated)
                .CreateItem(new Document
                {
                    Id = receiptId, OwnerKey = ownerKey, Kind = "receipt", Pending = true, Receipt = reserved
                });
            using var response = await batch.ExecuteAsync(ct);
            if (response.IsSuccessStatusCode) return new(ScanReservationOutcome.Reserved, updated, reserved);
            CheckFailure(response);
            await BackoffAsync(attempt, ct);
        }
        throw Contention(ownerKey);
    }

    public async Task<ScanAllowanceAdjustmentResult> AdjustAsync(string ownerKey, Guid operationId, long delta,
        bool unlimited, string actor, string reason, CancellationToken ct = default)
    {
        ScanAllowanceOwner.Validate(ownerKey);
        AllowanceRules.ValidateAdjustment(operationId, actor, reason);
        var createdAt = DateTimeOffset.UtcNow;
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var existing = await ReadAsync(ownerKey, AuditId(operationId), ct);
            if (existing is not null)
            {
                var audit = existing.Resource.Adjustment
                    ?? throw new InvalidDataException("Allowance audit is missing its adjustment.");
                AllowanceRules.CheckReplay(audit, delta, unlimited, actor, reason);
                return new(audit, true);
            }
            var state = await ReadAsync(ownerKey, StateId, ct);
            var current = state is null ? AllowanceRules.Empty(ownerKey) : State(state);
            // Check a concurrently committed replay before reporting a now-invalid debit/overflow.
            if (delta < -current.CreditsRemaining || delta > long.MaxValue - current.CreditsRemaining)
            {
                existing = await ReadAsync(ownerKey, AuditId(operationId), ct);
                if (existing is not null)
                {
                    var prior = existing.Resource.Adjustment
                        ?? throw new InvalidDataException("Allowance audit is missing its adjustment.");
                    AllowanceRules.CheckReplay(prior, delta, unlimited, actor, reason);
                    return new(prior, true);
                }
            }
            var updated = AllowanceRules.Apply(current, delta, unlimited, createdAt);
            var adjustment = new ScanAllowanceAdjustment(operationId, delta, unlimited, actor, reason, createdAt, updated);
            var batch = WriteState(container.CreateTransactionalBatch(new PartitionKey(ownerKey)), state, updated)
                .CreateItem(new Document
                {
                    Id = AuditId(operationId), OwnerKey = ownerKey, Kind = "adjustment", Adjustment = adjustment
                });
            using var response = await batch.ExecuteAsync(ct);
            if (response.IsSuccessStatusCode) return new(adjustment, false);
            CheckFailure(response);
            await BackoffAsync(attempt, ct);
        }
        throw Contention(ownerKey);
    }

    public async IAsyncEnumerable<ScanDispatchReceipt> ListPendingAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var iterator = container.GetItemQueryIterator<Document>(
            new QueryDefinition("SELECT * FROM c WHERE c.kind = 'receipt' AND c.pending = true"),
            requestOptions: new QueryRequestOptions { MaxItemCount = 100 });
        while (iterator.HasMoreResults)
        {
            ct.ThrowIfCancellationRequested();
            var page = await iterator.ReadNextAsync(ct);
            foreach (var document in page)
            {
                ct.ThrowIfCancellationRequested();
                yield return document.Receipt
                    ?? throw new InvalidDataException("Pending allowance receipt is missing its payload.");
            }
        }
    }

    public async Task<bool> MarkDispatchedAsync(string ownerKey, string jobId, CancellationToken ct = default)
    {
        ScanAllowanceOwner.Validate(ownerKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        var id = ReceiptId(jobId);
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var existing = await ReadAsync(ownerKey, id, ct);
            if (existing is null) return false;
            var receipt = Receipt(existing);
            if (receipt.Message.JobId != jobId) throw new InvalidDataException("Receipt job identity mismatch.");
            if (receipt.DispatchedAt is not null) return true;
            var updated = existing.Resource with
            {
                Pending = false, Receipt = receipt with { DispatchedAt = DateTimeOffset.UtcNow }
            };
            using var batch = await container.CreateTransactionalBatch(new PartitionKey(ownerKey))
                .ReplaceItem(id, updated, new TransactionalBatchItemRequestOptions { IfMatchEtag = existing.ETag })
                .ExecuteAsync(ct);
            if (batch.IsSuccessStatusCode) return true;
            CheckFailure(batch);
            await BackoffAsync(attempt, ct);
        }
        throw Contention(ownerKey);
    }

    private static ScanAllowance State(ItemResponse<Document> response) => response.Resource.Allowance
        ?? throw new InvalidDataException("Allowance document is missing its balance.");

    private static ScanDispatchReceipt Receipt(ItemResponse<Document> response) => response.Resource.Receipt
        ?? throw new InvalidDataException("Allowance receipt is missing its payload.");

    private static TransactionalBatch WriteState(TransactionalBatch batch, ItemResponse<Document>? previous, ScanAllowance allowance)
    {
        var document = new Document { Id = StateId, OwnerKey = allowance.OwnerKey, Kind = "allowance", Allowance = allowance };
        return previous is null ? batch.CreateItem(document)
            : batch.ReplaceItem(StateId, document, new TransactionalBatchItemRequestOptions { IfMatchEtag = previous.ETag });
    }

    private static void CheckFailure(TransactionalBatchResponse response)
    {
        // FailedDependency is only a symptom; inspect the operation that actually failed.
        var contention = false;
        foreach (var operation in response)
        {
            var status = operation.StatusCode;
            if ((int)status < 400 || status == HttpStatusCode.FailedDependency) continue;
            if (status is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed)
            {
                contention = true;
                continue;
            }
            throw new CosmosException(response.ErrorMessage ?? "Allowance transaction failed.", status, 0,
                response.ActivityId, response.RequestCharge);
        }
        if (!contention || response.StatusCode is not (HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed or HttpStatusCode.FailedDependency))
            throw new CosmosException(response.ErrorMessage ?? "Allowance transaction failed.", response.StatusCode, 0,
                response.ActivityId, response.RequestCharge);
    }

    private static Task BackoffAsync(int attempt, CancellationToken ct) =>
        Task.Delay(TimeSpan.FromMilliseconds(Math.Min(200, 5 * (attempt + 1)) + Random.Shared.Next(10)), ct);

    private static InvalidOperationException Contention(string ownerKey) =>
        new($"Scan allowance contention retry limit ({Attempts}) exceeded for {ownerKey}.");
}
