#:package AssetsTools.NET@3.0.3
#:package Nerdbank.MessagePack@1.0.43

using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using Nerdbank.MessagePack;
using PolyType;

// --- 解析可选参数 ---
string indexFilePath = "index.json";
string downloadDirPath = "downloads";

var remainingArgs = new List<string>();
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--index" && i + 1 < args.Length)
    {
        indexFilePath = args[++i];
    }
    else if (args[i] == "--download" && i + 1 < args.Length)
    {
        downloadDirPath = args[++i];
    }
    else
    {
        remainingArgs.Add(args[i]);
    }
}

if (remainingArgs.Count < 1)
{
    Console.WriteLine("使用方法：");
    Console.WriteLine($"解密: {AppDomain.CurrentDomain.FriendlyName} [--index <index路径>] [--download <下载目录>] decrypt <bundle文件路径>");
    Console.WriteLine($"加密: {AppDomain.CurrentDomain.FriendlyName} [--index <index路径>] [--download <下载目录>] encrypt <要 patch 的源文件> [bundle文件路径]");
    Console.WriteLine($"更新资源 index: {AppDomain.CurrentDomain.FriendlyName} [--index <index路径>] update-index");
    Console.WriteLine($"下载全部资源:   {AppDomain.CurrentDomain.FriendlyName} [--index <index路径>] [--download <下载目录>] download-all");
    Console.WriteLine();
    Console.WriteLine("选项：");
    Console.WriteLine("  --index <路径>    指定 index.json 的路径（默认: index.json）");
    Console.WriteLine("  --download <目录> 指定下载目录（默认: downloads）");
    return;
}

var command = remainingArgs[0];

switch (command)
{
    case "update-index":
        UpdateIndex();
        Console.WriteLine("资源 index 的 update finished 的说");
        return;

    case "download-all":
        DownloadAll();
        Console.WriteLine("全部资源 download finished 的说");
        return;

    case "decrypt":
        DecryptCommand();
        return;

    case "encrypt":
        EncryptCommand();
        return;

    default:
        Console.WriteLine($"未知的命令：{command}");
        Console.WriteLine("可用命令：decrypt, encrypt, update-index, download-all");
        return;
}

// --- 命令实现 ---

void DecryptCommand()
{
    if (remainingArgs.Count < 2)
    {
        Console.WriteLine("使用方法：decrypt <bundle文件路径>");
        return;
    }

    var filePath = remainingArgs[1];
    if (!File.Exists(filePath))
    {
        Console.WriteLine("文件 not exist 的说");
        return;
    }

    using var stream = File.OpenRead(filePath);

    using var aes = Aes.Create();
    aes.Key = Convert.FromBase64String("rT8Pie5RxTdzHxeW91xxhAFhdW2g1IbJ");
    aes.IV = Convert.FromBase64String("TkCziuvxqFMSLF+tzKNoXQ==");

    stream.Seek(0, SeekOrigin.Begin);

    var manager = new AssetsManager();
    var bundleInst = manager.LoadBundleFile(stream, true);
    var fileInst = manager.LoadAssetsFileFromBundle(bundleInst, 0, false);

    var textInfo = fileInst.file.GetAssetsOfType(AssetClassID.TextAsset).Single();
    var textBase = manager.GetBaseField(fileInst, textInfo);
    var name = textBase["m_Name"].AsString;
    var script = textBase["m_Script"].AsByteArray;
    var decrypted = aes.CreateDecryptor().TransformFinalBlock(script, 0, script.Length);
    File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(filePath)!, name), decrypted);

    Console.WriteLine($"解密完成：{name}");
}

