using System.IO;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CadModeling.Core;
using CadModeling.Ir;

public sealed record ArtifactReceipt(string ArtifactId, string Sha256, string Path);

/// <summary>Local content-addressed receipts; paths and hashes detect changes, not hostile local authorship.</summary>
public sealed class CompactArtifactStore
{
    private readonly string root;
    private readonly string producer;
    public CompactArtifactStore() : this(Environment.GetEnvironmentVariable("AUTO_SOLIDWORKS_ARTIFACT_DIRECTORY")
        ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AutoSolidWorks", "mcp-artifacts")) { }
    public CompactArtifactStore(string directory, string? producerIdentity = null)
    {
        if (!System.IO.Path.IsPathFullyQualified(directory)) throw new ArgumentException("结果存储目录必须是绝对路径。");
        root = System.IO.Path.GetFullPath(directory);
        producer = producerIdentity ?? string.Join(":", new[] { typeof(CompactArtifactStore).Assembly,
            typeof(GenericPlanCompiler).Assembly, typeof(ModelingPlan).Assembly }.Select(a => a.ManifestModule.ModuleVersionId.ToString("N")));
    }

    public ArtifactReceipt SaveResult(object result) => Save("result", JsonSerializer.SerializeToNode(result, ModelingIrJson.Options)!, []);
    public ArtifactReceipt SaveInput(string kind, JsonNode payload, string? sourceModelPath, DrawingPlanContext? context, ModelingRecoveryOptions? recovery)
    {
        if (kind is not ("context" or "draft")) throw new ArgumentException("输入类型必须是 context 或 draft。");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? path)
        {
            if (path is null) return;
            if (!System.IO.Path.IsPathFullyQualified(path)) throw new ArgumentException("输入来源必须使用绝对路径。");
            paths.Add(System.IO.Path.GetFullPath(path));
        }
        Add(sourceModelPath); Add(context?.SourcePath); Add(context?.OmissionReview?.InventoryPath);
        Add(recovery?.ResumeManifestPath);
        if (recovery?.ResumeManifestPath is { } manifest)
            Add(JsonSerializer.Deserialize<ModelingCheckpointManifest>(File.ReadAllText(manifest), ModelingIrJson.Options)?.NativePath);
        return Save(kind, payload, paths.Select(p => new SourceIdentity(p, FileHash(p))).ToArray());
    }

