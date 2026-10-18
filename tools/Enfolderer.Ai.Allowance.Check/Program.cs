using System.Net;
using System.Reflection;
using System.Text.Json;
using Enfolderer.Ai.Contracts;
using Enfolderer.Ai.Infrastructure.Allowances;
using Enfolderer.Ai.Infrastructure.Queueing;
using Enfolderer.Ai.Infrastructure.Storage;
using Microsoft.Azure.Cosmos;

var checks = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    checks++;
}
void ThrowsStatus(Action action, HttpStatusCode expected)
{
    try { action(); }
    catch (CosmosException ex) when (ex.StatusCode == expected) { checks++; return; }
    throw new InvalidOperationException($"Expected Cosmos status {expected}.");
}
var storeType = typeof(CosmosScanAllowanceStore);
var checkFailure = storeType.GetMethod("CheckFailure", BindingFlags.Static | BindingFlags.NonPublic)!;
void CheckFailure(TransactionalBatchResponse response)
{
    try { checkFailure.Invoke(null, [response]); }
    catch (TargetInvocationException ex) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException!).Throw(); }
}
foreach (var conflict in new[] { HttpStatusCode.Conflict, HttpStatusCode.PreconditionFailed })
{
    foreach (var statuses in new[]
    {
        new[] { conflict, HttpStatusCode.FailedDependency },
        new[] { HttpStatusCode.FailedDependency, conflict }
    })
    {
        using var response = new BatchResponse(HttpStatusCode.FailedDependency, statuses);
        CheckFailure(response);
        checks++;
    }
}
foreach (var error in new[] { HttpStatusCode.Forbidden, HttpStatusCode.TooManyRequests, HttpStatusCode.BadRequest })
{
    using var response = new BatchResponse(HttpStatusCode.FailedDependency, HttpStatusCode.FailedDependency, error);
    ThrowsStatus(() => CheckFailure(response), error);
}
using (var response = new BatchResponse(HttpStatusCode.FailedDependency, HttpStatusCode.FailedDependency))
    ThrowsStatus(() => CheckFailure(response), HttpStatusCode.FailedDependency);
using (var response = new BatchResponse(HttpStatusCode.TooManyRequests, HttpStatusCode.PreconditionFailed))
    ThrowsStatus(() => CheckFailure(response), HttpStatusCode.TooManyRequests);

// Test the actual production batch builder and serialized private document, without Cosmos access.
var owner = ScanAllowanceOwner.Key(Guid.NewGuid().ToString(), Guid.NewGuid().ToString());
var allowance = new ScanAllowance { OwnerKey = owner, Granted = true, CreditsRemaining = 10 };
var documentType = storeType.GetNestedType("Document", BindingFlags.NonPublic)!;
var responseType = typeof(HeaderResponse<>).MakeGenericType(documentType);
var previous = Activator.CreateInstance(responseType, "\"revision-17\"")!;
var writeState = storeType.GetMethod("WriteState", BindingFlags.Static | BindingFlags.NonPublic)!;
var batch = new RecordingBatch();
writeState.Invoke(null, [batch, null, allowance]);
Check(batch.Operation == "create", "Initial grant must create, never upsert.");
writeState.Invoke(null, [batch, previous, allowance with { CreditsRemaining = 9, TotalReserved = 1 }]);
Check(batch.Operation == "replace" && batch.Id == "allowance", "Subsequent mutations must replace allowance.");
Check(batch.Options?.IfMatchEtag == "\"revision-17\"", "Revision must use SDK response header ETag.");
var serializer = new SystemTextJsonCosmosSerializer();
using (var stream = serializer.ToStream(batch.Payload))
using (var json = JsonDocument.Parse(stream))
{
    Check(json.RootElement.GetProperty("id").GetString() == "allowance", "Serialized id.");
    Check(json.RootElement.GetProperty("ownerKey").GetString() == owner, "Serialized partition key.");
    Check(json.RootElement.GetProperty("ttl").GetInt32() == -1, "Explicit TTL disable.");
    Check(json.RootElement.GetProperty("allowance").GetProperty("creditsRemaining").GetInt64() == 9, "Serialized counter.");
    Check(!json.RootElement.TryGetProperty("_etag", out _), "ETag belongs to SDK response headers, not document data.");
}
var defaultJson = Newtonsoft.Json.Linq.JObject.Parse(Newtonsoft.Json.JsonConvert.SerializeObject(batch.Payload));
Check(defaultJson.Value<string>("ownerKey") == owner && defaultJson.Value<int>("ttl") == -1,
    "Default Newtonsoft serializer partition/TTL compatibility.");

var now = DateTimeOffset.UtcNow;
var receipt = new ScanDispatchReceipt(owner, new ScanJobMessage("job", "blob.jpg", "mtg"), now);
var audit = new ScanAllowanceAdjustment(Guid.NewGuid(), 10, false, "admin", "initial", now, allowance);
foreach (var (kind, property, payload) in new (string, string, object)[] { ("receipt", "Receipt", receipt), ("adjustment", "Adjustment", audit) })
{
    var document = Activator.CreateInstance(documentType)!;
    documentType.GetProperty("Id")!.SetValue(document, kind);
    documentType.GetProperty("OwnerKey")!.SetValue(document, owner);
    documentType.GetProperty("Kind")!.SetValue(document, kind);
    documentType.GetProperty(property)!.SetValue(document, payload);
    using var stream = serializer.ToStream(document);
    using var json = JsonDocument.Parse(stream);
    Check(json.RootElement.GetProperty("ttl").GetInt32() == -1, $"{kind} never expires.");
    Check(json.RootElement.GetProperty("ownerKey").GetString() == owner, $"{kind} shares allowance partition.");
}

