using System.IO;

namespace Enfolderer.App;

public class CachePathService
{
    private readonly string _root;
    public CachePathService(string root) { _root = root; }
    public string MetaDir => Path.Combine(_root, "meta");
    public string MetaDonePath(string hash) => Path.Combine(MetaDir, hash + ".done");
    public bool IsMetaComplete(string hash) => File.Exists(MetaDonePath(hash));
}