    public JsonNode LoadInput(string id, string kind)
    {
        if (kind is not ("context" or "draft")) throw new ArgumentException("输入类型必须是 context 或 draft。");
        var envelope = Load(id, kind);
        ValidateProducerAndSources(envelope);
        return envelope.Payload.DeepClone();
    }
    public ArtifactReceipt SavePlan(ModelingPlan plan)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? path) { if (path is not null) paths.Add(System.IO.Path.GetFullPath(path)); }
        Add(plan.SourceModelPath); Add(plan.DrawingContext?.SourcePath); Add(plan.DrawingContext?.OmissionReview?.InventoryPath);
        Add(plan.Recovery.ResumeManifestPath);
        if (plan.Recovery.ResumeManifestPath is { } manifest)
            Add(JsonSerializer.Deserialize<ModelingCheckpointManifest>(File.ReadAllText(manifest), ModelingIrJson.Options)?.NativePath);
        var sources = paths.Select(p => new SourceIdentity(p, FileHash(p))).ToArray();
        return Save("plan", JsonSerializer.SerializeToNode(plan, ModelingIrJson.Options)!, sources);
    }

    public ModelingPlan LoadPlan(string id)
    {
        var envelope = Load(id, "plan");
        ValidateProducerAndSources(envelope);
        return ModelingIrJson.Deserialize(envelope.Payload.ToJsonString(ModelingIrJson.Options));
    }

    private void ValidateProducerAndSources(ArtifactEnvelope envelope)
    {
        if (envelope.Producer != producer) throw new InvalidOperationException("计划编译器身份已变化，请重新编译计划。");
        foreach (var source in envelope.Sources)
            if (!File.Exists(source.Path) || FileHash(source.Path) != source.Sha256)
                throw new InvalidOperationException("计划的来源或恢复文件已变化，请重新编译：" + source.Path);
    }

    public object Read(string id, string section = "", int offset = 0, int limit = 50)
    {
        if (offset < 0 || limit is < 1 or > 200) throw new ArgumentException("offset 必须非负，limit 必须在 1..200 范围内。");
        var envelope = Load(id);
        JsonNode? value = envelope.Payload;
        if (section.Length > 0)
        {
            if (!section.StartsWith('/')) throw new ArgumentException("section 使用 JSON Pointer，例如 /evidence 或 /pages/0/observation/observations。");
            foreach (var token in section[1..].Split('/'))
            {
                if (Regex.IsMatch(token, "~(?![01])")) throw new ArgumentException("section 中的 JSON Pointer 转义无效。");
                var key = token.Replace("~1", "/").Replace("~0", "~");
                if (value is JsonObject obj && obj.TryGetPropertyValue(key, out var item)) value = item;
                else if (value is JsonArray list && int.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var index) && index < list.Count) value = list[index];
                else throw new ArgumentException("section 不存在：" + section);
            }
        }
        const int budget = 24000;
        if (value is JsonValue scalar && scalar.TryGetValue<string>(out var text))
        {
            var start = Math.Min(offset, text.Length);
            if (start > 0 && start < text.Length && char.IsLowSurrogate(text[start]) && char.IsHighSurrogate(text[start - 1]))
                throw new ArgumentException("字符偏移不能位于 Unicode 代理对内部；使用返回的 next_offset。");
            var returned = Math.Min(budget, text.Length - start);
            if (start + returned < text.Length && returned > 0 && char.IsHighSurrogate(text[start + returned - 1])) returned--;
            return new { artifact_id = id, section, offset, total = text.Length, returned,
                pagination_unit = "characters", next_offset = start + returned < text.Length ? (int?)(start + returned) : null,
                text = text.Substring(start, returned) };
        }
        var items = new JsonArray();
        var consumed = 0;
        var size = 0;
        var entries = value is JsonArray array ? array.Select((v, i) => (i.ToString(CultureInfo.InvariantCulture), v)).ToArray()
            : value is JsonObject dictionary ? dictionary.Select(p => (p.Key, p.Value)).ToArray() : [("value", value)];
        foreach (var (key, item) in entries.Skip(offset).Take(limit))
        {
            // Object listings describe containers; a narrower pointer retrieves their contents.
            var oversizedText = item is JsonValue v && v.TryGetValue<string>(out var itemText) && itemText.Length > budget / 2;
            JsonNode? display = value is JsonObject && (item is JsonObject or JsonArray || oversizedText)
                ? new JsonObject { ["key"] = key, ["type"] = oversizedText ? "string" : item is JsonArray ? "array" : "object", ["count"] = item is JsonArray a ? a.Count : item is JsonObject o ? o.Count : item!.GetValue<string>().Length,
                    ["section"] = section + "/" + key.Replace("~", "~0").Replace("/", "~1") }
                : value is JsonObject ? new JsonObject { ["key"] = key, ["value"] = item?.DeepClone() } : item?.DeepClone();
            var length = display?.ToJsonString().Length ?? 4;
            if (size + length > budget)
            {
                if (consumed == 0) throw new ArgumentException("单条结果超过读取预算，请使用更细的 section 指向其字段。");
                break;
            }
            items.Add(display); size += length; consumed++;
        }
        return new { artifact_id = id, section, offset, limit, total = entries.Length, returned = consumed,
            next_offset = offset + consumed < entries.Length ? (int?)(offset + consumed) : null, items };
    }

    private ArtifactReceipt Save(string kind, JsonNode payload, SourceIdentity[] sources)
    {
        var envelope = new ArtifactEnvelope(1, kind, producer, payload, sources);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, ModelingIrJson.Options);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var id = kind + "_" + hash;
        Directory.CreateDirectory(root);
        var path = System.IO.Path.Combine(root, id + ".json");
        try { using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None); stream.Write(bytes); stream.Flush(true); }
        catch (IOException) when (File.Exists(path))
        { if (!File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes)) throw new IOException("已有结果文件身份不匹配，拒绝覆盖。"); }
        return new(id, hash, path);
    }

    private ArtifactEnvelope Load(string id, string? expectedKind = null)
    {
        if (!Regex.IsMatch(id, "\\A(?:plan|result|context|draft)_[a-f0-9]{64}\\z")) throw new ArgumentException("计划／结果／输入句柄无效。");
        var split = id.IndexOf('_'); var kind = id[..split];
        if (expectedKind is not null && kind != expectedKind) throw new ArgumentException("句柄类型不匹配。");
        var bytes = File.ReadAllBytes(System.IO.Path.Combine(root, id + ".json"));
        if (Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() != id[(split + 1)..])
            throw new InvalidOperationException("计划／结果文件内容已变化，拒绝使用。");
        var envelope = JsonSerializer.Deserialize<ArtifactEnvelope>(bytes, ModelingIrJson.Options) ?? throw new JsonException("结果文件为空。");
        if (envelope.Version != 1 || envelope.Kind != kind) throw new InvalidOperationException("结果文件合同不匹配。");
        return envelope;
    }

    private static string FileHash(string path)
    { using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite); return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(); }
    private sealed record SourceIdentity(string Path, string Sha256);
    private sealed record ArtifactEnvelope(int Version, string Kind, string Producer, JsonNode Payload, SourceIdentity[] Sources);
}
