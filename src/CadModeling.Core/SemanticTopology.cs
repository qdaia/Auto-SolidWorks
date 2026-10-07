using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using CadModeling.Ir;

namespace CadModeling.Core;

/// <summary>根实体、源语义和来源特征身份独立于冻结的坐标签名。</summary>
public sealed record SemanticTopologyReference
{
    public string Contract { get; init; } = "1.0";
    public required string SemanticKey { get; init; }
    public required string RootToken { get; init; }
    public required string RootOwnerPersistentReference { get; init; }
    public required string RootConfiguration { get; init; }
    public IReadOnlyList<string> AllowedOperationIds { get; init; } = [];
}

public sealed record TopologySourceBinding
{
    public required string SemanticKey { get; init; }
    public required string SourceRevisionId { get; init; }
    public IReadOnlyList<string> SourceFactIds { get; init; } = [];
    /// <summary>从该版源定义独立推导；不能复制待选候选项的测量值来通过。</summary>
    public required GeometrySignature ExpectedSignature { get; init; }
}

public sealed record TopologyElement
{
    public required string Token { get; init; }
    public required GeometryCandidate Candidate { get; init; }
}

public sealed record TopologySnapshot
{
    public required string RevisionKey { get; init; }
    public required GeometryDocumentIdentity Document { get; init; }
    public required string Configuration { get; init; }
    public IReadOnlyList<TopologyElement> Elements { get; init; } = [];
    public IReadOnlyList<TopologySourceBinding> SourceBindings { get; init; } = [];
}

[JsonConverter(typeof(JsonStringEnumConverter<TopologyRelation>))]
public enum TopologyRelation { Unchanged, Modified, Generated, Split, Merged, Deleted }

public sealed record TopologyMapping
{
    public TopologyRelation Relation { get; init; }
    public IReadOnlyList<string> InputTokens { get; init; } = [];
    public IReadOnlyList<string> OutputTokens { get; init; } = [];
}

public sealed record TopologyTransition
{
    public required string BeforeRevisionKey { get; init; }
    public required string AfterRevisionKey { get; init; }
    public required string OperationId { get; init; }
    public required string NativeOperationPersistentReference { get; init; }
    public bool CompleteHistory { get; init; }
    public IReadOnlyList<TopologyMapping> Mappings { get; init; } = [];
}

/// <summary>由执行／观察端口产生的库存与历史，不是可由 MCP 调用者提交的成功证据。</summary>
public sealed record TopologyHistoryCapture
{
    public required string Provider { get; init; }
    public bool CompleteInventory { get; init; }
    public bool SavedModelIdentityVerified { get; init; }
    public IReadOnlyList<TopologySnapshot> Snapshots { get; init; } = [];
    public IReadOnlyList<TopologyTransition> Transitions { get; init; } = [];
    public required string Digest { get; init; }
}

public interface ISemanticTopologyHistorySession
{
    TopologyHistoryCapture? Capture(GeometryRef reference, GeometryDocumentIdentity currentDocument);
}

public sealed record SemanticTopologyReceipt
{
    public required string ReferenceFingerprint { get; init; }
    public required string CurrentSourceRevisionId { get; init; }
    public required string CurrentConfiguration { get; init; }
    public required string CurrentRevisionKey { get; init; }
    public required string SelectedToken { get; init; }
    public required string HistoryDigest { get; init; }
    public IReadOnlyList<string> OperationIds { get; init; } = [];
    public required TopologyHistoryCapture Capture { get; init; }
}

public static class SemanticTopologyResolver
{
    public static void Validate(GeometryRef reference)
    {
        if (reference.Semantic is not { } semantic) return;
        if (semantic.Contract != "1.0" || Empty(semantic.SemanticKey) || Empty(semantic.RootToken)
            || Empty(semantic.RootOwnerPersistentReference) || Empty(semantic.RootConfiguration) || Empty(reference.NativePersistentReference)
            || Empty(reference.OperationId) || Empty(reference.FeatureId) || Empty(reference.SourceRevisionId)
            || reference.SourceFactIds.Count == 0 || reference.InputToFeature is not null
            || reference.EntityKind is not (EntityKind.Face or EntityKind.Edge))
            throw Invalid("语义引用需根实体、源事实／修订、源操作和来源特征持久身份；当前只支持最终 B-Rep 面／边。");
        Identifiers(semantic.AllowedOperationIds, false, "允许操作");
        if(semantic.AllowedOperationIds.Count>256||reference.SourceFactIds.Count>256)throw Invalid("语义引用最多 256 个源事实与允许操作。");
    }

