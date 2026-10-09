using System.Text.Json;
using System.Text.Json.Serialization;
using Nerdbank.MessagePack;
using PolyType;

namespace Roco;

[GenerateShapeFor<List<Dictionary<string, IndexItem>>>]
public partial record IndexItem(
    [property: Key(0)] string Hash,
    [property: Key(1)] string Name,
    [property: Key(2)] uint Size
);

public record AssetIndex(int Version, Dictionary<string, IndexItem> Items);

[JsonSerializable(typeof(AssetIndex))]
internal partial class AssetServiceJsonSerializerContext : JsonSerializerContext;
