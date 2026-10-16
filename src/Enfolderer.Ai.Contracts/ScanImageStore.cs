namespace Enfolderer.Ai.Contracts;

/// <summary>
/// Read/write access to the scan and crop images. Implemented over Azure Blob Storage in Azure and
/// over a local directory for offline development.
/// </summary>
public interface IScanImageStore
{
    /// <summary>Opens a blob for reading. <paramref name="blobPath"/> is <c>container/name</c>.</summary>
    Task<Stream> OpenReadAsync(string blobPath, CancellationToken ct = default);

    /// <summary>Writes (or overwrites) a blob. <paramref name="blobPath"/> is <c>container/name</c>.</summary>
    Task WriteAsync(string blobPath, Stream content, string contentType, CancellationToken ct = default);

    Task<bool> ExistsAsync(string blobPath, CancellationToken ct = default);
}

/// <summary>
/// Local-filesystem image store used when no storage account is configured. The API and the worker
/// point at the same root directory so the whole upload/poll loop can be demoed offline.
/// </summary>
public sealed class LocalDirectoryScanImageStore : IScanImageStore
{
    private readonly string _root;

    public LocalDirectoryScanImageStore(string root)
    {
        _root = Path.GetFullPath(root);
        Directory.CreateDirectory(_root);
    }

    public Task<Stream> OpenReadAsync(string blobPath, CancellationToken ct = default)
    {
        var full = Resolve(blobPath);
        Stream stream = File.OpenRead(full);
        return Task.FromResult(stream);
    }

    public async Task WriteAsync(string blobPath, Stream content, string contentType, CancellationToken ct = default)
    {
        var full = Resolve(blobPath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await using var file = File.Create(full);
        await content.CopyToAsync(file, ct);
    }

    public Task<bool> ExistsAsync(string blobPath, CancellationToken ct = default) =>
        Task.FromResult(File.Exists(Resolve(blobPath)));

    /// <summary>
    /// Maps a <c>container/name</c> blob path onto the local root, rejecting anything that would
    /// escape it.
    /// </summary>
    private string Resolve(string blobPath)
    {
        if (string.IsNullOrWhiteSpace(blobPath))
            throw new ArgumentException("Blob path is required.", nameof(blobPath));

        var segments = blobPath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        foreach (var segment in segments)
        {
            if (segment is "." or "..")
                throw new ArgumentException($"Invalid blob path '{blobPath}'.", nameof(blobPath));
        }

        var full = Path.GetFullPath(Path.Combine(_root, Path.Combine(segments)));
        var rootWithSeparator = _root.EndsWith(Path.DirectorySeparatorChar) ? _root : _root + Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootWithSeparator, StringComparison.Ordinal))
            throw new ArgumentException($"Invalid blob path '{blobPath}'.", nameof(blobPath));

        return full;
    }
}