    public static string Digest(TopologyHistoryCapture capture)
    {
        // 数组顺序在历史合同中有意义；摘要覆盖完整库存、源绑定和映射，排除摘要自身。
        var payload = CanonicalBytes(capture with { Digest = "" });
        return Convert.ToHexString(SHA256.HashData(payload));
    }

    public static GeometryRefResolution Resolve(GeometryRef reference, GeometryDocumentIdentity document,
        IReadOnlyList<GeometryCandidate> currentCandidates, TopologyHistoryCapture? capture)
    {
        GeometryRefResolver.Validate(reference);
        if (reference.Semantic is not { } semantic) throw Invalid("需要语义引用。");
        GeometryRefResolution Fail(GeometryRefResolutionStatus status, string message, IReadOnlyList<string>? ids = null) => new()
        {
            Reference = reference, Status = status, ResolvedModelSha256 = document.ModelSha256,
            CandidateIds = ids ?? [], Message = message, Evidence = ["semantic_topology=true", "frozen_signature_fallback=false"]
        };
        if (!SameDocument(reference.DocumentId, reference.DocumentPath, document))
            return Fail(GeometryRefResolutionStatus.WrongDocument, "语义引用属于另一保存文档。");
        if (capture is null) return Fail(GeometryRefResolutionStatus.Unsupported, "缺少完整来源库存与拓扑变更历史；不能回退到冻结签名或最近实体。");
        try
        {
            ValidateCapture(capture, reference);
            var first = capture.Snapshots[0]; var last = capture.Snapshots[^1];
            if(first.Configuration!=semantic.RootConfiguration)
                return Fail(GeometryRefResolutionStatus.Stale,"拓扑历史起点不是引用的原始配置。");
            if (!SameSha(first.Document.ModelSha256, reference.ModelSha256) || first.Document.SourceRevisionId != reference.SourceRevisionId
                || first.Document.DocumentRevision != reference.DocumentRevision)
                return Fail(GeometryRefResolutionStatus.Stale, "拓扑历史起点不属于引用的模型／源／文档修订。");
            if (!SameSha(last.Document.ModelSha256, document.ModelSha256) || last.Document.SourceRevisionId != document.SourceRevisionId
                || last.Document.DocumentRevision != document.DocumentRevision)
                return Fail(GeometryRefResolutionStatus.Stale, "拓扑历史终点不是当前模型与源修订。");
            var root = first.Elements.SingleOrDefault(e => e.Token == semantic.RootToken);
            if (root is null || root.Candidate.NativePersistentReference != reference.NativePersistentReference
                || !GeometryOwnership.HasOwner(root.Candidate,semantic.RootOwnerPersistentReference,reference.FeatureId)
                || !GeometryRefResolver.MatchesFrozen(reference, root.Candidate))
                return Fail(GeometryRefResolutionStatus.Stale, "根实体或来源特征身份／源几何不符。");
            var reached = new HashSet<string>(StringComparer.Ordinal) { semantic.RootToken };
            for (var i = 0; i < capture.Transitions.Count; i++)
            {
                var step = capture.Transitions[i];
                if (!semantic.AllowedOperationIds.Contains(step.OperationId, StringComparer.Ordinal))
                    return Fail(GeometryRefResolutionStatus.Stale, "拓扑变更经过未声明的操作：" + step.OperationId);
                var next = new HashSet<string>(StringComparer.Ordinal);
                var byInput=step.Mappings.SelectMany(m=>m.InputTokens.Select(t=>(Token:t,Mapping:m))).ToDictionary(x=>x.Token,x=>x.Mapping,StringComparer.Ordinal);
                foreach (var token in reached)
                {
                    if (!byInput.TryGetValue(token,out var mapping)) return Fail(GeometryRefResolutionStatus.Unsupported, "变更历史对目标存在缺口或互相矛盾的映射。");
                    foreach (var output in mapping.OutputTokens) next.Add(output);
                }
                reached = next;
            }
            var binding = last.SourceBindings.Single(b => b.SemanticKey == semantic.SemanticKey);
            var expected = reference with { Semantic = null, FeatureId = null, GeometryKind=binding.ExpectedSignature.GeometryKind, Signature = binding.ExpectedSignature };
            var descendants = last.Elements.Where(e => reached.Contains(e.Token)).ToArray();
            var eligible = descendants.Where(e => GeometryRefResolver.MatchesFrozen(expected,e.Candidate)).ToArray();
            if (eligible.Length == 0) return Fail(GeometryRefResolutionStatus.Stale, "目标已删除或源语义不再由任何历史后继实体满足。");
            if (eligible.Length > 1) return Fail(GeometryRefResolutionStatus.Ambiguous, "源语义匹配多个历史后继实体；禁止首次／最近匹配。",eligible.Select(e => e.Candidate.CandidateId).ToArray());
            var selected = eligible[0];
            // 最终证据必须对应本次实际库存；历史中的候选不能自证存在于当前模型。
            var liveById = currentCandidates.ToDictionary(c => c.CandidateId,StringComparer.Ordinal);
            var live = liveById.TryGetValue(selected.Candidate.CandidateId,out var selectedLive) ? new[] {selectedLive} : [];
            if (live.Length != 1 || !SameCandidate(live[0], selected.Candidate))
                return Fail(GeometryRefResolutionStatus.Stale, "历史后继与本次实际 B-Rep 库存不一致。");
            if (last.Elements.Count != currentCandidates.Count || last.Elements.Any(e => !liveById.TryGetValue(e.Candidate.CandidateId,out var c) || !SameCandidate(c,e.Candidate)))
                return Fail(GeometryRefResolutionStatus.Unsupported, "终点库存不完整，无法排除被遗漏的候选或错误归属。");
            var receipt = new SemanticTopologyReceipt
            {
                ReferenceFingerprint = GeometryRefResolver.Fingerprint(reference), CurrentSourceRevisionId = last.Document.SourceRevisionId!,
                CurrentConfiguration = last.Configuration, CurrentRevisionKey = last.RevisionKey, SelectedToken = selected.Token,
                HistoryDigest = capture.Digest, OperationIds = capture.Transitions.Select(t => t.OperationId).ToArray(), Capture = capture
            };
            return new()
            {
                Reference = reference, Status = GeometryRefResolutionStatus.Resolved, Candidate = live[0], CandidateIds = [live[0].CandidateId],
                ResolvedModelSha256 = document.ModelSha256, Rebound = capture.Transitions.Count > 0 || live[0].NativePersistentReference != reference.NativePersistentReference,
                NativeReferenceRecovered = live[0].NativePersistentReference == reference.NativePersistentReference, SemanticReceipt = receipt,
                Message = "源语义及完整变更历史唯一解析了当前实体；坐标／尺寸来自当前源修订。",
                Evidence = ["semantic_topology=true", "frozen_signature_fallback=false", "history_digest="+capture.Digest, "semantic_key="+semantic.SemanticKey]
            };
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NullReferenceException)
        { return Fail(GeometryRefResolutionStatus.Unsupported,"语义拓扑证据无效："+ex.Message); }
    }

