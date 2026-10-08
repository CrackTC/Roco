using AssetsTools.NET;
using AssetsTools.NET.Extra;

namespace Roco.Commands;

/// <summary>
/// 解密：把 bundle 里 TextAsset 的 m_Script 用固定密钥解出来，按 TextAsset 的 m_Name 写到 bundle 旁边。
/// </summary>
internal static class DecryptCommand
{
    public static void Run(CommandContext context, List<string> args)
    {
        if (args.Count < 2)
        {
            Console.WriteLine("使用方法：decrypt <bundle文件路径>");
            return;
        }

        var filePath = args[1];
        if (!File.Exists(filePath))
        {
            Console.WriteLine("文件 not exist 的说");
            return;
        }

        using var stream = File.OpenRead(filePath);
        using var aes = BundleCrypto.CreateAes();

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
}
