using System.Security.Cryptography;
using System.Text;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using LZ4ps;

namespace Roco;

/// <summary>What one bundle conversion produced.</summary>
public sealed record BundleConversionResult(
    bool PreservedExistingWebGl,
    string Version,
    string Compression,
    int TextureCount,
    int ObjectCount,
    int SerializedFileCount,
    int RemovedStreams,
    long OutputBytes,
    IReadOnlyList<string> Messages
);

/// <summary>
/// Standalone (no Unity) port of the Unity editor converter of the same name. It rewrites an Android asset bundle so
/// a WebGL player can load it, keeping the original behavior: textures are inlined into their objects, the platform is
/// changed to WebGL, the streams that become unused are dropped and the result is LZ4-compressed. Every object hash,
/// the embedded resources and the serialized layout are verified after writing. Bundles that are already WebGL + LZ4
/// are preserved byte-for-byte.
///
/// The Unity editor of the target player is 6000.0.66f2; <see cref="SupportedVersions"/> mirrors the version gate of
/// the editor converter, and <see cref="TargetPlatformWebGl"/> is the platform the conversion writes. Unlike the
/// editor converter, this port has no editor to enumerate Unity's built-in resources, so external references into
/// <c>unity default resources</c> and <c>unity_builtin_extra</c> are accepted without an editor-side check.
/// </summary>
public static class ModelBundlePlatformConverter
{
    /// <summary>Android, the platform the downloaded bundles are built for.</summary>
    public const uint TargetPlatformAndroid = 13;

    /// <summary>WebGL, the platform the converted bundles carry.</summary>
    public const uint TargetPlatformWebGl = 20;

    /// <summary>The Unity editor version of the target player, and the version whose serialization is verified.</summary>
    public const string UnityEditorVersion = "6000.0.66f2";

    /// <summary>The size of the data blocks of a chunk-compressed bundle.</summary>
    const int BlockSize = 0x20000;

    /// <summary>
    /// Serialization versions the Android rewrite is verified against. A WebGL bundle skips this gate entirely, so an
    /// already-converted bundle from a newer editor is still preserved byte-for-byte.
    /// </summary>
    public static readonly string[] SupportedVersions =
    [
        "2018.4.",
        "2020.3.",
        "2021.3.",
        "6000.0.66f2",
        "6000.2."
    ];