    public static bool VerifyReceipt(GeometryRefResolution resolution)
    {
        if (resolution.Reference.Semantic is null) return false;
        if (resolution.Status != GeometryRefResolutionStatus.Resolved || resolution.Candidate is null || resolution.SemanticReceipt is not { } receipt) return false;
        try
        {
            var last = receipt.Capture.Snapshots[^1];
            var replay = Resolve(resolution.Reference, last.Document, last.Elements.Select(e => e.Candidate).ToArray(),receipt.Capture);
            return replay.Status == GeometryRefResolutionStatus.Resolved && replay.SemanticReceipt is { } verified
                && receipt.ReferenceFingerprint == verified.ReferenceFingerprint && receipt.HistoryDigest == verified.HistoryDigest
                && receipt.CurrentSourceRevisionId == verified.CurrentSourceRevisionId && receipt.CurrentConfiguration == verified.CurrentConfiguration
                && receipt.CurrentRevisionKey == verified.CurrentRevisionKey && receipt.SelectedToken == verified.SelectedToken
                && receipt.OperationIds.SequenceEqual(verified.OperationIds) && SameSha(resolution.ResolvedModelSha256,last.Document.ModelSha256)
                && resolution.CandidateIds.SequenceEqual(replay.CandidateIds) && SameCandidate(resolution.Candidate,replay.Candidate!)
                && resolution.Rebound == replay.Rebound && resolution.NativeReferenceRecovered == replay.NativeReferenceRecovered;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NullReferenceException or IndexOutOfRangeException) { return false; }
    }

