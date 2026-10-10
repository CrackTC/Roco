using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;

namespace Roco.Commands;

/// <summary>
/// 下载全部资源：从 index 里逐条下载 bundle 到 --download，每下载完成（或因为本地已有同一份文件而跳过）
/// 一个包，就调用 <see cref="ModelBundlePlatformConverter"/> 把它转换成 WebGL 包写进 --converted；
/// --converted 里已有同名包、通过校验且不比源包旧时跳过转换。
/// </summary>
internal sealed class DownloadAllCommand
{
    const int MaxConcurrency = 5;
    const int MaxRetries = 3;
    const int RetryDelayMs = 2000;

    /// <summary>
    /// --download 目录里的记录：每个源包对应 index 里的哪个 hash（文件名 → hash）。同名的包换了内容而 size 没变时，
    /// size 看不出来。两个 index 版本之间抽查：hash 没变的 10 个内容都没变（三个版本里也没有 hash 没变而 size 变了
    /// 的条目），所以 hash 没变就当作文件没变；hash 变了而 size 没变的 10 个里只有 1 个内容变了，所以 hash 变了
    /// 只用来决定要不要重新下载比较，换不换源包看内容。
    /// 没有记录的包（记录出现之前下载的，或者下载后记录没存下来的）按 size 接受，并记成 index 当前的 hash：
    /// 在那之前发生的、size 没变的内容变化要等它的 hash 再变一次才会被发现。
    /// </summary>
    const string DownloadedHashesFileName = ".downloaded.json";

    /// <summary>每处理这么多个包存一次记录，进程中途被杀时已经下载好的包不至于没有记录。</summary>
    const int SaveHashesEvery = 2000;

    /// <summary>输出同步用；<see cref="Console"/> 本身不是线程安全的。</summary>
    readonly object consoleLock = new();

    string downloadRoot = "";
    string convertedRoot = "";
    string temporaryRoot = "";
    long downloadSize;
    // 每个包最后只落在一种结果里：downloaded（真的下了）/ skipped（本地已有同大小的包）/ failed（重试耗尽）。
    // total == downloaded + skipped + failed，成功数直接读 downloaded，不要用减法去凑。
    int downloaded;
    int skipped;
    int failed;
    int converted;
    int convertFailed;
    int convertSkipped;
    int processed;

    /// <summary>下载并转换；参数有问题时返回 false（错误已经打印过），调用方不要再报完成。</summary>
    public static bool Run(CommandContext context, CliOptions options)
    {
        var converted = options.ConvertedPath;
        if (string.IsNullOrWhiteSpace(converted))
        {
            Console.WriteLine("download-all 需要 --converted <已转换包目录>，否则下载完的包不会转换");
            return false;
        }

        return new DownloadAllCommand().Execute(context, converted, options.Limit);
    }