void EncryptCommand()
{
    if (remainingArgs.Count < 2)
    {
        Console.WriteLine("使用方法：encrypt <要 patch 的源文件> [bundle文件路径]");
        Console.WriteLine("  bundle 文件路径可选，留空则自动从资源服务器 download");
        return;
    }

    var sourceFilePath = remainingArgs[1];
    if (!File.Exists(sourceFilePath))
    {
        Console.WriteLine("源文件 not exist 的说");
        return;
    }

    string? patchFilePath;
    if (remainingArgs.Count < 3 && Console.IsInputRedirected is false)
    {
        Console.Write("请输入要 patch 的资源文件（留空自动从资源服务器 download）：");
        patchFilePath = Console.ReadLine();
    }
    else if (Console.IsInputRedirected)
    {
        patchFilePath = null;
    }
    else
    {
        patchFilePath = remainingArgs[2];
    }

    if (string.IsNullOrWhiteSpace(patchFilePath))
    {
        var index = LoadLocalIndex();
        // 根据源文件名推断 bundle 名
        if (index.Items.GetValueOrDefault(Path.GetFileName(sourceFilePath) + ".unity3d") is not { } item)
        {
            Console.WriteLine("资源 id not found...可以尝试 update index 的说");
            return;
        }

        var assetUrl =
            $"https://d2sf4w9bkv485c.cloudfront.net/{index.Version}/production/2018/Android/{item.Name}";
        Console.WriteLine($"{assetUrl} download 中...");
        using var httpClient = new HttpClient();
        using var response = httpClient.GetStreamAsync(assetUrl).Result;
        patchFilePath = sourceFilePath + ".unity3d";
        using var fileStream = File.Create(patchFilePath);
        response.CopyTo(fileStream);
    }

    if (!File.Exists(patchFilePath))
    {
        Console.WriteLine("bundle 文件 not exist 的说");
        return;
    }

    var backFilePath = patchFilePath + ".bak";
    File.Copy(patchFilePath, backFilePath, true);

    using var aes = Aes.Create();
    aes.Key = Convert.FromBase64String("rT8Pie5RxTdzHxeW91xxhAFhdW2g1IbJ");
    aes.IV = Convert.FromBase64String("TkCziuvxqFMSLF+tzKNoXQ==");

    var manager = new AssetsManager();
    var bundleInst = manager.LoadBundleFile(backFilePath, true);
    var fileInst = manager.LoadAssetsFileFromBundle(bundleInst, 0, false);
    var textInfo = fileInst.file.GetAssetsOfType(AssetClassID.TextAsset).Single();
    var textBase = manager.GetBaseField(fileInst, textInfo);

    using var sourceStream = File.OpenRead(sourceFilePath);
    using var ms = new MemoryStream();
    using var cryptoStream = new CryptoStream(ms, aes.CreateEncryptor(), CryptoStreamMode.Write);
    sourceStream.CopyTo(cryptoStream);
    cryptoStream.FlushFinalBlock();
    textBase["m_Script"].AsByteArray = ms.ToArray();
    textInfo.SetNewData(textBase);

    bundleInst.file.BlockAndDirInfo.DirectoryInfos[0].SetNewData(fileInst.file);
    using var writer = new AssetsFileWriter(patchFilePath);
    bundleInst.file.Write(writer);

    Console.WriteLine($"加密完成：{patchFilePath}");
}

AssetIndex LoadLocalIndex()
{
    Console.WriteLine("load 本地资源 index 中...");
    var path = indexFilePath;
    if (!File.Exists(path))
    {
        return UpdateIndex();
    }

    using var stream = File.OpenRead(path);
    return JsonSerializer.Deserialize(
        stream,
        AssetServiceJsonSerializerContext.Default.AssetIndex
    )!;
}

AssetIndex UpdateIndex()
{
    Console.WriteLine("update 资源 index 中...");
    var matsuriVersionApi = "https://api.matsurihi.me/api/mltd/v2/version/latest";
    using var httpClient = new HttpClient();
    using var response = httpClient.GetStreamAsync(matsuriVersionApi).Result;
    using var jsonDoc = JsonDocument.Parse(response);
    var assetVersion = jsonDoc.RootElement.GetProperty("asset").GetProperty("version").GetInt32()!;
    var assetIndexName = jsonDoc
        .RootElement.GetProperty("asset")
        .GetProperty("indexName")
        .GetString()!;

    var assetIndexUrl =
        $"https://d2sf4w9bkv485c.cloudfront.net/{assetVersion}/production/2018/Android/{assetIndexName}";
    using var stream = httpClient.GetStreamAsync(assetIndexUrl).Result;
    var serializer = new MessagePackSerializer();
    var index = new AssetIndex(
        assetVersion,
        serializer.Deserialize<List<Dictionary<string, IndexItem>>, IndexItem>(stream)![0]
    );
    var path = indexFilePath;
    using var fileStream = File.Create(path);
    JsonSerializer.Serialize(
        fileStream,
        index,
        AssetServiceJsonSerializerContext.Default.AssetIndex
    );
    return index;
}

