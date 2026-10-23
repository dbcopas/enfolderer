using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Enfolderer.Ai.Contracts;

var allowanceMode = args.Length == 8 && args[2] == "allowance";
if (args.Length != 7 && !allowanceMode)
{
    Console.Error.WriteLine("Usage: ScanDiagnostics <config-file> <output-directory> <latest|job-id> <hours> <record-file> <import-record|none> <silent|sign-in>");
    return 2;
}
try
{
    var config = File.ReadLines(args[0]).Where(l => !string.IsNullOrWhiteSpace(l) && !l.TrimStart().StartsWith('#'))
        .Select(l => l.Split('=', 2)).ToDictionary(p => p[0].Trim(), p => p[1].Trim(), StringComparer.OrdinalIgnoreCase);
    var api = new Uri(config["api_base_url"].TrimEnd('/') + "/");
    if (api.Scheme != Uri.UriSchemeHttps) throw new InvalidOperationException("Diagnostics requires an HTTPS API URL.");
    if (!int.TryParse(args[3], out var hours) || hours is < 1 or > 168)
        throw new ArgumentException("Hours must be 1-168.");
    if (!allowanceMode && args[2] != "latest" && (!Guid.TryParse(args[2], out var jobGuid) || jobGuid == Guid.Empty))
        throw new ArgumentException("Job ID must be a GUID, or 'latest'.");

    var recordPath = Path.GetFullPath(args[4]);
    var loadPath = File.Exists(recordPath) ? recordPath : args[5] != "none" && File.Exists(args[5]) ? args[5] : null;
    AuthenticationRecord? record = null;
    if (loadPath is not null)
    {
        await using var input = File.OpenRead(loadPath);
        record = await AuthenticationRecord.DeserializeAsync(input);
        if (!string.Equals(record.TenantId, config["tenant_id"], StringComparison.OrdinalIgnoreCase)
            || !string.Equals(record.ClientId, config["client_id"], StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Authentication record belongs to another tenant/client. Use the matching config and record.");
    }
    var credential = new InteractiveBrowserCredential(new InteractiveBrowserCredentialOptions
    {
        TenantId = config["tenant_id"], ClientId = config["client_id"], AuthenticationRecord = record,
        DisableAutomaticAuthentication = true,
        TokenCachePersistenceOptions = new TokenCachePersistenceOptions { Name = "Enfolderer.Scan" }
    });
    var request = new TokenRequestContext([config["scope"]]);
    if (args[6] == "sign-in")
    {
        record = await credential.AuthenticateAsync(request);
    }
    else if (record is null)
        throw new InvalidOperationException("No cached account record. Run get-scan-diagnostics.ps1 -SignIn once.");
    var token = await credential.GetTokenAsync(request);
    Directory.CreateDirectory(Path.GetDirectoryName(recordPath)!);
    await using (var saved = File.Create(recordPath))
        await record!.SerializeAsync(saved);

    using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
    {
        BaseAddress = api, Timeout = TimeSpan.FromMinutes(3)
    };
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
    var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
    if (allowanceMode)
    {
        using var command = JsonDocument.Parse(await File.ReadAllTextAsync(args[7]));
        var objectId = command.RootElement.GetProperty("objectId").GetGuid();
        if (objectId == Guid.Empty) throw new ArgumentException("A nonempty target object ID is required.");
        var route = $"admin/allowances/{objectId:D}";
        using var operation = command.RootElement.TryGetProperty("operationId", out _)
            ? await client.PostAsJsonAsync(route, command.RootElement)
            : await client.GetAsync(route);
        if (!operation.IsSuccessStatusCode)
            throw new InvalidOperationException($"Allowance request failed ({(int)operation.StatusCode}): {await operation.Content.ReadAsStringAsync()}");
        var content = await operation.Content.ReadFromJsonAsync<JsonElement>();
        Directory.CreateDirectory(args[1]);
        await File.WriteAllTextAsync(Path.Combine(args[1], "allowance.json"), JsonSerializer.Serialize(content, json));
        Console.WriteLine(JsonSerializer.Serialize(content, json));
        return 0;
    }
    var jobId = args[2];
    if (jobId == "latest")
    {
        var jobs = await client.GetFromJsonAsync<JsonElement>($"diagnostics/scans/?hours={hours}&limit=1");
        if (jobs.GetArrayLength() == 0) throw new InvalidOperationException($"No uploaded pilot scan in the last {hours} hours.");
        jobId = jobs[0].GetProperty("jobId").GetString()!;
    }
    jobId = Guid.Parse(jobId).ToString("N");
    using var response = await client.GetAsync($"diagnostics/scans/{jobId}");
    if (response.StatusCode == HttpStatusCode.Forbidden)
        throw new InvalidOperationException("This account is not an approved diagnostics operator.");
    response.EnsureSuccessStatusCode();
    var job = await response.Content.ReadFromJsonAsync<ScanJobDocument>()
        ?? throw new InvalidDataException("Empty job document.");
    if (job.JobId != jobId) throw new InvalidDataException("API returned a different job.");
    var directory = Path.Combine(Path.GetFullPath(args[1]), $"{jobId}-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfffZ}");
    Directory.CreateDirectory(directory);
    await File.WriteAllTextAsync(Path.Combine(directory, "job.json"), JsonSerializer.Serialize(job, json));
    if (job.Result is not null)
        await File.WriteAllTextAsync(Path.Combine(directory, "result.json"), JsonSerializer.Serialize(job.Result, json));
    var files = new List<object>();
    async Task Download(string route, string name)
    {
        using var download = await client.GetAsync($"diagnostics/scans/{jobId}/{route}", HttpCompletionOption.ResponseHeadersRead);
        if (download.StatusCode == HttpStatusCode.NotFound)
        {
            Console.Error.WriteLine($"Unavailable for job {jobId}: {name} (may not have been produced yet).");
            files.Add(new { name, available = false, reason = "HTTP 404: absent at retrieval time" });
            return;
        }
        download.EnsureSuccessStatusCode();
        var path = Path.Combine(directory, name);
        var partial = path + ".partial";
        await using (var output = File.Create(partial)) await download.Content.CopyToAsync(output);
        File.Move(partial, path);
        using var content = File.OpenRead(path);
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content));
        files.Add(new { name, available = true, sha256 = hash });
    }
    await Download("original", "original" + Path.GetExtension(ScanBlobPaths.SanitizeFileName(job.BlobPath)));
    if (job.Result?.AnnotatedImagePath is not null) await Download("annotated-image", "overlay.png");
    if (job.Result is not null) await Download("cards.csv", "cards.csv");
    await Download("identification.json", "identification.json");
    for (var index = 0; index < job.CardsDetected; index++)
    {
        await Download($"crops/{index}", $"crop-{index:D3}.png");
        await Download($"identification/{index}", $"identification-{index:D3}.json");
    }
    await File.WriteAllTextAsync(Path.Combine(directory, "manifest.json"), JsonSerializer.Serialize(new
    {
        job.JobId, retrievedAt = DateTimeOffset.UtcNow, api = api.ToString(), job.Status,
        files, logs = "Logs are collected separately by get-scan-diagnostics.ps1; see logs-status.json."
    }, json));
    Console.WriteLine(directory);
    return 0;
}
catch (Exception ex) when (ex is AuthenticationFailedException or AuthenticationRequiredException
    or HttpRequestException or IOException or InvalidOperationException or ArgumentException
    or JsonException or KeyNotFoundException or TaskCanceledException)
{
    Console.Error.WriteLine($"Scan diagnostics failed: {ex.Message}");
    return 1;
}
