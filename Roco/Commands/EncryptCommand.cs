using System.Security.Cryptography;
using AssetsTools.NET;
using AssetsTools.NET.Extra;

namespace Roco.Commands;

/// <summary>
/// 加密：把源文件加密后写进 bundle 里 TextAsset 的 m_Script，用来做 patch。
/// 不指定 bundle 时按源文件名从资源服务器下载一份原始 bundle。
/// </summary>
internal static class EncryptCommand
{
    public static void Run(CommandContext context, List<string> args)
    {
        if (args.Count < 2)
        {
            Console.WriteLine("使用方法：encrypt <要 patch 的源文件> [bundle文件路径]");
            Console.WriteLine("  bundle 文件路径可选，留空则自动从资源服务器 download");
            return;
        }

        var sourceFilePath = args[1];
        if (!File.Exists(sourceFilePath))
        {
            Console.WriteLine("源文件 not exist 的说");
            return;
        }

        var patchFilePath = ResolvePatchFile(context, args, sourceFilePath);
        if (patchFilePath is null)
        {
            return;
        }

        if (!File.Exists(patchFilePath))
        {
            Console.WriteLine("bundle 文件 not exist 的说");
            return;
        }

        var backFilePath = patchFilePath + ".bak";
        File.Copy(patchFilePath, backFilePath, true);

        using var aes = BundleCrypto.CreateAes();

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

    /// <summary>确定要 patch 的 bundle：命令行给了就用，否则从资源服务器下载。</summary>
    static string? ResolvePatchFile(CommandContext context, List<string> args, string sourceFilePath)
    {
        string? patchFilePath;
        if (args.Count < 3 && Console.IsInputRedirected is false)
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
            patchFilePath = args[2];
        }

        if (!string.IsNullOrWhiteSpace(patchFilePath))
        {
            return patchFilePath;
        }

        var index = UpdateIndexCommand.Load(context.IndexPath);
        // 根据源文件名推断 bundle 名
        if (index.Items.GetValueOrDefault(Path.GetFileName(sourceFilePath) + ".unity3d") is not { } item)
        {
            Console.WriteLine("资源 id not found...可以尝试 update index 的说");
            return null;
        }

        var assetUrl = AssetUrls.ForBundle(index.Version, item.Name);
        Console.WriteLine($"{assetUrl} download 中...");
        using var httpClient = new HttpClient();
        using var response = httpClient.GetStreamAsync(assetUrl).Result;
        var downloaded = sourceFilePath + ".unity3d";
        using var fileStream = File.Create(downloaded);
        response.CopyTo(fileStream);
        return downloaded;
    }
}
