using System.Text.Json.Nodes;

/// <summary>Extract a self-contained schema, preserving only reachable local references.</summary>
public static class FocusedContract
{
    public static JsonNode Select(JsonNode root, string section)
    {
        if (string.IsNullOrEmpty(section)) return root;
        if (!section.StartsWith('/')) throw new ArgumentException("section must be a JSON Pointer.");
        var selected = Resolve(root, section).DeepClone();
        var definitions = new JsonObject();
        var names = new Dictionary<string,string>(StringComparer.Ordinal);
        void Rewrite(JsonNode? node)
        {
            if (node is JsonObject obj)
            {
                if (obj["$ref"] is JsonValue value && value.TryGetValue<string>(out var reference))
                {
                    if (!reference.StartsWith('#')) throw new ArgumentException("External schema references are unsupported.");
                    if (!names.TryGetValue(reference, out var name))
                    {
                        name = "ref_" + names.Count; names.Add(reference, name);
                        var definition = Resolve(root, reference[1..]).DeepClone();
                        definitions[name] = definition;
                        Rewrite(definition);
                    }
                    obj["$ref"] = "#/$defs/" + name;
                }
                foreach (var pair in obj.ToArray()) if (pair.Key != "$ref") Rewrite(pair.Value);
            }
            else if (node is JsonArray array) foreach (var child in array) Rewrite(child);
        }
        Rewrite(selected);
        if (definitions.Count > 0)
        {
            if (selected is not JsonObject obj) throw new ArgumentException("Select a schema object.");
            obj["$defs"] = definitions;
        }
        return selected;
    }
    private static JsonNode Resolve(JsonNode root, string pointer)
    {
        JsonNode? current = root;
        if (pointer.Length == 0) return root;
        if (!pointer.StartsWith('/')) throw new ArgumentException("Invalid schema pointer.");
        foreach (var raw in pointer[1..].Split('/'))
        {
            var token = raw.Replace("~1", "/").Replace("~0", "~");
            current = current is JsonObject obj ? obj[token]
                : current is JsonArray array && int.TryParse(token, out var i) && i >= 0 && i < array.Count ? array[i] : null;
            if (current is null) throw new ArgumentException("Unknown schema section: " + pointer);
        }
        return current!;
    }
}