    private static void ValidateCapture(TopologyHistoryCapture capture, GeometryRef reference)
    {
        if (Empty(capture.Provider) || !capture.CompleteInventory || !capture.SavedModelIdentityVerified || capture.Snapshots is null || capture.Transitions is null
            || capture.Snapshots.Count is 0 or > 65 || capture.Transitions.Count != capture.Snapshots.Count-1
            || !SameSha(capture.Digest,Digest(capture))) throw Invalid("缺少完整库存、连续历史或完整摘要。");
        if (capture.Snapshots.Sum(s => s.Elements.Count) > 100000) throw Invalid("拓扑库存超过 100000 个实体。");
        Identifiers(capture.Snapshots.Select(s => s.RevisionKey).ToArray(),true,"修订键");
        var semantic = reference.Semantic!;
        foreach (var snapshot in capture.Snapshots)
        {
            var doc = snapshot.Document;
            if (!SameDocument(reference.DocumentId,reference.DocumentPath,doc) || !Sha(doc.ModelSha256) || Empty(doc.SourceRevisionId)
                || Empty(snapshot.Configuration) || snapshot.Configuration != capture.Snapshots[0].Configuration)
                throw Invalid("库存文档／源／配置身份缺失或越界。");
            Identifiers(snapshot.Elements.Select(e => e.Token).ToArray(),false,"实体 token");
            Identifiers(snapshot.Elements.Select(e => e.Candidate.CandidateId).ToArray(),false,"候选 ID");
            Identifiers(snapshot.Elements.Select(e => e.Candidate.NativePersistentReference!).ToArray(),false,"实体持久引用");
            foreach (var element in snapshot.Elements)
                if (!GeometryRefResolver.TryValidate(element.Candidate,out _) || GeometryOwnership.Owners(element.Candidate).Count==0)
                    throw Invalid("库存实体缺可验证几何或实际来源特征身份。");
            if(snapshot.SourceBindings.Count>256)throw Invalid("每个库存最多 256 个源语义绑定。");
            Identifiers(snapshot.SourceBindings.Select(b => b.SemanticKey).ToArray(),true,"源语义键");
            var bindings = snapshot.SourceBindings.Where(b => b.SemanticKey == semantic.SemanticKey).ToArray();
            if (bindings.Length != 1 || bindings[0].SourceRevisionId != doc.SourceRevisionId
                || !bindings[0].SourceFactIds.ToHashSet(StringComparer.Ordinal).SetEquals(reference.SourceFactIds))
                throw Invalid("每个修订需唯一源语义及完整源事实覆盖；不能删掉不便满足的需求。");
            Identifiers(bindings[0].SourceFactIds,true,"源事实");
            GeometryRefResolver.Validate(bindings[0].ExpectedSignature);
            if (bindings[0].ExpectedSignature.EntityKind != reference.EntityKind
                || bindings[0].ExpectedSignature.GeometryKind == GeometryKind.Any
                || !HasDiscriminator(bindings[0].ExpectedSignature)) throw Invalid("源语义需明确种类和独立几何判别条件。");
        }
        var rootBinding = capture.Snapshots[0].SourceBindings.Single(b => b.SemanticKey == semantic.SemanticKey);
        if (!SameSignature(rootBinding.ExpectedSignature,reference.Signature)) throw Invalid("起点源定义与引用冻结时的独立签名不同。");
        for (var i = 0; i < capture.Transitions.Count; i++)
        {
            var step = capture.Transitions[i]; var before = capture.Snapshots[i]; var after = capture.Snapshots[i+1];
            if (!step.CompleteHistory || Empty(step.OperationId) || Empty(step.NativeOperationPersistentReference)
                || step.BeforeRevisionKey != before.RevisionKey || step.AfterRevisionKey != after.RevisionKey || step.Mappings.Count > 100000)
                throw Invalid("变更操作身份或历史顺序／完整性无效。");
            var inputSet = before.Elements.Select(e => e.Token).ToHashSet(StringComparer.Ordinal);
            var outputSet = after.Elements.Select(e => e.Token).ToHashSet(StringComparer.Ordinal);
            var beforeByToken=before.Elements.ToDictionary(e=>e.Token,StringComparer.Ordinal);
            var afterByToken=after.Elements.ToDictionary(e=>e.Token,StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var map in step.Mappings)
            {
                if (!Enum.IsDefined(map.Relation)) throw Invalid("未知拓扑变更关系。");
                Identifiers(map.InputTokens,true,"输入实体"); Identifiers(map.OutputTokens,map.Relation != TopologyRelation.Deleted,"输出实体");
                if (map.InputTokens.Any(t => !inputSet.Contains(t) || !seen.Add(t)) || map.OutputTokens.Any(t => !outputSet.Contains(t)))
                    throw Invalid("映射实体缺失或同一输入出现矛盾映射。");
                if (map.Relation is TopologyRelation.Unchanged or TopologyRelation.Modified && (map.InputTokens.Count != 1 || map.OutputTokens.Count != 1)
                    || map.Relation == TopologyRelation.Split && (map.InputTokens.Count != 1 || map.OutputTokens.Count < 2)
                    || map.Relation == TopologyRelation.Merged && (map.InputTokens.Count < 2 || map.OutputTokens.Count != 1)
                    || map.Relation == TopologyRelation.Deleted && map.OutputTokens.Count != 0)
                    throw Invalid("拓扑关系与输入／输出数量矛盾。");
                if (map.Relation == TopologyRelation.Unchanged)
                {
                    var a = beforeByToken[map.InputTokens[0]].Candidate;
                    var b = afterByToken[map.OutputTokens[0]].Candidate;
                    if (a.NativePersistentReference != b.NativePersistentReference || !GeometryOwnership.SameOwners(a,b)
                        || !SameSignature(a.Signature,b.Signature)) throw Invalid("Unchanged 不能掩盖实体、所有权或几何改变。");
                }
                foreach(var target in map.OutputTokens)
                {
                    foreach(var owner in GeometryOwnership.Owners(afterByToken[target].Candidate))
                    if(owner.PersistentReference!=step.NativeOperationPersistentReference && !map.InputTokens.Any(t=>GeometryOwnership.Owners(beforeByToken[t].Candidate).Contains(owner)))
                        throw Invalid("后继实体绑定到了未参与输入或当前操作的来源特征。");
                }
            }
            if (!seen.SetEquals(inputSet)) throw Invalid("历史必须覆盖全部输入库存，不能丢掉竞争或合并来源。");
        }
    }

