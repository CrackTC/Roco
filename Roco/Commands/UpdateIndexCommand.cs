using System.Text.Json;
using Nerdbank.MessagePack;

namespace Roco.Commands;

/// <summary>
/// 更新资源 index：问 matsuri 要最新版本号，下载对应的 MessagePack index，再写成本地 JSON。
/// </summary>
internal static class UpdateIndexCommand
{
    public static void Run(CommandContext context)
    {
        var index = Fetch();
        Write(context.IndexPath, index);
        Console.WriteLine($"index 已写入：{context.IndexPath}（version {index.Version}，{index.Items.Count} 项）");
    }

    /// <summary>下载最新的 index；本地 index 不存在时 <see cref="Load"/> 也会用它。</summary>
    public static AssetIndex Fetch()
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
        return new AssetIndex(
            assetVersion,
            serializer.Deserialize<List<Dictionary<string, IndexItem>>, IndexItem>(stream)![0]
        );
    }

    /// <summary>读取本地 index，没有就先去更新一份。</summary>
    public static AssetIndex Load(string indexPath)
    {
        Console.WriteLine("load 本地资源 index 中...");
        if (File.Exists(indexPath))
        {
            return Read(indexPath);
        }

        var fetched = Fetch();
        Write(indexPath, fetched);
        return fetched;
    }

    /// <summary>
    /// 尽量用最新 index，但拿不到时（网络不通等）就退回本地已有一份，让下载还能继续。
    /// 两边都没有才认输。
    /// </summary>
    public static AssetIndex LoadOrFetch(string indexPath)
    {
        try
        {
            var fetched = Fetch();
            Write(indexPath, fetched);
            return fetched;
        }
        catch (Exception ex) when (File.Exists(indexPath))
        {
            Console.WriteLine($"update index 失败（{ex.Message}），改用本地 index：{indexPath}");
            return Read(indexPath);
        }
    }

    static AssetIndex Read(string indexPath)
    {
        using var stream = File.OpenRead(indexPath);
        return JsonSerializer.Deserialize(
            stream,
            AssetServiceJsonSerializerContext.Default.AssetIndex
        )!;
    }

    static void Write(string indexPath, AssetIndex index)
    {
        using var fileStream = File.Create(indexPath);
        JsonSerializer.Serialize(
            fileStream,
            index,
            AssetServiceJsonSerializerContext.Default.AssetIndex
        );
    }
}
