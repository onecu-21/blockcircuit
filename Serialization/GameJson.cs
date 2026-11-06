global using JsonSerializer = BlockCircuit.GameJson;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
namespace BlockCircuit;

// Keep supplied JSON options and wire/save formats; use compiled metadata for game DTOs.
public static class GameJson
{
    static readonly ConditionalWeakTable<JsonSerializerOptions,JsonSerializerOptions> cache=new();
    static readonly JsonSerializerOptions defaults=new();
    public static JsonSerializerOptions Options(JsonSerializerOptions? supplied=null)=>cache.GetValue(supplied??defaults,original=>{
        var options=new JsonSerializerOptions(original);
        options.TypeInfoResolver=JsonTypeInfoResolver.Combine(GameJsonContext.Default,new DefaultJsonTypeInfoResolver());
        return options;
    });
    static JsonTypeInfo<T> Info<T>(JsonSerializerOptions? options)=>(JsonTypeInfo<T>)Options(options).GetTypeInfo(typeof(T));
    public static string Serialize<T>(T value,JsonSerializerOptions? options=null)=>System.Text.Json.JsonSerializer.Serialize(value,Info<T>(options));
    public static byte[] SerializeToUtf8Bytes<T>(T value,JsonSerializerOptions? options=null)=>System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(value,Info<T>(options));
    public static JsonElement SerializeToElement<T>(T value,JsonSerializerOptions? options=null)=>System.Text.Json.JsonSerializer.SerializeToElement(value,Info<T>(options));
    public static T? Deserialize<T>(JsonElement value,JsonSerializerOptions? options=null)=>System.Text.Json.JsonSerializer.Deserialize(value,Info<T>(options));
    public static T? Deserialize<T>(string value,JsonSerializerOptions? options=null)=>System.Text.Json.JsonSerializer.Deserialize(value,Info<T>(options));
    public static T? Deserialize<T>(ReadOnlySpan<byte> value,JsonSerializerOptions? options=null)=>System.Text.Json.JsonSerializer.Deserialize(value,Info<T>(options));
}

[JsonSourceGenerationOptions(IncludeFields=true,GenerationMode=JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(World.SaveData))]
[JsonSerializable(typeof(World.SnapshotData))]
[JsonSerializable(typeof(ChunkWorldManifest))]
[JsonSerializable(typeof(ChunkFileData))]
[JsonSerializable(typeof(GameSettings))]
[JsonSerializable(typeof(WorldLibrary.Info))]
[JsonSerializable(typeof(global::Game.MultiplayerProfile))]
[JsonSerializable(typeof(NetMessage))]
[JsonSerializable(typeof(NetHello))]
[JsonSerializable(typeof(NetWelcome))]
[JsonSerializable(typeof(NetPose))]
[JsonSerializable(typeof(NetCommand))]
[JsonSerializable(typeof(NetCommandResult))]
[JsonSerializable(typeof(NetChunk))]
[JsonSerializable(typeof(NetState))]
[JsonSerializable(typeof(NetFailure))]
[JsonSerializable(typeof(MultiplayerTests.Probe))]
[JsonSerializable(typeof(PlayerInventoryState))]
[JsonSerializable(typeof(InventorySaveState))]
[JsonSerializable(typeof(FallingBlockSaveState))]
[JsonSerializable(typeof(Dictionary<string,NetworkPlayerSaveData>))]
[JsonSerializable(typeof(Block[]))]
internal partial class GameJsonContext:JsonSerializerContext;
