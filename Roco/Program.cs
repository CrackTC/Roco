using AssetsTools.NET;
using AssetsTools.NET.Extra;
using Nerdbank.MessagePack;
using PolyType;
using Roco.Commands;

namespace Roco;

internal static class Program
{
    static void Main(string[] args)
    {
        if (!CliOptions.TryParse(args, out var options, out var parseError))
        {
            Console.WriteLine(parseError);
            return;
        }

        try
        {
            if (options.Command is null)
            {
                CliOptions.PrintUsage();
                return;
            }

            Run(options);
        }
        catch (Exception ex)
        {
            // 网络和文件系统的失败都以一条错误信息结束，而不是一堆堆栈
            Console.WriteLine($"执行失败：{ex.Message}");
        }
    }

    static void Run(CliOptions options)
    {
        var context = new CommandContext(options.IndexPath, options.DownloadPath);
        switch (options.Command)
        {
            case "update-index":
                UpdateIndexCommand.Run(context);
                Console.WriteLine("资源 index 的 update finished 的说");
                return;

            case "decrypt":
                WarnConvertedIsIgnored(options);
                DecryptCommand.Run(context, options.RemainingArgs);
                return;

            case "encrypt":
                WarnConvertedIsIgnored(options);
                EncryptCommand.Run(context, options.RemainingArgs);
                return;

            case "download-all":
                if (DownloadAllCommand.Run(context, options))
                {
                    Console.WriteLine("全部资源 download finished 的说");
                }
                return;

            default:
                Console.WriteLine($"未知的命令：{options.Command}");
                Console.WriteLine("可用命令：decrypt, encrypt, update-index, download-all");
                return;
        }
    }

    static void WarnConvertedIsIgnored(CliOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.ConvertedPath))
        {
            Console.WriteLine("--converted 只对 download-all 有效的说");
        }
    }
}
