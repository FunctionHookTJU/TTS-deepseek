using System.IO;

namespace DeepSeekTTS;

/// <summary>
/// 临时缓存文件管理。
///
/// 生命周期：
///  - 生成成功后写入临时目录，记录在案
///  - 重新生成 / 手动删除 / 清空时立即删除
///  - 超过 TTL 自动回收
///  - 超过容量上限按最久未使用淘汰
///  - 应用启动时清空（兜底上次崩溃残留）
///  - 应用退出时清空
///
/// 写入用 .part 暂存再改名，避免出现半截文件。
/// </summary>
public sealed class TempAudioStore : IDisposable
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(30);
    private const int MaxEntries = 20;
    private const long MaxTotalBytes = 1024L * 1024 * 1024;

    private readonly DirectoryInfo _dir;
    private readonly object _gate = new();
    private readonly List<Entry> _entries = new();
    private readonly System.Timers.Timer _sweeper;
    private long _totalBytes;

    private sealed class Entry
    {
        public required string Id { get; init; }
        public required string FilePath { get; init; }
        public required long Bytes { get; init; }
        public required DateTime CreatedAt { get; init; }
        public DateTime LastAccess { get; set; }
        public required string Format { get; init; }
    }

    public TempAudioStore()
    {
        _dir = new DirectoryInfo(Path.Combine(AppSettings.DataDir, "tmp"));
        Directory.CreateDirectory(_dir.FullName);
        ClearAll();

        _sweeper = new System.Timers.Timer(60_000) { AutoReset = true };
        _sweeper.Elapsed += (_, _) => Sweep();
        _sweeper.Start();
    }

    public string DirPath => _dir.FullName;

    /// <summary>保存音频，返回临时文件路径。</summary>
    public string Save(string format, byte[] audio)
    {
        var ext = format == "pcm" ? "pcm" : "wav";
        var id = Guid.NewGuid().ToString();
        var finalPath = Path.Combine(_dir.FullName, $"{id}.{ext}");
        var partPath = finalPath + ".part";

        try
        {
            // 先写 .part 再改名：即使中途失败也不会留下可被误用的半截文件
            File.WriteAllBytes(partPath, audio);
            File.Move(partPath, finalPath, overwrite: true);
        }
        catch
        {
            TryDelete(partPath);
            throw;
        }

        lock (_gate)
        {
            _entries.Add(new Entry
            {
                Id = id,
                FilePath = finalPath,
                Bytes = audio.Length,
                CreatedAt = DateTime.Now,
                LastAccess = DateTime.Now,
                Format = format,
            });
            _totalBytes += audio.Length;
            EnforceLimits();
        }

        return finalPath;
    }

    public void Touch(string path)
    {
        lock (_gate)
        {
            var e = _entries.FirstOrDefault(x => x.FilePath == path);
            if (e is not null) e.LastAccess = DateTime.Now;
        }
    }

    public bool Delete(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;

        bool existed;
        lock (_gate)
        {
            var e = _entries.FirstOrDefault(x => x.FilePath == path);
            if (e is not null)
            {
                _entries.Remove(e);
                _totalBytes -= e.Bytes;
                existed = true;
            }
            else
            {
                existed = File.Exists(path);
            }
        }

        TryDelete(path);
        return existed;
    }

    private void EnforceLimits()
    {
        while (_entries.Count > 0 && (_entries.Count > MaxEntries || _totalBytes > MaxTotalBytes))
        {
            var victim = _entries.OrderBy(x => x.LastAccess).First();
            _entries.Remove(victim);
            _totalBytes -= victim.Bytes;
            TryDelete(victim.FilePath);
        }
    }

    private void Sweep()
    {
        List<Entry> expired;
        lock (_gate)
        {
            var now = DateTime.Now;
            expired = _entries.Where(x => now - x.LastAccess > Ttl).ToList();
            foreach (var e in expired)
            {
                _entries.Remove(e);
                _totalBytes -= e.Bytes;
            }
        }
        foreach (var e in expired) TryDelete(e.FilePath);
        RemoveOrphans();
    }

    /// <summary>清掉索引外的孤儿文件（上次异常退出可能残留）。</summary>
    private void RemoveOrphans()
    {
        try
        {
            var known = new HashSet<string>(
                _entries.Where(_ => true).Select(x => Path.GetFileName(x.FilePath)),
                StringComparer.OrdinalIgnoreCase);

            foreach (var f in _dir.EnumerateFiles())
            {
                if (!known.Contains(f.Name)) TryDelete(f.FullName);
            }
        }
        catch
        {
            // 清理失败不影响主流程
        }
    }

    /// <summary>清空全部临时文件。</summary>
    public void ClearAll()
    {
        lock (_gate)
        {
            _entries.Clear();
            _totalBytes = 0;
        }
        try
        {
            foreach (var f in _dir.EnumerateFiles()) TryDelete(f.FullName);
        }
        catch
        {
            // 目录不存在等情况，忽略
        }
    }

    private static bool TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        _sweeper.Stop();
        _sweeper.Dispose();
        ClearAll();
        try
        {
            if (_dir.Exists && !_dir.EnumerateFileSystemInfos().Any()) _dir.Delete();
        }
        catch
        {
            // 占用中则留给下次启动清理
        }
    }
}
