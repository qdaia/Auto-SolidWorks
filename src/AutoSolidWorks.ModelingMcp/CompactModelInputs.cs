using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CadModeling.Core;
using CadModeling.Ir;

/// <summary>Immutable full-fidelity model inputs. Storing an input is not drawing acceptance or compilation.</summary>
public static class CompactModelInputs
{
    private static readonly JsonSerializerOptions Strict = new(ModelingIrJson.Options)
        { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    public static T Decode<T>(JsonNode node) where T : class => node.Deserialize<T>(Strict)
        ?? throw new JsonException("输入不能为空。");

    public static object Store(CompactArtifactStore store, string kind, JsonElement? input, string? baseInputId, JsonElement? edits)
    {
        if (kind is not ("drawing_context" or "draft")) throw new ArgumentException("kind 必须是 drawing_context 或 draft。");
        var artifactKind = kind == "drawing_context" ? "context" : "draft";
        if ((input is null) == string.IsNullOrWhiteSpace(baseInputId))
            throw new ArgumentException("input 与 baseInputId 必须且只能提供一个。");
        if (input is not null && edits is not null) throw new ArgumentException("首次保存使用 input；增量修改使用 baseInputId 与 edits。");
        if (input is null && edits is null) throw new ArgumentException("增量修改必须提供 edits。");
        var node = input is { } value ? JsonNode.Parse(value.GetRawText()) : store.LoadInput(baseInputId!, artifactKind);
        if (node is not JsonObject) throw new ArgumentException("输入必须是 JSON 对象。");
        if (edits is { } changes) ApplyEdits(node, changes);

        GenericModelDraft? draft = null;
        DrawingPlanContext? context;
        if (kind == "drawing_context") context = Decode<DrawingPlanContext>(node);
        else { draft = Decode<GenericModelDraft>(node); context = draft.DrawingContext; }
        // Retain the submitted representation; defaults are interpreted by the original compiler.
        var receipt = store.SaveInput(artifactKind, node, draft?.SourceModelPath, context, draft?.Recovery);
        return new { input_id = receipt.ArtifactId, input_sha256 = receipt.Sha256, input_path = receipt.Path,
            kind, base_input_id = baseInputId, operations_count = draft?.Operations.Count,
            source_path = context?.SourcePath, dimensions_count = context?.Dimensions.Count,
            features_count = context?.Features.Count, candidate_reviews_count = context?.OmissionReview?.Candidates.Count,
            status = "stored_not_validated", compiled = false, full_drawing_equivalence = false,
            message = "完整输入已保存在本机；仍需遗漏复查、原有编译和原生验收。" };
    }

    private static void ApplyEdits(JsonNode root, JsonElement edits)
    {
        if (edits.ValueKind != JsonValueKind.Array || edits.GetArrayLength() is < 1 or > 200)
            throw new ArgumentException("edits 必须包含 1..200 项 JSON Patch 子集操作。");
        foreach (var element in edits.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object) throw new ArgumentException("每个修改必须是对象。");
            var keys = element.EnumerateObject().Select(p => p.Name).ToArray();
            if (keys.Distinct(StringComparer.Ordinal).Count() != keys.Length || keys.Any(k => k is not ("op" or "path" or "value")))
                throw new ArgumentException("修改只接受 op、path、value，且不得重复字段。");
            if (!element.TryGetProperty("op", out var opValue) || opValue.ValueKind != JsonValueKind.String
                || !element.TryGetProperty("path", out var pathValue) || pathValue.ValueKind != JsonValueKind.String)
                throw new ArgumentException("修改必须提供字符串 op 和 path。");
            var op = opValue.GetString()!; var path = pathValue.GetString()!;
            if (op is not ("add" or "replace" or "remove" or "test")) throw new ArgumentException("只支持 add、replace、remove、test。");
            var hasValue = element.TryGetProperty("value", out var value);
            if ((op == "remove") == hasValue) throw new ArgumentException("remove 不提供 value；其他操作必须提供 value。");
            if (!path.StartsWith('/') || path.Length > 2048) throw new ArgumentException("path 使用非空 JSON Pointer，禁止替换整个输入。");
            var tokens = path[1..].Split('/').Select(t =>
            {
                if (Regex.IsMatch(t, "~(?![01])")) throw new ArgumentException("JSON Pointer 转义无效。");
                return t.Replace("~1", "/").Replace("~0", "~");
            }).ToArray();
            JsonNode? parent = root;
            foreach (var token in tokens[..^1])
            {
                if (parent is JsonObject obj && obj.TryGetPropertyValue(token, out var next)) parent = next;
                else if (parent is JsonArray array) parent = array[Index(token, array.Count, false)];
                else throw new ArgumentException("修改路径不存在：" + path);
            }
            var key = tokens[^1];
            var replacement = hasValue ? JsonNode.Parse(value.GetRawText()) : null;
            if (parent is JsonObject dictionary)
            {
                var exists = dictionary.TryGetPropertyValue(key, out var current);
                if (op != "add" && !exists) throw new ArgumentException("修改目标不存在：" + path);
                if (op == "test") { if (!JsonNode.DeepEquals(current, replacement)) throw new InvalidOperationException("修改前置条件不匹配：" + path); }
                else if (op == "remove") dictionary.Remove(key);
                else dictionary[key] = replacement;
            }
            else if (parent is JsonArray array)
            {
                var index = key == "-" && op == "add" ? array.Count : Index(key, array.Count, op == "add");
                if (op == "test") { if (!JsonNode.DeepEquals(array[index], replacement)) throw new InvalidOperationException("修改前置条件不匹配：" + path); }
                else if (op == "remove") array.RemoveAt(index);
                else if (op == "add") array.Insert(index, replacement);
                else array[index] = replacement;
            }
            else throw new ArgumentException("修改父路径不是对象或数组：" + path);
        }
    }

    private static int Index(string token, int count, bool insertion)
    {
        if (!Regex.IsMatch(token, "\\A(?:0|[1-9][0-9]*)\\z") || !int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var index)
            || index > count || (!insertion && index == count)) throw new ArgumentException("数组索引无效或越界。");
        return index;
    }
}