    public static bool SameCandidate(GeometryCandidate a, GeometryCandidate b) => a.CandidateId == b.CandidateId
        && a.NativePersistentReference == b.NativePersistentReference && a.FeatureId == b.FeatureId
        && a.OwnerFeaturePersistentReference == b.OwnerFeaturePersistentReference && a.InputToFeature == b.InputToFeature
        && GeometryOwnership.SameOwners(a,b)
        && SameSignature(a.Signature,b.Signature);
    private static bool SameSignature(GeometrySignature a, GeometrySignature b) => CanonicalBytes(a).AsSpan().SequenceEqual(CanonicalBytes(b));
    internal static byte[] CanonicalBytes(object value)
    {
        var element=JsonSerializer.SerializeToElement(value,ModelingIrJson.Options);
        using var stream=new MemoryStream();
        using(var writer=new Utf8JsonWriter(stream))Write(element,writer);
        return stream.ToArray();
        static void Write(JsonElement element,Utf8JsonWriter writer)
        {
            if(element.ValueKind==JsonValueKind.Object)
            {
                writer.WriteStartObject();foreach(var p in element.EnumerateObject()){writer.WritePropertyName(p.Name);Write(p.Value,writer);}writer.WriteEndObject();
            }
            else if(element.ValueKind==JsonValueKind.Array)
            {writer.WriteStartArray();foreach(var item in element.EnumerateArray())Write(item,writer);writer.WriteEndArray();}
            else if(element.ValueKind==JsonValueKind.Number&&element.TryGetDouble(out var number)&&number==0)writer.WriteNumberValue(0);
            else element.WriteTo(writer);
        }
    }
    private static bool HasDiscriminator(GeometrySignature s) => s.AnchorMm is not null || s.Direction is not null || s.RadiusMm is not null || s.AreaMm2 is not null;
    private static bool SameDocument(string id,string? path,GeometryDocumentIdentity doc) => id == doc.DocumentId
        && (path is null || doc.DocumentPath is not null && Path.GetFullPath(path).Equals(Path.GetFullPath(doc.DocumentPath),OperatingSystem.IsWindows()?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal));
    private static bool SameSha(string a,string b) => Sha(a) && Sha(b) && a.Equals(b,StringComparison.OrdinalIgnoreCase);
    private static bool Sha(string? s) => s is {Length:64} && s.All(Uri.IsHexDigit);
    private static bool Empty(string? s) => string.IsNullOrWhiteSpace(s);
    private static void Identifiers(IReadOnlyList<string> ids,bool requireAny,string label)
    { if (ids is null || requireAny && ids.Count==0 || ids.Count > 100000 || ids.Any(Empty) || ids.Distinct(StringComparer.Ordinal).Count()!=ids.Count) throw Invalid(label+"必须非空且唯一。"); }
    private static ArgumentException Invalid(string message) => new("SEMANTIC_TOPOLOGY_CONTRACT："+message);
}
