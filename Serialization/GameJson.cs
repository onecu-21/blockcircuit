global using JsonSerializer = BlockCircuit.GameJson;
using System.Runtime.CompilerServices;
using System.Collections;
using System.Reflection;
using System.Text;
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
    public static string Serialize<T>(T value,JsonSerializerOptions? options=null)=>Encoding.UTF8.GetString(SerializeToUtf8Bytes(value,options));
    public static byte[] SerializeToUtf8Bytes<T>(T value,JsonSerializerOptions? options=null)
    {
        if(GameJsonContext.Default.GetTypeInfo(typeof(T)) is not null)
            return System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(value,Info<T>(options));
        using var stream=new MemoryStream();
        using(var writer=new Utf8JsonWriter(stream,new JsonWriterOptions{Indented=options?.WriteIndented??false}))
            WriteDiagnostic(writer,value,options??defaults);
        return stream.ToArray();
    }
    // Anonymous diagnostic/test objects cannot have source-generation attributes.
    // Write their shape directly, without constructing generic reflection converters at runtime.
    static void WriteDiagnostic(Utf8JsonWriter writer,object? value,JsonSerializerOptions options)
    {
        if(value is null){writer.WriteNullValue();return;}
        var type=value.GetType();
        if(GameJsonContext.Default.GetTypeInfo(type) is not null){System.Text.Json.JsonSerializer.Serialize(writer,value,Options(options).GetTypeInfo(type));return;}
        switch(value){
            case string s:writer.WriteStringValue(s);return;
            case bool b:writer.WriteBooleanValue(b);return;
            case int n:writer.WriteNumberValue(n);return;
            case long n:writer.WriteNumberValue(n);return;
            case double n:writer.WriteNumberValue(n);return;
            case float n:writer.WriteNumberValue(n);return;
            case decimal n:writer.WriteNumberValue(n);return;
            case DateTime d:writer.WriteStringValue(d);return;
            case Guid g:writer.WriteStringValue(g);return;
            case Enum e:writer.WriteNumberValue(Convert.ToInt64(e));return;
            case IDictionary map:
                writer.WriteStartObject();foreach(DictionaryEntry entry in map){writer.WritePropertyName(Convert.ToString(entry.Key,System.Globalization.CultureInfo.InvariantCulture)!);WriteDiagnostic(writer,entry.Value,options);}writer.WriteEndObject();return;
            case IEnumerable list:
                writer.WriteStartArray();foreach(var item in list)WriteDiagnostic(writer,item,options);writer.WriteEndArray();return;
        }
        writer.WriteStartObject();
        foreach(var property in type.GetProperties(BindingFlags.Public|BindingFlags.Instance)){
            if(!property.CanRead||property.GetIndexParameters().Length!=0)continue;
            writer.WritePropertyName(options.PropertyNamingPolicy?.ConvertName(property.Name)??property.Name);
            WriteDiagnostic(writer,property.GetValue(value),options);
        }
        writer.WriteEndObject();
    }
    public static JsonElement SerializeToElement<T>(T value,JsonSerializerOptions? options=null)
    {
        using var document=JsonDocument.Parse(SerializeToUtf8Bytes(value,options));return document.RootElement.Clone();
    }
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