// Exercise public domain behavior concurrently; Cosmos uses the same arithmetic/replay rules.
var memory = new InMemoryScanAllowanceStore();
Check((await memory.ReserveAsync(owner, receipt.Message)).Outcome == ScanReservationOutcome.Ungranted, "No implicit grant.");
await memory.AdjustAsync(owner, audit.OperationId, 10, false, "admin", "initial");
var results = await Task.WhenAll(Enumerable.Range(0, 100).Select(i => Task.Run(() =>
    memory.ReserveAsync(owner, receipt.Message with { JobId = $"job-{i}" }))));
Check(results.Count(r => r.Outcome == ScanReservationOutcome.Reserved) == 10, "Concurrent finite reservations cannot overspend.");
Check((await memory.GetAsync(owner)).CreditsRemaining == 0, "Finite balance stays nonnegative.");
var accepted = results.First(r => r.Allowed).Receipt!;
await memory.MarkDispatchedAsync(owner, accepted.Message.JobId);
var replay = await memory.ReserveAsync(owner, accepted.Message);
Check(replay.Outcome == ScanReservationOutcome.AlreadyReserved && replay.Receipt!.DispatchedAt is not null,
    "Dispatch marking retains permanent reservation.");
Check(replay.Receipt!.CreatedAt == accepted.CreatedAt, "Replay preserves original creation time.");
var pending = 0;
await foreach (var _ in memory.ListPendingAsync()) pending++;
Check(pending == 9, "Pending recovery excludes marked receipts only.");
await memory.AdjustAsync(owner, Guid.NewGuid(), 2, true, "admin", "unlimited");
Check((await memory.ReserveAsync(owner, receipt.Message)).Allowed, "Unlimited reservation.");
Check((await memory.GetAsync(owner)).CreditsRemaining == 2, "Unlimited preserves banked credits.");
await memory.AdjustAsync(owner, Guid.NewGuid(), 0, false, "admin", "finite");
Check(!(await memory.GetAsync(owner)).Unlimited, "Explicit false disables unlimited.");
Check((await memory.AdjustAsync(owner, audit.OperationId, 10, false, "admin", "initial")).AlreadyApplied,
    "Adjustment idempotency survives later changes.");
try
{
    await memory.AdjustAsync(owner, audit.OperationId, 11, false, "admin", "initial");
    throw new Exception("Expected changed-payload conflict.");
}
catch (InvalidOperationException) { checks++; }
using var cancellation = new CancellationTokenSource();
cancellation.Cancel();
try
{
    await foreach (var _ in new InMemoryScanAllowanceStore().ListPendingAsync(cancellation.Token)) { }
    throw new Exception("Expected cancellation on empty recovery stream.");
}
catch (OperationCanceledException) { checks++; }
Console.WriteLine($"PASS: {checks} allowance checks (SDK batch failures, ETag conditions, serialization, TTL, concurrent domain rules).");

sealed class BatchResponse(HttpStatusCode status, params HttpStatusCode[] statuses) : TransactionalBatchResponse
{
    private readonly List<TransactionalBatchOperationResult> _operations = statuses.Select(s =>
        (TransactionalBatchOperationResult)Activator.CreateInstance(typeof(TransactionalBatchOperationResult),
            BindingFlags.Instance | BindingFlags.NonPublic, null, [s], null)!).ToList();
    public override HttpStatusCode StatusCode => status;
    public override string ErrorMessage => "offline injected failure";
    public override string ActivityId => "offline";
    public override double RequestCharge => 0;
    public override IEnumerator<TransactionalBatchOperationResult> GetEnumerator() => _operations.GetEnumerator();
}

sealed class HeaderResponse<T>(string etag) : ItemResponse<T>
{
    public override string ETag => etag;
}

sealed class RecordingBatch : TransactionalBatch
{
    public string? Operation { get; private set; }
    public string? Id { get; private set; }
    public object? Payload { get; private set; }
    public TransactionalBatchItemRequestOptions? Options { get; private set; }
    public override TransactionalBatch CreateItem<T>(T item, TransactionalBatchItemRequestOptions? options = null)
    {
        Operation = "create"; Payload = item; Options = options; return this;
    }
    public override TransactionalBatch ReplaceItem<T>(string id, T item, TransactionalBatchItemRequestOptions? options = null)
    {
        Operation = "replace"; Id = id; Payload = item; Options = options; return this;
    }
    public override TransactionalBatch CreateItemStream(Stream streamPayload, TransactionalBatchItemRequestOptions? options = null) => throw new NotSupportedException();
    public override TransactionalBatch ReadItem(string id, TransactionalBatchItemRequestOptions? options = null) => throw new NotSupportedException();
    public override TransactionalBatch UpsertItem<T>(T item, TransactionalBatchItemRequestOptions? options = null) => throw new NotSupportedException();
    public override TransactionalBatch UpsertItemStream(Stream streamPayload, TransactionalBatchItemRequestOptions? options = null) => throw new NotSupportedException();
    public override TransactionalBatch ReplaceItemStream(string id, Stream streamPayload, TransactionalBatchItemRequestOptions? options = null) => throw new NotSupportedException();
    public override TransactionalBatch DeleteItem(string id, TransactionalBatchItemRequestOptions? options = null) => throw new NotSupportedException();
    public override TransactionalBatch PatchItem(string id, IReadOnlyList<PatchOperation> patchOperations, TransactionalBatchPatchItemRequestOptions? options = null) => throw new NotSupportedException();
    public override Task<TransactionalBatchResponse> ExecuteAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public override Task<TransactionalBatchResponse> ExecuteAsync(TransactionalBatchRequestOptions options, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}
