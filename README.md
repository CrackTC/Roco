# Usage

构建：

```
dotnet build Roco/Roco.csproj
```

之后用 `Roco/bin/Debug/net10.0/Roco.exe` 运行。

```
使用方法：
解密: Roco [--index <index路径>] [--download <下载目录>] decrypt <bundle文件路径>
加密: Roco [--index <index路径>] [--download <下载目录>] encrypt <要 patch 的源文件> [bundle文件路径]
更新资源 index: Roco [--index <index路径>] update-index
下载全部资源:   Roco [--index <index路径>] [--download <下载目录>] --converted <已转换包目录> [--limit <个数>] download-all

选项：
  --index <路径>     指定 index.json 的路径（默认: index.json）
  --download <目录>  指定未转换包的下载目录（默认: downloads）
  --converted <目录> 指定已转换包的输出目录；每下载完成/跳过一个包就转换成 WebGL 包放进去，
                     该目录下已有通过校验、不比源包旧的同名包则跳过转换（对 download-all 必填）
  --limit <个数>     只处理前 N 个资源，用于小批量试跑（默认: 0，即全部）
```

先小批量试跑再跑全量：

```
Roco.exe --converted converted --limit 20 download-all
```

# 资源转换

`download-all` 会把未转换的包放在 `--download`，把转换好的包写到 `--converted`：每个包下载完成或者因为
本地已有同名同大小的包而跳过后，都会调用 `ModelBundlePlatformConverter` 把它转成 WebGL 包。
该目录里有同名包时直接跳过转换；同名包比源包旧（源包重新下载过），或者没通过校验（读不出包头或块表，
或者带着旧版本转出的、没压小却标成 LZ4 的块，播放器读不了）时会重新转换。

源包是不是最新，先看 index 里的 size，再看 hash：`--download` 目录里的 `.downloaded.json` 记着每个源包对应
index 里的哪个 hash。hash 没变就当作文件没变；hash 变了而 size 没变的包会重新下载，和原来的源包比较内容，
内容真的变了才换掉（两个 index 版本之间抽查 10 个这样的包，只有 1 个内容变了）。没有记录的包按 size 接受，并记成
index 当前的 hash，所以在那之前发生的、size 没变的内容变化要等它的 hash 再变一次才会被发现。

`--limit` 只处理 index 里的前 N 个资源，试跑时用，避免一上来就拉全量。

转换后的包和下载下来的包**同名**：用的是 index 里的 key（本地文件名，形如 `ch_ex086_011ami.unity3d`），
不是 `item.Name`（资源服务器上的 hash 文件名，形如 `9cc90a32….unity3d`）。下载时 `item.Name` 只用来拼 URL。

拉不到最新 index 时（网络不通等）会用本地 `index.json` 继续，两边都没有才报错。

## 代码结构

每个命令的实现单独一个文件（`Roco/Commands/`）：

| 文件 | 内容 |
| --- | --- |
| `Roco/Program.cs` | 入口：解析参数、分发命令、统一报错 |
| `Roco/CliOptions.cs` | 命令行选项、用法说明、命令上下文 |
| `Roco/Commands/UpdateIndexCommand.cs` | `update-index`，以及被其它命令复用的 index 读取 |
| `Roco/Commands/DecryptCommand.cs` | `decrypt` |
| `Roco/Commands/EncryptCommand.cs` | `encrypt` |
| `Roco/Commands/DownloadAllCommand.cs` | `download-all`，下载 + 逐包转换 |
| `Roco/ModelBundlePlatformConverter.cs` | bundle → WebGL 的转换实现 |
| `Roco/IndexModels.cs` | index.json 的数据模型与序列化上下文 |
| `Roco/BundleCrypto.cs` | `decrypt`/`encrypt` 共用的密钥 |
| `Roco/AssetUrls.cs` | 资源服务器地址 |

转换沿用 Unity 编辑器里同名 converter 的行为：把纹理数据内联进对象、把平台改成 WebGL、
丢掉因此不再被引用的 `.resS` 流并用 LZ4 压缩，写入后再逐个校验对象哈希、序列化文件布局和内嵌资源。
本来就是 WebGL + LZ4 的包不会被重写，而是逐字节保留。

转换结果先写到 `--converted` 下的临时文件，全部校验通过后才改名为正式包，所以转换失败或中断不会留下
半个包。目标包的替换优先用原子替换，平台不支持时再退回到“删除后改名”。

目标播放器使用的 Unity 编辑器版本是 6000.0.66f2；可转换的 Android 序列化版本见
`ModelBundlePlatformConverter.SupportedVersions`。

# Credits
[matsurihi.me](https://matsurihi.me): 资源版本api