    /// <summary>
    /// Converts <paramref name="source"/> into a WebGL bundle at <paramref name="output"/>. Callers are expected to
    /// test whether <paramref name="output"/> already exists (a bundle of the same name that is already converted)
    /// and to skip the call in that case. The output is written to a temporary file next to <paramref name="output"/>
    /// and only moved over <paramref name="output"/> after the conversion has been verified, so a failed or
    /// interrupted conversion never leaves a partial bundle behind.
    /// </summary>
    /// <param name="source">An Android (or already converted WebGL) bundle.</param>
    /// <param name="output">Destination path of the converted bundle.</param>
    /// <param name="textOnly">
    /// True for bundles that only carry TextAsset and AssetBundle objects (for example the master data bundles), which
    /// are checked for unexpected objects instead of being treated as portable model data.
    /// </param>
    public static BundleConversionResult Convert(string source, string output, bool textOnly = false)
    {
        if (string.IsNullOrWhiteSpace(source)) throw new ArgumentException("Source path is required.", nameof(source));
        if (string.IsNullOrWhiteSpace(output)) throw new ArgumentException("Output path is required.", nameof(output));
        var sourcePath = Path.GetFullPath(source);
        var outputPath = Path.GetFullPath(output);
        if (!File.Exists(sourcePath)) throw new FileNotFoundException("Bundle to convert not found.", sourcePath);
        if (string.Equals(sourcePath, outputPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Conversion source and output are the same file: " + sourcePath);
        var directory = Path.GetDirectoryName(outputPath);
        if (string.IsNullOrEmpty(directory)) throw new InvalidOperationException("Output path has no directory: " + outputPath);
        Directory.CreateDirectory(directory);
        // A file name, so the temporary files this conversion creates never collide with another worker's.
        var nonce = Guid.NewGuid().ToString("N");
        var temporary = outputPath + "." + nonce + ".tmp";
        var packed = outputPath + "." + nonce + ".lz4.tmp";
        var manager = new AssetsManager();
        try
        {
            var result = ConvertSingle(manager, sourcePath, temporary, packed, textOnly);
            // The verified file is complete; make it visible under its real name only now.
            Install(temporary, outputPath);
            return result with { OutputBytes = new FileInfo(outputPath).Length };
        }
        finally
        {
            // Never leave worker output behind, on success, failure or cancellation.
            manager.UnloadAll(true);
            if (File.Exists(temporary)) File.Delete(temporary);
            if (File.Exists(packed)) File.Delete(packed);
        }
    }

    static BundleConversionResult ConvertSingle(AssetsManager manager, string source, string temporary, string packed,
        bool textOnly)
    {
        var messages = new List<string>();
        var sourceHash = HashFile(source);
        // Read from the header rather than GetCompressionType, which reports the directory-info compression (almost
        // always None) and would hide that an input is already LZ4.
        var compression = CompressionMode(source);
        var bundle = OpenBundle(manager, source, true);
        var indices = SerializedIndices(bundle.file);
        if (indices.Length == 0)
            throw new InvalidDataException("Bundle has no serialized file: " + source);
        var files = indices.Select(index => manager.LoadAssetsFileFromBundle(bundle, index, false)).ToArray();
        var version = files[0].file.Metadata.UnityVersion;
        var platform = files[0].file.Metadata.TargetPlatform;
        if (platform != TargetPlatformAndroid && platform != TargetPlatformWebGl)
            throw new NotSupportedException("Expected Android or WebGL bundle: " + source);
        if (files.Any(file => file.file.Metadata.UnityVersion != version
                || file.file.Metadata.TargetPlatform != platform))
            throw new NotSupportedException("Serialized files differ in version or platform: " + source);
        // Archive paths are case-insensitive: 2018.4 scenes name archive:/buildplayer-x/buildplayer-x.sharedassets.
        var siblings = indices
            .Zip(files, (index, file) => (name: bundle.file.BlockAndDirInfo.DirectoryInfos[index].Name, file))
            .ToDictionary(entry => entry.name,
                entry => new HashSet<long>(entry.file.file.AssetInfos.Select(info => info.PathId)),
                StringComparer.OrdinalIgnoreCase);
        var crossBundleExternals = 0;
        var unresolvedReferences = 0;
        foreach (var file in files)
        {
            var (bundleExternals, references) = CheckExternals(manager, file, siblings, source);
            crossBundleExternals += bundleExternals;
            unresolvedReferences += references;
        }
        // A directory name is only a routing hint. Reject unexpected native or scripted objects rather than silently
        // treating them as portable data.
        if (textOnly)
        {
            var file = files[0];
            var textCount = file.file.GetAssetsOfType(AssetClassID.TextAsset).Count();
            var bundleCount = file.file.GetAssetsOfType(AssetClassID.AssetBundle).Count();
            if (files.Length != 1 || textCount == 0 || bundleCount != 1
                || textCount + bundleCount != file.file.AssetInfos.Count)
                throw new NotSupportedException("Expected only TextAsset and AssetBundle objects: " + source);
        }
        // Already-converted bundles need no serialization rewrite. In particular, a WebGL bundle built by a newer
        // editor must not hit the Android rewrite version gate. Preserve its objects and embedded streams
        // byte-for-byte; an LZ4 input is copied whole, any other is only repacked to LZ4. This is not a claim that
        // arbitrary newer bundles work in older players.
        var webgl = platform == TargetPlatformWebGl;
        if (webgl && compression is 2 or 3)
        {
            manager.UnloadAll(true);
            CopyVerified(source, temporary, sourceHash);
            if (sourceHash != HashFile(source))
                throw new IOException("Source changed during copy: " + source);
            return new BundleConversionResult(true, version, CompressionName(compression), 0, 0, files.Length, 0,
                new FileInfo(temporary).Length,
                [$"existing WebGL bundle preserved byte-for-byte ({version})"]);
        }
        // Read texture fields using each bundle's own TypeTree. Keep unknown versions gated; object hashes are
        // verified after writing.
        if (!webgl && !SupportedVersions.Any(supported => version.StartsWith(supported, StringComparison.Ordinal)))
            throw new NotSupportedException("Unverified Android serialization version: " + version + " in " + source);
        var expected = files
            .Select(file => file.file.AssetInfos.ToDictionary(info => info.PathId, info => ObjectHash(file, info)))
            .ToArray();
        var replaced = files.Select(_ => new Dictionary<long, byte[]>()).ToArray();
        var resources = ResourceHashes(bundle.file);
        var textureCount = 0;
        var directories = bundle.file.BlockAndDirInfo.DirectoryInfos;
        var names = indices.Select(index => directories[index].Name).ToArray();
        var removed = new HashSet<string>();
        // LZ4 chunks, as the native route's BuildAssetBundleOptions.ChunkBasedCompression.
        if (webgl)
        {
            PackLz4(bundle.file, packed);
        }
        else
        {
            for (var f = 0; f < files.Length; f++)
                foreach (var info in Textures(files[f]))
                {
                    var field = manager.GetBaseField(files[f], info);
                    var stream = field["m_StreamData"];
                    if (stream.IsDummy)
                        throw new InvalidDataException("Texture stream layout missing: " + source);
                    var size = stream["size"].AsUInt;
                    if (size == 0)
                        continue;
                    if (field["image data"].AsByteArray.Length != 0)
                        throw new InvalidDataException("Texture has both inline and streamed data: " + source);
                    var name = stream["path"].AsString.Replace('\\', '/').Split('/').Last();
                    var matches = directories.Select((entry, index) => new { entry, index })
                        .Where(item => item.entry.Name == name).ToArray();
                    if (matches.Length != 1 || matches[0].entry.IsSerialized)
                        throw new InvalidDataException("Missing or ambiguous embedded texture stream: " + name);
                    bundle.file.GetFileRange(matches[0].index, out var start, out var length);
                    var offset = stream["offset"].AsULong;
                    if (offset > (ulong)length || size > (ulong)length - offset || size > int.MaxValue)
                        throw new InvalidDataException("Texture stream range outside resource: " + name);
                    bundle.file.DataReader.Position = checked(start + (long)offset);
                    var bytes = bundle.file.DataReader.ReadBytes((int)size);
                    if (bytes.Length != size)
                        throw new EndOfStreamException(name);
                    field["image data"].AsByteArray = bytes;
                    stream["offset"].AsULong = 0;
                    stream["size"].AsUInt = 0;
                    stream["path"].AsString = "";
                    var serialized = field.WriteToByteArray();
                    expected[f][info.PathId] = Hash(serialized);
                    replaced[f][info.PathId] = serialized;
                    info.SetNewData(serialized);
                    textureCount++;
                }
            for (var f = 0; f < files.Length; f++)
            {
                files[f].file.Metadata.TargetPlatform = TargetPlatformWebGl;
                directories[indices[f]].SetNewData(files[f].file);
            }
            // Inlined textures leave their .resS stream unused. Other objects (meshes, audio, textures that did not
            // stream) may still use a resource entry; it stays byte-for-byte while any object names it.
            foreach (var entry in directories.Where(entry => !entry.IsSerialized))
                if (!IsReferenced(entry.Name, files, replaced))
                {
                    entry.SetRemoved();
                    removed.Add(entry.Name);
                }
            resources = resources
                .Where(resource => !removed.Contains(resource.Substring(0, resource.LastIndexOf(':'))))
                .ToArray();
            using (var writer = new AssetsFileWriter(File.Create(temporary)))
                bundle.file.Write(writer);
            manager.UnloadAll(true);
            var unpacked = OpenBundle(manager, temporary, false);
            PackLz4(unpacked.file, packed);
        }
        manager.UnloadAll(true);
        RequireLz4Bundle(packed, source);
        manager.UnloadAll(true);
        bundle = OpenBundle(manager, packed, true);
        var reloaded = SerializedIndices(bundle.file);
        if (!reloaded.Select(index => bundle.file.BlockAndDirInfo.DirectoryInfos[index].Name).SequenceEqual(names))
            throw new InvalidDataException("Converted serialized file layout changed: " + source);
        for (var f = 0; f < reloaded.Length; f++)
        {
            var file = manager.LoadAssetsFileFromBundle(bundle, reloaded[f], false);
            if (file.file.Metadata.UnityVersion != version
                || file.file.Metadata.TargetPlatform != TargetPlatformWebGl
                || file.file.AssetInfos.Count != expected[f].Count)
                throw new InvalidDataException("Converted metadata verification failed: " + source);
            foreach (var info in file.file.AssetInfos)
                if (!expected[f].TryGetValue(info.PathId, out var hash) || hash != ObjectHash(file, info))
                    throw new InvalidDataException("Converted object verification failed: " + info.PathId);
            if (!webgl)
                foreach (var info in Textures(file))
                    if (manager.GetBaseField(file, info)["m_StreamData.size"].AsUInt != 0)
                        throw new InvalidDataException("Texture still has external data: " + source);
        }
        if (!resources.SequenceEqual(ResourceHashes(bundle.file)))
            throw new InvalidDataException("Embedded resources changed: " + source);
        manager.UnloadAll(true);
        if (sourceHash != HashFile(source))
            throw new IOException("Source changed during conversion: " + source);
        // The verified file is the packed one. Everything downstream (the caller's move, the returned size) uses
        // `temporary`, so hand it over only now, after the whole verification above has passed.
        Install(packed, temporary);
        var objects = expected.Sum(entries => entries.Count);
        messages.Add(webgl
            ? $"existing WebGL bundle repacked from {CompressionName(compression)} ({version})"
            : $"{textureCount} textures inlined, {removed.Count} unused streams dropped");
        messages.Add($"{objects} objects verified"
            + (files.Length > 1 ? $" in {files.Length} serialized files" : "")
            + (crossBundleExternals == 0
                ? ""
                : $"; {unresolvedReferences} references into {crossBundleExternals} other bundle(s), kept as-is"));
        // Reached only when a rewrite happened: an already WebGL + LZ4 bundle returned above.
        return new BundleConversionResult(false, version,
            CompressionName(CompressionMode(temporary)), textureCount, objects,
            files.Length, removed.Count, new FileInfo(temporary).Length, messages);
    }

    /// <summary>
    /// Moves a finished file over its destination. <see cref="File.Replace"/> is the atomic form and keeps the
    /// destination in place until the swap succeeds; where the platform refuses it, the destination is removed first
    /// and the move overwrites it, which is still a single rename of a complete file.
    /// </summary>
    public static void Install(string temporary, string destination)
    {
        if (!File.Exists(destination))
        {
            File.Move(temporary, destination);
            return;
        }
        try
        {
            File.Replace(temporary, destination, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            File.Move(temporary, destination, true);
        }
    }

    /// <summary>
    /// Writes the unpacked <paramref name="bundle"/> to <paramref name="path"/> the way a native
    /// <c>BuildAssetBundleOptions.ChunkBasedCompression</c> build does: the block and directory info first, then the
    /// data in LZ4HC blocks of <see cref="BlockSize"/> bytes, where a block that LZ4 does not make smaller is stored
    /// as-is with mode 0. <c>AssetBundleFile.Pack</c> is not used because it only stores a block that grew: a block
    /// that compresses to exactly its own size is written as LZ4 there. A native build never produces such a block
    /// and the player cannot read it — loading from it fails with "The file 'archive:/CAB-…' is corrupted!"
    /// (<c>dan_union1_01.imo</c>; dance motions and audio are close to incompressible, so the sizes do meet).
    /// </summary>
    static void PackLz4(AssetBundleFile bundle, string path)
    {
        var blocks = new List<AssetBundleBlockInfo>();
        using var data = new MemoryStream();
        var reader = bundle.DataReader;
        reader.Position = 0;
        for (var block = reader.ReadBytes(BlockSize); block.Length != 0; block = reader.ReadBytes(BlockSize))
        {
            var compressed = LZ4Codec.Encode32HC(block, 0, block.Length);
            var shrunk = compressed.Length < block.Length;
            var stored = shrunk ? compressed : block;
            data.Write(stored, 0, stored.Length);
            blocks.Add(new AssetBundleBlockInfo
            {
                CompressedSize = (uint)stored.Length,
                DecompressedSize = (uint)block.Length,
                Flags = (ushort)(shrunk ? 3 : 0)
            });
        }
        var info = new AssetBundleBlockAndDirInfo
        {
            Hash = new Hash128(),
            BlockInfos = blocks.ToArray(),
            DirectoryInfos = bundle.BlockAndDirInfo.DirectoryInfos
        };
        byte[] infoBytes;
        using (var stream = new MemoryStream())
        {
            info.Write(new AssetsFileWriter(stream) { BigEndian = true });
            infoBytes = stream.ToArray();
        }
        var packedInfo = LZ4Codec.Encode32HC(infoBytes, 0, infoBytes.Length);
        var header = new AssetBundleHeader
        {
            Signature = bundle.Header.Signature,
            Version = bundle.Header.Version,
            GenerationVersion = bundle.Header.GenerationVersion,
            EngineVersion = bundle.Header.EngineVersion,
            FileStreamHeader = new AssetBundleFSHeader
            {
                CompressedSize = (uint)packedInfo.Length,
                DecompressedSize = (uint)infoBytes.Length,
                Flags = AssetBundleFSHeaderFlags.LZ4HCCompressed | AssetBundleFSHeaderFlags.HasDirectoryInfo
            }
        };
        using var writer = new AssetsFileWriter(File.Create(path));
        // The header's length does not depend on its sizes: write it once to learn where it ends, then again.
        WriteHeader(writer, header);
        header.FileStreamHeader.TotalFileSize = writer.Position + packedInfo.Length + data.Length;
        writer.Write(packedInfo);
        data.WriteTo(writer.BaseStream);
        writer.Position = 0;
        WriteHeader(writer, header);
    }

    static void WriteHeader(AssetsFileWriter writer, AssetBundleHeader header)
    {
        header.Write(writer);
        if (header.Version >= 7)
            writer.Align16();
    }

    /// <summary>
    /// Verifies that <paramref name="path"/> is LZ4-compressed the way a native
    /// <c>BuildAssetBundleOptions.ChunkBasedCompression</c> bundle is: the compression mode in the UnityFS header is
    /// LZ4 or LZ4HC, and every data block is either an LZ4 block that is smaller than its data or an uncompressed
    /// block whose compressed and decompressed sizes are equal. LZ4 cannot shrink data that is already compressed,
    /// and a chunk that does not shrink is stored as-is with mode 0 — the audio banks (<c>.acb</c>) hit this, so
    /// requiring every block to be LZ4 would reject bundles that are perfectly fine. An LZ4 block that is not smaller
    /// than its data is rejected: the player cannot read it (see <see cref="PackLz4"/>). <c>GetCompressionType</c>
    /// cannot be used to check any of this, because it reports the directory-info compression, which is a different
    /// thing and is usually "None".
    /// </summary>
    static void RequireLz4Bundle(string path, string source)
    {
        var mode = CompressionMode(path);
        if (mode is not (2 or 3))
            throw new InvalidDataException(
                $"Converted bundle is not LZ4-compressed (header compression mode {mode}): " + source);
        var manager = new AssetsManager();
        try
        {
            var bundle = OpenBundle(manager, path, false);
            if (bundle.file.BlockAndDirInfo.BlockInfos.Length == 0)
                throw new InvalidDataException("Converted bundle has no data blocks: " + source);
            foreach (var block in bundle.file.BlockAndDirInfo.BlockInfos)
            {
                var blockMode = (uint)block.Flags & 0x3f;
                if (blockMode is 2 or 3 && block.CompressedSize < block.DecompressedSize)
                    continue;
                // 存不下就原样存的块：声明成未压缩就必须真的是原样大小
                if (blockMode == 0 && block.CompressedSize == block.DecompressedSize)
                    continue;
                throw new InvalidDataException(
                    $"Converted bundle has a bad data block (mode {blockMode}, {block.CompressedSize}/{block.DecompressedSize} bytes): "
                    + source);
            }
        }
        finally
        {
            manager.UnloadAll(true);
        }
    }

    /// <summary>
    /// Whether the header and block list of the converted bundle at <paramref name="path"/> can be read and pass
    /// <see cref="RequireLz4Bundle"/>; the data itself is not read. A bundle converted before <see cref="PackLz4"/>
    /// can hold an LZ4 block that is not smaller than its data, and the caller converts a bundle that fails this
    /// check again instead of keeping it.
    /// </summary>
    public static bool IsLz4Bundle(string path)
    {
        try
        {
            RequireLz4Bundle(path, path);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// The compression mode in the first 6 bits of the UnityFS header of <paramref name="path"/>: 0 none, 1 LZMA,
    /// 2 LZ4, 3 LZ4HC.
    /// </summary>
    static uint CompressionMode(string path)
    {
        using var stream = File.OpenRead(path);
        var header = new AssetBundleHeader();
        header.Read(new AssetsFileReader(stream));
        return (uint)header.FileStreamHeader.Flags & 0x3f;
    }

    static string CompressionName(uint mode) => mode switch
    {
        1 => "LZMA",
        2 => "LZ4",
        3 => "LZ4HC",
        _ => "None"
    };

    /// <summary>
    /// Checks where a serialized file is allowed to reference objects outside itself: Unity's built-in resources
    /// (<c>unity default resources</c> / <c>unity_builtin_extra</c>, which every player has) and, for a streamed
    /// scene, another serialized file of the same bundle. References into <em>other</em> bundles are cross-bundle
    /// dependencies: they cannot be checked here, because the other bundles are separate files that may not even be
    /// downloaded yet, so they are counted and reported rather than refused. The conversion does not touch the
    /// external list, so those references keep pointing at the same bundles.
    /// </summary>
    static (int CrossBundleExternals, int UnresolvedReferences) CheckExternals(AssetsManager manager,
        AssetsFileInstance file, Dictionary<string, HashSet<long>> siblings, string source)
    {
        var externals = file.file.Metadata.Externals;
        if (externals.Count == 0)
            return (0, 0);
        var allowed = new HashSet<long>?[externals.Count];
        var unresolvedReferences = 0;
        var crossBundleExternals = 0;
        for (var i = 0; i < externals.Count; i++)
        {
            var external = externals[i];
            var guid = external.Guid.ToString();
            // Unity's built-in resources: "unity default resources" and "unity_builtin_extra", which every player has.
            if (external.Type == AssetsFileExternalType.Normal
                && (guid == "0000000000000000e000000000000000" || guid == "0000000000000000f000000000000000"))
            {
                continue;
            }
            // archive:/<archive>/<file> with no GUID: a sibling inside this same bundle (a streamed scene's shared
            // assets), which stays in the bundle and is therefore checkable.
            var archive = external.PathName.StartsWith("archive:/", StringComparison.Ordinal)
                ? external.PathName.Substring(external.PathName.LastIndexOf('/') + 1)
                : null;
            if (external.Type == AssetsFileExternalType.Normal
                && guid == "00000000000000000000000000000000"
                && archive is not null
                && siblings.TryGetValue(archive, out var sibling))
            {
                allowed[i] = sibling;
                continue;
            }
            // Anything else points into another bundle.
            crossBundleExternals++;
        }
        foreach (var info in file.file.AssetInfos)
            Check(manager.GetBaseField(file, info));

        void Check(AssetTypeValueField field)
        {
            if (field.TypeName.StartsWith("PPtr<", StringComparison.Ordinal))
            {
                var fileId = field["m_FileID"].AsInt;
                var pathId = field["m_PathID"].AsLong;
                if (fileId < 0 || fileId > externals.Count)
                    throw new InvalidDataException("Unknown external reference " + fileId + ":" + pathId + " in " + source);
                if (fileId == 0)
                    return;
                if (allowed[fileId - 1] is { } ids)
                {
                    if (!ids.Contains(pathId))
                        throw new InvalidDataException(
                            "Unknown reference into a serialized file of the same bundle " + fileId + ":" + pathId + " in " + source);
                    return;
                }
                // A built-in resource or a cross-bundle dependency: nothing local to check the path ID against.
                unresolvedReferences++;
                return;
            }
            foreach (var child in field.Children)
                Check(child);
        }

        return (crossBundleExternals, unresolvedReferences);
    }

    /// <summary>
    /// Directory indices of the bundle's serialized files: one for an asset bundle, the shared assets and the scene
    /// for a streamed scene bundle. The SerializedFile flag decides; <c>AssetBundleFile.IsAssetsFile</c> guesses from
    /// the contents and takes some .resS streams for serialized files.
    /// </summary>
    static int[] SerializedIndices(AssetBundleFile bundle) =>
        Enumerable.Range(0, bundle.BlockAndDirInfo.DirectoryInfos.Count)
            .Where(index => bundle.BlockAndDirInfo.DirectoryInfos[index].IsSerialized).ToArray();

    static BundleFileInstance OpenBundle(AssetsManager manager, string path, bool unpackIfPacked)
    {
        // LoadBundleFile(path) opens the file itself and leaves it open when the bundle cannot be read: the manager
        // only closes the bundles it loaded. An unreadable converted bundle must be free for its replacement.
        var stream = File.OpenRead(path);
        try
        {
            return manager.LoadBundleFile(stream, unpackIfPacked);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    static void CopyVerified(string source, string output, string hash)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var temporary = output + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.Copy(source, temporary);
            if (HashFile(temporary) != hash) throw new IOException("Copied output hash mismatch: " + source);
            Install(temporary, output);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    /// <summary>
    /// Whether any object, as it will be written, names the resource entry: StreamingInfo and StreamedResource paths
    /// are archive:/&lt;archive&gt;/&lt;entry name&gt;.
    /// </summary>
    static bool IsReferenced(string name, AssetsFileInstance[] files, Dictionary<long, byte[]>[] replaced)
    {
        var pattern = Encoding.ASCII.GetBytes(name);
        for (var f = 0; f < files.Length; f++)
            foreach (var info in files[f].file.AssetInfos)
                if (IndexOf(replaced[f].TryGetValue(info.PathId, out var bytes) ? bytes : ObjectBytes(files[f], info),
                        pattern) >= 0)
                    return true;
        return false;
    }

    static int IndexOf(byte[] data, byte[] pattern)
    {
        for (var i = 0; i <= data.Length - pattern.Length; i++)
        {
            var j = 0;
            while (j < pattern.Length && data[i + j] == pattern[j])
                j++;
            if (j == pattern.Length)
                return i;
        }
        return -1;
    }

    static IEnumerable<AssetFileInfo> Textures(AssetsFileInstance file) =>
        file.file.GetAssetsOfType(AssetClassID.Texture2D).Concat(file.file.GetAssetsOfType(AssetClassID.Cubemap));

    static string Hash(byte[] bytes)
    {
        using var hash = SHA256.Create();
        return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "");
    }

    static string HashFile(string path)
    {
        using var hash = SHA256.Create();
        using var stream = File.OpenRead(path);
        return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "");
    }

    static byte[] ObjectBytes(AssetsFileInstance file, AssetFileInfo info)
    {
        file.file.Reader.Position = info.GetAbsoluteByteOffset(file.file);
        return file.file.Reader.ReadBytes(checked((int)info.ByteSize));
    }

    static string ObjectHash(AssetsFileInstance file, AssetFileInfo info) => Hash(ObjectBytes(file, info));

    /// <summary>The bundle's non-serialized entries (.resS / .resource streams).</summary>
    static string[] ResourceHashes(AssetBundleFile bundle) =>
        Enumerable.Range(0, bundle.BlockAndDirInfo.DirectoryInfos.Count)
            .Where(index => !bundle.BlockAndDirInfo.DirectoryInfos[index].IsSerialized).Select(index =>
            {
                bundle.GetFileRange(index, out var start, out var length);
                bundle.DataReader.Position = start;
                return bundle.BlockAndDirInfo.DirectoryInfos[index].Name + ":"
                    + Hash(bundle.DataReader.ReadBytes(checked((int)length)));
            }).ToArray();
}