void DownloadAll()
{
    // 先更新 index（内部会写入本地 index.json）
    var index = UpdateIndex();

    var downloadDir = downloadDirPath;
    Directory.CreateDirectory(downloadDir);

    const int maxConcurrency = 5;
    const int maxRetries = 3;
    const int retryDelayMs = 2000;

    var items = index.Items.ToList();
    var total = items.Count;
    //总大小：
    var totalSize = items.Sum(kv => kv.Value.Size);
    var downloadSize = 0L;
    Console.WriteLine($"共 {total} 个资源，总大小 {totalSize / 1024.0 / 1024.0:F2} MB");
    using var httpClient = new HttpClient();

    // 进度与输出同步
    var consoleLock = new object();
    var completed = 0;
    var failed = 0;
    var skipped = 0;

    using var semaphore = new SemaphoreSlim(maxConcurrency);

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
                $"{downloadSize * 100.0 / totalSize:F2}% 进度：{downloadSize / 1024.0 / 1024.0:F2} MB / {totalSize / 1024.0 / 1024.0:F2} MB {done}/{total}（成功 {done - fail - skip}，跳过 {skip}，失败 {fail}）"
            );
        }
    }

    var tasks = items.Select((kv, idx) => Task.Run(async () =>
    {
        var (key, item) = kv;
        var position = $"[{idx + 1}/{total}]";

        var fileName = string.IsNullOrWhiteSpace(item.Name) ? key : item.Name;
        var filePath = Path.Combine(downloadDir, key);

        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        await semaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            if (File.Exists(filePath))
            {
                var existingSize = new FileInfo(filePath).Length;
                if (existingSize == item.Size)
                {
                    Log($"{position} {fileName} 已存在且 size 一致，跳过");
                    Interlocked.Increment(ref skipped);
                    Interlocked.Increment(ref completed);
                    ReportProgress(item.Size);
                    return;
                }

                Log($"{position} {fileName} size 不一致，删除后重新 download 中...");
                try { File.Delete(filePath); } catch { }
            }

            var assetUrl =
                $"https://d2sf4w9bkv485c.cloudfront.net/{index.Version}/production/2018/Android/{fileName}";

            var success = false;
            for (var attempt = 1; attempt <= maxRetries && !success; attempt++)
            {
                try
                {
                    using var response = await httpClient
                        .GetStreamAsync(assetUrl)
                        .ConfigureAwait(false);
                    using var fileStream = File.Create(filePath);
                    await response.CopyToAsync(fileStream).ConfigureAwait(false);
                    success = true;

                    if (attempt > 1)
                    {
                        Log($"{position} {fileName} 第 {attempt} 次尝试 download 成功");
                    }
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

                    if (attempt < maxRetries)
                    {
                        Log(
                            $"{position} {fileName} download 失败（第 {attempt}/{maxRetries} 次）：" +
                            $"{ex.Message}，{retryDelayMs / 1000} 秒后重试..."
                        );
                        await Task.Delay(retryDelayMs).ConfigureAwait(false);
                    }
                    else
                    {
                        Log(
                            $"{position} {fileName} download 失败（已达最大重试次数 {maxRetries}）：" +
                            $"{ex.Message}"
                        );
                    }
                }
            }

            if (!success)
            {
                Interlocked.Increment(ref failed);
            }

            Interlocked.Increment(ref completed);
            ReportProgress(item.Size);
        }
        finally
        {
            semaphore.Release();
        }
    })).ToArray();

    Task.WaitAll(tasks);

    Console.WriteLine(
        $"全部完成：共 {total}，成功 {total - failed - skipped}，跳过 {skipped}，失败 {failed}"
    );
}

[GenerateShapeFor<List<Dictionary<string, IndexItem>>>]
public partial record IndexItem(
    [property: Key(0)] string Hash,
    [property: Key(1)] string Name,
    [property: Key(2)] uint Size
);

record AssetIndex(int Version, Dictionary<string, IndexItem> Items);

[JsonSerializable(typeof(AssetIndex))]
internal partial class AssetServiceJsonSerializerContext : JsonSerializerContext;