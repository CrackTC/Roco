namespace Roco;

/// <summary>命令行选项，以及 -- 之后的命令和它的参数。</summary>
internal sealed record CliOptions(
    string IndexPath,
    string DownloadPath,
    string? ConvertedPath,
    int Limit,
    string? Command,
    List<string> RemainingArgs)
{
    /// <summary>解析命令行；失败时返回 false 并给出要打印的错误。</summary>
    public static bool TryParse(string[] args, out CliOptions options, out string? error)
    {
        var indexPath = "index.json";
        var downloadPath = "downloads";
        string? convertedPath = null;
        var limit = 0;
        var remainingArgs = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--index" when i + 1 < args.Length:
                    indexPath = args[++i];
                    break;
                case "--download" when i + 1 < args.Length:
                    downloadPath = args[++i];
                    break;
                case "--converted" when i + 1 < args.Length:
                    convertedPath = args[++i];
                    break;
                case "--limit" when i + 1 < args.Length:
                    var text = args[++i];
                    if (!int.TryParse(text, out limit) || limit < 0)
                    {
                        options = null!;
                        error = $"--limit 需要一个非负整数，收到：{text}";
                        return false;
                    }
                    break;
                default:
                    remainingArgs.Add(args[i]);
                    break;
            }
        }

        options = new CliOptions(indexPath, downloadPath, convertedPath, limit,
            remainingArgs.Count > 0 ? remainingArgs[0] : null, remainingArgs);
        error = null;
        return true;
    }

    public static void PrintUsage()
    {
        var name = AppDomain.CurrentDomain.FriendlyName;
        Console.WriteLine("使用方法：");
        Console.WriteLine($"解密: {name} [--index <index路径>] [--download <下载目录>] decrypt <bundle文件路径>");
        Console.WriteLine($"加密: {name} [--index <index路径>] [--download <下载目录>] encrypt <要 patch 的源文件> [bundle文件路径]");
        Console.WriteLine($"更新资源 index: {name} [--index <index路径>] update-index");
        Console.WriteLine($"下载全部资源:   {name} [--index <index路径>] [--download <下载目录>] --converted <已转换包目录> [--limit <个数>] download-all");
        Console.WriteLine();
        Console.WriteLine("选项：");
        Console.WriteLine("  --index <路径>     指定 index.json 的路径（默认: index.json）");
        Console.WriteLine("  --download <目录>  指定未转换包的下载目录（默认: downloads）");
        Console.WriteLine("  --converted <目录> 指定已转换包的输出目录；每下载完成/跳过一个包就转换成 WebGL 包放进去，" +
            "该目录下已有通过校验、不比源包旧的同名包则跳过转换（对 download-all 必填）");
        Console.WriteLine("  --limit <个数>     只处理前 N 个资源，用于小批量试跑（默认: 0，即全部）");
    }
}

/// <summary>命令运行时用到的全局路径。</summary>
internal sealed record CommandContext(string IndexPath, string DownloadPath);
