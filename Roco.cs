#:package AssetsTools.NET@3.0.3
#:package Nerdbank.MessagePack@1.0.43

using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using Nerdbank.MessagePack;
using PolyType;

if (args.Length < 1)
{
    Console.WriteLine("使用方法：");
    Console.WriteLine($"解密: {AppDomain.CurrentDomain.FriendlyName} <文件路径>");
    Console.WriteLine(
        $"加密: {AppDomain.CurrentDomain.FriendlyName} <文件路径> <要 patch 的资源文件>"
    );
    Console.WriteLine($"更新资源 index: {AppDomain.CurrentDomain.FriendlyName} update-index");
    TryReadKey();
    return;
}

if (args[0] == "update-index")
{
    UpdateIndex();
    Console.WriteLine("资源 index 的 update finished 的说");
    TryReadKey();
    return;
}

var filePath = args[0];

if (!File.Exists(filePath))
{
    Console.WriteLine("文件 not exist 的说");
    TryReadKey();
    return;
}

using var stream = File.OpenRead(filePath);

using var aes = Aes.Create();
aes.Key = Convert.FromBase64String("rT8Pie5RxTdzHxeW91xxhAFhdW2g1IbJ");
aes.IV = Convert.FromBase64String("TkCziuvxqFMSLF+tzKNoXQ==");

if (
    Enumerable.Range(0, 7).Select(i => stream.ReadByte()).ToArray()
    is ['U', 'n', 'i', 't', 'y', 'F', 'S']
)
{
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
}
else
{
    stream.Seek(0, SeekOrigin.Begin);

    string? patchFilePath;
    if (args.Length < 2 && Console.IsInputRedirected is false)
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
        patchFilePath = args[1];
    }

    if (string.IsNullOrWhiteSpace(patchFilePath))
    {
        var index = LoadLocalIndex();
        if (index.Items.GetValueOrDefault(Path.GetFileName(filePath) + ".unity3d") is not { } item)
        {
            Console.WriteLine("资源 id not found...可以尝试 update index 的说");
            TryReadKey();
            return;
        }

        var assetUrl =
            $"https://d2sf4w9bkv485c.cloudfront.net/{index.Version}/production/2018/Android/{item.Name}";
        Console.WriteLine($"{assetUrl} download 中...");
        using var httpClient = new HttpClient();
        using var response = httpClient.GetStreamAsync(assetUrl).Result;
        patchFilePath = filePath + ".unity3d";
        using var fileStream = File.Create(patchFilePath);
        response.CopyTo(fileStream);
    }

    if (!File.Exists(patchFilePath))
    {
        Console.WriteLine("文件 not exist 的说");
        TryReadKey();
        return;
    }

    var backFilePath = patchFilePath + ".bak";
    File.Copy(patchFilePath, backFilePath, true);

    var manager = new AssetsManager();
    var bundleInst = manager.LoadBundleFile(backFilePath, true);
    var fileInst = manager.LoadAssetsFileFromBundle(bundleInst, 0, false);
    var textInfo = fileInst.file.GetAssetsOfType(AssetClassID.TextAsset).Single();
    var textBase = manager.GetBaseField(fileInst, textInfo);

    using var ms = new MemoryStream();
    using var cryptoStream = new CryptoStream(ms, aes.CreateEncryptor(), CryptoStreamMode.Write);
    stream.CopyTo(cryptoStream);
    cryptoStream.FlushFinalBlock();
    textBase["m_Script"].AsByteArray = ms.ToArray();
    textInfo.SetNewData(textBase);

    bundleInst.file.BlockAndDirInfo.DirectoryInfos[0].SetNewData(fileInst.file);
    using var writer = new AssetsFileWriter(patchFilePath);
    bundleInst.file.Write(writer);
}

static void TryReadKey()
{
    try
    {
        Console.ReadKey();
    }
    catch { }
}

static AssetIndex LoadLocalIndex()
{
    Console.WriteLine("load 本地资源 index 中...");
    var path = Path.Combine(AppContext.BaseDirectory, "index.json");
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

static AssetIndex UpdateIndex()
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
    var path = Path.Combine(AppContext.BaseDirectory, "index.json");
    using var fileStream = File.Create(path);
    JsonSerializer.Serialize(
        fileStream,
        index,
        AssetServiceJsonSerializerContext.Default.AssetIndex
    );
    return index;
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
