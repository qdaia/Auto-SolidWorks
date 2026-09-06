using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace CadModeling.Drawing.Contracts;

public static class DrawingContractJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    public static string Serialize<T>(T value, bool indented = true)
    {
        var options = new JsonSerializerOptions(Options) { WriteIndented = indented };
        return JsonSerializer.Serialize(value, options);
    }

    public static string SerializeDeterministic<T>(T value)
    {
        var node = JsonSerializer.SerializeToNode(value, Options)
            ?? throw new JsonException("Drawing contract serialized to an empty document.");
        var canonical = SortNode(node);
        return canonical.ToJsonString(new JsonSerializerOptions(Options) { WriteIndented = false });
    }

    public static T Deserialize<T>(string json) where T : class =>
        JsonSerializer.Deserialize<T>(json, Options)
        ?? throw new JsonException("Drawing contract document was empty.");

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DictionaryKeyPolicy = null,
            PropertyNameCaseInsensitive = false,
            AllowTrailingCommas = false,
            ReadCommentHandling = JsonCommentHandling.Disallow,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver()
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
        return options;
    }

    private static JsonNode SortNode(JsonNode node) => node switch
    {
        JsonObject obj => new JsonObject(obj
            .OrderBy(property => property.Key, StringComparer.Ordinal)
            .Select(property => KeyValuePair.Create(property.Key, property.Value is null ? null : SortNode(property.Value)))),
        JsonArray array => new JsonArray(array.Select(item => item is null ? null : SortNode(item)).ToArray()),
        _ => node.DeepClone()
    };
}
