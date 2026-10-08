namespace Roco.Commands;

/// <summary>
/// 下载全部资源：从 index 里逐条下载 bundle 到 --download，每下载完成（或因为本地已有同大小文件而跳过）
/// 一个包，就调用 <see cref="ModelBundlePlatformConverter"/> 把它转换成 WebGL 包写进 --converted；
/// --converted 里已有同名包时跳过转换。
/// </summary>
internal sealed class DownloadAllCommand
{
    const int MaxConcurrency = 5;
    const int MaxRetries = 3;
    const int RetryDelayMs = 2000;

    /// <summary>输出同步用；<see cref="Console"/> 本身不是线程安全的。</summary>
    readonly object consoleLock = new();

    string downloadRoot = "";
    string convertedRoot = "";
    string temporaryRoot = "";
    long downloadSize;
    int completed;
    int failed;
    int skipped;
    int converted;
    int convertFailed;
    int convertSkipped;

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

        // 更新 index（内部会写入本地 index.json）
        var index = UpdateIndexCommand.Fetch();

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

        void ReportProgress(uint addedSize)
        {
            lock (consoleLock)
            {
                var done = Volatile.Read(ref completed);
                var fail = Volatile.Read(ref failed);
                var skip = Volatile.Read(ref skipped);
                downloadSize += addedSize;
                Console.WriteLine(
                    $"{downloadSize * 100.0 / totalSize:F2}% 进度：{downloadSize / 1024.0 / 1024.0:F2} MB / {totalSize / 1024.0 / 1024.0:F2} MB {done}/{total}（成功 {done - fail - skip}，跳过 {skip}，失败 {fail}，转换 {Volatile.Read(ref converted)}）"
                );
            }
        }

        // 下载完成或跳过后调用：把未转换的包转成 WebGL 包，输出到临时目录再替换到 --converted 里的同名包
        void ConvertBundle(string position, string fileName, string sourcePath)
        {
            var targetPath = Path.Combine(convertedRoot, fileName);
            if (File.Exists(targetPath))
            {
                Log($"{position} {fileName} 已转换包已存在，跳过转换");
                Interlocked.Increment(ref convertSkipped);
                return;
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
                Log(
                    $"{position} {fileName} 转换完成：{result.TextureCount} 纹理内联，{result.ObjectCount} 对象校验通过，" +
                    $"{result.RemovedStreams} 个无用流已丢弃，{new FileInfo(targetPath).Length / 1024.0:F0} KB，" +
                    $"耗时 {Environment.TickCount64 - startTime} ms"
                );
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
            var fileName = string.IsNullOrWhiteSpace(item.Name) ? key : item.Name;
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
                if (File.Exists(filePath))
                {
                    if (new FileInfo(filePath).Length == item.Size)
                    {
                        Log($"{label} {fileName} 已存在且 size 一致，跳过 download");
                        Interlocked.Increment(ref skipped);
                        // 跳过的包也要转换，除非目标目录里已经有同名包
                        ConvertBundle(label, fileName, filePath);
                        Interlocked.Increment(ref completed);
                        ReportProgress(item.Size);
                        return;
                    }

                    Log($"{label} {fileName} size 不一致，删除后重新 download 中...");
                    try { File.Delete(filePath); } catch { }
                }

                var success = await DownloadAsync(label, fileName, index.Version, filePath).ConfigureAwait(false);
                if (!success)
                {
                    Interlocked.Increment(ref failed);
                }
                else
                {
                    // download 完成的包立刻转换
                    ConvertBundle(label, fileName, filePath);
                }

                Interlocked.Increment(ref completed);
                ReportProgress(item.Size);
            }
            finally
            {
                semaphore.Release();
            }
        }

        async Task<bool> DownloadAsync(string label, string fileName, int version, string filePath)
        {
            var assetUrl = AssetUrls.ForBundle(version, fileName);

            for (var attempt = 1; attempt <= MaxRetries; attempt++)
            {
                try
                {
                    using var response = await httpClient.GetStreamAsync(assetUrl).ConfigureAwait(false);
                    using var fileStream = File.Create(filePath);
                    await response.CopyToAsync(fileStream).ConfigureAwait(false);

                    if (attempt > 1)
                    {
                        Log($"{label} {fileName} 第 {attempt} 次尝试 download 成功");
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
                            $"{label} {fileName} download 失败（第 {attempt}/{MaxRetries} 次）：" +
                            $"{ex.Message}，{RetryDelayMs / 1000} 秒后重试..."
                        );
                        await Task.Delay(RetryDelayMs).ConfigureAwait(false);
                    }
                    else
                    {
                        Log(
                            $"{label} {fileName} download 失败（已达最大重试次数 {MaxRetries}）：" +
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
        Task.WaitAll(tasks);

        try
        {
            // 转换残留的临时文件在这里清掉；转换中的临时文件不会被别的进程占用
            if (Directory.Exists(temporaryRoot) && !Directory.EnumerateFileSystemEntries(temporaryRoot).Any())
                Directory.Delete(temporaryRoot);
        }
        catch { }

        Console.WriteLine(
            $"全部完成：共 {total}，成功 {total - failed - skipped}，跳过 download {skipped}，失败 {failed}，" +
            $"转换 {converted}，跳过转换 {convertSkipped}，转换失败 {convertFailed}"
        );
        return true;
    }
}