    bool Execute(CommandContext context, string convertedPath, int limit)
    {
        // 未转换的包和已转换的包是两个目录：转换好的包写到 --converted，源包留在 --download
        downloadRoot = Path.GetFullPath(context.DownloadPath);
        convertedRoot = Path.GetFullPath(convertedPath);
        if (string.Equals(downloadRoot, convertedRoot, StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("--converted 不能和 --download 是同一个目录：转换要把包写到另一个目录");
            return false;
        }

        // 转换用的临时文件放在输出目录里，保证最后一步能和目标包做同卷的移动
        temporaryRoot = Path.Combine(convertedRoot, ".convert-tmp");
        Directory.CreateDirectory(downloadRoot);
        Directory.CreateDirectory(convertedRoot);
        Directory.CreateDirectory(temporaryRoot);

        // 更新 index（写回本地 index.json）；拿不到新版本时用本地那份
        var index = UpdateIndexCommand.LoadOrFetch(context.IndexPath);
        var hashesPath = Path.Combine(downloadRoot, DownloadedHashesFileName);
        var hashes = new ConcurrentDictionary<string, string>(ReadDownloadedHashes(hashesPath));
        var hashesLock = new object();

        var items = limit > 0 ? index.Items.Take(limit).ToList() : index.Items.ToList();
        var total = items.Count;
        var totalSize = items.Sum(pair => pair.Value.Size);
        Console.WriteLine($"共 {total} 个资源，总大小 {totalSize / 1024.0 / 1024.0:F2} MB"
            + (limit > 0 ? $"（只处理前 {limit} 个）" : ""));
        Console.WriteLine($"未转换的包：{downloadRoot}");
        Console.WriteLine($"已转换的包：{convertedRoot}");
        using var httpClient = new HttpClient();
        using var semaphore = new SemaphoreSlim(MaxConcurrency);

        void Log(string message)
        {
            lock (consoleLock)
            {
                Console.WriteLine(message);
            }
        }

        // 记录任何时候存下来都是对的：一个包的 hash 只在它的文件就位之后才记进去
        void SaveHashes()
        {
            lock (hashesLock)
            {
                try
                {
                    WriteDownloadedHashes(hashesPath, hashes);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Log($"写不了 {hashesPath}：{ex.Message}");
                }
            }
        }

        void ReportProgress(uint addedSize)
        {
            lock (consoleLock)
            {
                var ok = Volatile.Read(ref downloaded);
                var skip = Volatile.Read(ref skipped);
                var fail = Volatile.Read(ref failed);
                downloadSize += addedSize;
                Console.WriteLine(
                    $"{downloadSize * 100.0 / totalSize:F2}% 进度：{downloadSize / 1024.0 / 1024.0:F2} MB / {totalSize / 1024.0 / 1024.0:F2} MB {ok + skip + fail}/{total}（下载 {ok}，跳过 {skip}，失败 {fail}，转换 {Volatile.Read(ref converted)}）"
                );
            }
        }

        // 下载完成或跳过后调用：把未转换的包转成 WebGL 包，输出到临时目录再替换到 --converted 里的同名包
        void ConvertBundle(string position, string fileName, string sourcePath)
        {
            var targetPath = Path.Combine(convertedRoot, fileName);
            if (File.Exists(targetPath))
            {
                // 源包比已转换包新：同名的包在 index 里换了内容、重新下载过，已转换包还是旧内容转出来的
                var outdated = File.GetLastWriteTimeUtc(sourcePath) > File.GetLastWriteTimeUtc(targetPath);
                if (!outdated && ModelBundlePlatformConverter.IsLz4Bundle(targetPath))
                {
                    Log($"{position} {fileName} 已转换包已存在，跳过转换");
                    Interlocked.Increment(ref convertSkipped);
                    return;
                }

                // 读不出包头或块表的包，以及旧版转换出来、带着播放器读不了的数据块（没压小却标成 LZ4）的包，也要重新转换
                Log(outdated
                    ? $"{position} {fileName} 源包比已转换包新，重新转换"
                    : $"{position} {fileName} 已转换包没通过校验，重新转换");
            }

            var temporaryPath = Path.Combine(temporaryRoot, fileName + "." + Guid.NewGuid().ToString("N") + ".tmp");
            var startTime = Environment.TickCount64;
            try
            {
                // ModelBundlePlatformConverter 自己也是先写临时文件、校验通过后才移到目标路径
                var result = ModelBundlePlatformConverter.Convert(sourcePath, temporaryPath, textOnly: false);
                // 转换出来的文件已经在临时目录里，校验通过后才放进 --converted
                ModelBundlePlatformConverter.Install(temporaryPath, targetPath);
                Interlocked.Increment(ref converted);
                var log = $"{position} {fileName} 转换完成：{result.ObjectCount} 对象校验通过，"
                    + $"{new FileInfo(targetPath).Length / 1024.0:F0} KB，耗时 {Environment.TickCount64 - startTime} ms";
                if (result.TextureCount > 0)
                    log += $"，{result.TextureCount} 纹理内联";
                if (result.RemovedStreams > 0)
                    log += $"，{result.RemovedStreams} 个无用流已丢弃";
                // 跨 bundle 依赖是唯一"转换没想到"的情况，单独说出来
                if (result.Messages.LastOrDefault(m => m.Contains("other bundle", StringComparison.Ordinal)) is { } note)
                    log += "；" + note[(note.IndexOf(';') + 1)..].Trim();
                Log(log);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref convertFailed);
                Log($"{position} {fileName} 转换失败：{ex.Message}");
            }
            finally
            {
                try
                {
                    if (File.Exists(temporaryPath))
                        File.Delete(temporaryPath);
                }
                catch { }
            }
        }

        async Task DownloadOneAsync(int position, string key, IndexItem item)
        {
            // index 的 key 是本地文件名，item.Name 是资源服务器上的 hash 文件名，两者通常不同：
            // 下载和转换都用 key，保证转换后的包和下载下来的包同名。
            var fileName = key;
            var filePath = Path.Combine(downloadRoot, key);
            var label = $"[{position + 1}/{total}]";

            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await semaphore.WaitAsync().ConfigureAwait(false);
            try
            {
                var exists = File.Exists(filePath);
                var sameSize = exists && new FileInfo(filePath).Length == item.Size;
                if (sameSize && (!hashes.TryGetValue(key, out var hash) || hash == item.Hash))
                {
                    hashes[key] = item.Hash;
                    Log($"{label} {fileName} 已存在且 size 一致，跳过 download");
                    Interlocked.Increment(ref skipped);
                    // 跳过的包也要转换，除非目标目录里已经有通过校验、不比它旧的同名包
                    ConvertBundle(label, fileName, filePath);
                    ReportProgress(item.Size);
                    return;
                }

                // size 没变而 hash 变了的包下载到旁边，内容真的变了才换掉源包；内容没变就保留原来的源包，
                // 它的时间不变，后面也就不会重新转换
                var downloadPath = sameSize ? filePath + ".download" : filePath;
                if (sameSize)
                {
                    Log($"{label} {fileName} hash 不一致，重新 download 后比较内容...");
                }
                else if (exists)
                {
                    Log($"{label} {fileName} size 不一致，删除后重新 download 中...");
                    try { File.Delete(filePath); } catch { }
                }

                var success = await DownloadAsync(label, fileName, item.Name, index.Version, downloadPath).ConfigureAwait(false);
                if (!success)
                {
                    // size 没变的那种情况原来的源包还在，记录也留着，下次再比
                    if (!sameSize)
                        hashes.TryRemove(key, out _);
                    Interlocked.Increment(ref failed);
                }
                else
                {
                    if (sameSize)
                    {
                        bool changed;
                        try
                        {
                            changed = !SameContent(filePath, downloadPath);
                            if (changed)
                                File.Move(downloadPath, filePath, true);
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            // 比不了或者换不了：原来的源包和记录都留着，下次再比
                            try { File.Delete(downloadPath); } catch { }
                            Log($"{label} {fileName} 比较内容失败：{ex.Message}");
                            Interlocked.Increment(ref failed);
                            ReportProgress(item.Size);
                            return;
                        }

                        if (!changed)
                        {
                            try { File.Delete(downloadPath); } catch { }
                        }

                        Log(changed
                            ? $"{label} {fileName} 内容变了，已换成新的源包"
                            : $"{label} {fileName} 内容没变，保留原来的源包");
                    }

                    hashes[key] = item.Hash;
                    Interlocked.Increment(ref downloaded);
                    // download 完成的包立刻转换
                    ConvertBundle(label, fileName, filePath);
                }

                ReportProgress(item.Size);
            }
            finally
            {
                semaphore.Release();
                if (Interlocked.Increment(ref processed) % SaveHashesEvery == 0)
                    SaveHashes();
            }
        }

        // remoteName 是资源服务器上的 hash 文件名，displayName 是本地文件名（log 里用）
        async Task<bool> DownloadAsync(string label, string displayName, string remoteName, int version, string filePath)
        {
            var assetUrl = AssetUrls.ForBundle(version, remoteName);

            for (var attempt = 1; attempt <= MaxRetries; attempt++)
            {
                try
                {
                    using var response = await httpClient.GetStreamAsync(assetUrl).ConfigureAwait(false);
                    using var fileStream = File.Create(filePath);
                    await response.CopyToAsync(fileStream).ConfigureAwait(false);

                    if (attempt > 1)
                    {
                        Log($"{label} {displayName} 第 {attempt} 次尝试 download 成功");
                    }
                    return true;
                }
                catch (Exception ex)
                {
                    try
                    {
                        if (File.Exists(filePath))
                        {
                            File.Delete(filePath);
                        }
                    }
                    catch { }

                    if (attempt < MaxRetries)
                    {
                        Log(
                            $"{label} {displayName} download 失败（第 {attempt}/{MaxRetries} 次）：" +
                            $"{ex.Message}，{RetryDelayMs / 1000} 秒后重试..."
                        );
                        await Task.Delay(RetryDelayMs).ConfigureAwait(false);
                    }
                    else
                    {
                        Log(
                            $"{label} {displayName} download 失败（已达最大重试次数 {MaxRetries}）：" +
                            $"{ex.Message}"
                        );
                    }
                }
            }

            return false;
        }

        var tasks = items
            .Select((pair, position) => Task.Run(() => DownloadOneAsync(position, pair.Key, pair.Value)))
            .ToArray();
        try
        {
            Task.WaitAll(tasks);
        }
        finally
        {
            SaveHashes();
        }

        try
        {
            // 转换残留的临时文件在这里清掉；转换中的临时文件不会被别的进程占用
            if (Directory.Exists(temporaryRoot) && !Directory.EnumerateFileSystemEntries(temporaryRoot).Any())
                Directory.Delete(temporaryRoot);
        }
        catch { }

        var succeeded = downloaded + skipped + failed;
        if (succeeded != total)
        {
            Console.WriteLine($"注意：下载 {downloaded} + 跳过 {skipped} + 失败 {failed} = {succeeded}，和处理数 {total} 不一致");
        }
        Console.WriteLine(
            $"全部完成：共 {total}，下载 {downloaded}，跳过 download {skipped}，失败 {failed}；" +
            $"转换 {converted}，跳过转换 {convertSkipped}，转换失败 {convertFailed}"
        );
        return true;
    }

    static bool SameContent(string first, string second)
    {
        using var a = File.OpenRead(first);
        using var b = File.OpenRead(second);
        return a.Length == b.Length && SHA256.HashData(a).AsSpan().SequenceEqual(SHA256.HashData(b));
    }

    static Dictionary<string, string> ReadDownloadedHashes(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                using var stream = File.OpenRead(path);
                return JsonSerializer.Deserialize(stream, AssetServiceJsonSerializerContext.Default.DictionaryStringString) ?? [];
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            Console.WriteLine($"读不了 {path}（{ex.Message}），这次只按 size 判断源包");
        }

        return [];
    }

    /// <summary>先写临时文件再改名，中断时不会留下半份记录。</summary>
    static void WriteDownloadedHashes(string path, ConcurrentDictionary<string, string> hashes)
    {
        var temporary = path + ".tmp";
        using (var stream = File.Create(temporary))
        {
            JsonSerializer.Serialize(stream, new Dictionary<string, string>(hashes),
                AssetServiceJsonSerializerContext.Default.DictionaryStringString);
        }

        File.Move(temporary, path, true);
    }
}
