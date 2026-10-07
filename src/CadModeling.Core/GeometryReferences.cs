using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using CadModeling.Ir;

namespace CadModeling.Core;

[JsonConverter(typeof(JsonStringEnumConverter<GeometryRefResolutionStatus>))]
public enum GeometryRefResolutionStatus
{
    Resolved,
    Stale,
    Ambiguous,
    Missing,
    WrongDocument,
    Unsupported
}

public sealed record GeometrySignature
{
    public EntityKind EntityKind { get; init; }
    public GeometryKind GeometryKind { get; init; } = GeometryKind.Any;
    public Vector3? AnchorMm { get; init; }
    public Vector3? Direction { get; init; }
    public double? RadiusMm { get; init; }
    public double? AreaMm2 { get; init; }
    public double PositionToleranceMm { get; init; } = 0.05;
    public double RadiusToleranceMm { get; init; } = 0.02;
    public double AreaToleranceMm2 { get; init; } = 0.05;
    public double DirectionToleranceDegrees { get; init; } = 0.25;
}

public sealed record GeometryRef
{
    public const string ContractVersion = "1.0.0";
    public string Contract { get; init; } = ContractVersion;
    public required string RefId { get; init; }
    public required string DocumentId { get; init; }
    public string? DocumentPath { get; init; }
    public string? DocumentRevision { get; init; }
    public required string ModelSha256 { get; init; }
    public string? NativePersistentReference { get; init; }
    public EntityKind EntityKind { get; init; }
    public GeometryKind GeometryKind { get; init; } = GeometryKind.Any;
    public string? FeatureId { get; init; }
    public string? OperationId { get; init; }
    // Null resolves final B-Rep. An explicit native feature name resolves edges in
    // that feature's input B-Rep while selection access is held (fillet/chamfer only).
    public string? InputToFeature { get; init; }
    public IReadOnlyList<string> SourceFactIds { get; init; } = [];
    public string? SourceRevisionId { get; init; }
    public required GeometrySignature Signature { get; init; }
    public SemanticTopologyReference? Semantic { get; init; }
}

public sealed record GeometryDocumentIdentity
{
    public required string DocumentId { get; init; }
    public string? DocumentPath { get; init; }
    public string? DocumentRevision { get; init; }
    public string? SourceRevisionId { get; init; }
    public required string ModelSha256 { get; init; }

    public static GeometryDocumentIdentity FromSavedPath(string path, string modelSha256, string? revision = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var full = Path.GetFullPath(path);
        var canonical = OperatingSystem.IsWindows() ? full.ToUpperInvariant() : full;
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        return new() { DocumentId = id, DocumentPath = full, DocumentRevision = revision, ModelSha256 = modelSha256 };
    }
}

public sealed record GeometryCandidate
{
    public required string CandidateId { get; init; }
    public string? NativePersistentReference { get; init; }
    public string? FeatureId { get; init; }
    public string? InputToFeature { get; init; }
    public string? OwnerFeaturePersistentReference { get; init; }
    public IReadOnlyList<GeometryOwnerIdentity> OwnerFeatures { get; init; } = [];
    public required GeometrySignature Signature { get; init; }
}

public sealed record GeometryRefResolution
{
    public required GeometryRef Reference { get; init; }
    public GeometryRefResolutionStatus Status { get; init; }
    public GeometryCandidate? Candidate { get; init; }
    public bool Rebound { get; init; }
    public bool NativeReferenceRecovered { get; init; }
    public required string ResolvedModelSha256 { get; init; }
    public IReadOnlyList<string> CandidateIds { get; init; } = [];
    public IReadOnlyList<string> Evidence { get; init; } = [];
    public string Message { get; init; } = string.Empty;
    public SemanticTopologyReceipt? SemanticReceipt { get; init; }
}

public static class GeometryRefResolver
{
    public static GeometryRefResolution Resolve(GeometryRef reference, GeometryDocumentIdentity document,
        IReadOnlyList<GeometryCandidate> candidates, TopologyHistoryCapture? topologyHistory = null)
    {
        Validate(reference);
        Validate(document);
        ArgumentNullException.ThrowIfNull(candidates);
        if (reference.Semantic is not null) return SemanticTopologyResolver.Resolve(reference,document,candidates,topologyHistory);

        var sameRevision = reference.ModelSha256.Equals(document.ModelSha256, StringComparison.OrdinalIgnoreCase) &&
            (reference.DocumentRevision is null || document.DocumentRevision is null || reference.DocumentRevision == document.DocumentRevision);

        var relevantCandidates = candidates.Where(candidate => CandidateCouldAffectResolution(reference, candidate)).ToArray();
        var invalidCandidates = relevantCandidates.Where(candidate => !TryValidate(candidate, out _)).ToArray();
        if (invalidCandidates.Length > 0)
            return Result(GeometryRefResolutionStatus.Unsupported,
                $"{invalidCandidates.Length}候选项在 GeometryRef 解决方案范围内的几何无效或非有限，无法认证。",
                ids: invalidCandidates.Select(candidate => candidate.CandidateId).ToArray());

        if (reference.SourceRevisionId is { Length: > 0 } expectedSourceRevision &&
            !expectedSourceRevision.Equals(document.SourceRevisionId, StringComparison.Ordinal))
            return Result(GeometryRefResolutionStatus.Stale, "源特征修订更改；几何参考需在再次使用前重新验证。");

        if (!reference.DocumentId.Equals(document.DocumentId, StringComparison.Ordinal) ||
            reference.DocumentPath is { Length: > 0 } expectedPath && document.DocumentPath is { Length: > 0 } actualPath &&
            !Path.GetFullPath(expectedPath).Equals(Path.GetFullPath(actualPath), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            return Result(GeometryRefResolutionStatus.WrongDocument, "几何参考属于不同的保存文档。");
        var native = reference.NativePersistentReference is null ? null : candidates.FirstOrDefault(candidate =>
            candidate.NativePersistentReference is not null && candidate.NativePersistentReference.Equals(reference.NativePersistentReference, StringComparison.Ordinal));

        if (sameRevision && native is not null)
        {
            if (!Matches(reference, native))
                return Result(GeometryRefResolutionStatus.Stale,
                    "原生持久参考恢复了一个几何签名不再匹配的对象；拒绝无声重定向。", native,
                    nativeRecovered: true);
            return Result(GeometryRefResolutionStatus.Resolved, "原生持久参考和几何签名都匹配。", native,
                nativeRecovered: true);
        }

        var matches = candidates.Where(candidate => Matches(reference, candidate)).ToArray();
        if (matches.Length == 0)
            return Result(sameRevision ? GeometryRefResolutionStatus.Missing : GeometryRefResolutionStatus.Stale,
                sameRevision ? "没有对象匹配已锁定几何图形签名。" : "文档修订更改，且引用的对象无法唯一恢复。",
                native, nativeRecovered: native is not null);
        if (matches.Length > 1)
            return Result(GeometryRefResolutionStatus.Ambiguous,
                $"几何签名匹配{matches.Length}对象；自动首匹配重绑定被禁止。",
                native, matches.Select(item => item.CandidateId).ToArray(), nativeRecovered: native is not null);

        return Result(GeometryRefResolutionStatus.Resolved,
            sameRevision ? "一个独特的几何特征匹配解决了参考问题。" : "一个更改的文档修订版被冻结的几何签名唯一重建。",
            matches[0], matches.Select(item => item.CandidateId).ToArray(), rebound: native != matches[0] || !sameRevision, nativeRecovered: native is not null);

        GeometryRefResolution Result(GeometryRefResolutionStatus status, string message, GeometryCandidate? candidate = null,
            IReadOnlyList<string>? ids = null, bool rebound = false, bool nativeRecovered = false) => new()
        {
            Reference = reference,
            Status = status,
            Candidate = candidate,
            Rebound = rebound,
            NativeReferenceRecovered = nativeRecovered,
            ResolvedModelSha256 = document.ModelSha256,
            CandidateIds = ids ?? (candidate is null ? [] : [candidate.CandidateId]),
            Evidence = BuildEvidence(reference, document, candidate, sameRevision, rebound),
            Message = message
        };
    }

    public static bool Matches(GeometryRef reference, GeometryCandidate candidate)
        => reference.Semantic is null && MatchesFrozen(reference,candidate);

    public static bool IsVerifiedResolution(GeometryRefResolution resolution) => resolution.Reference.Semantic is not null
        ? SemanticTopologyResolver.VerifyReceipt(resolution)
        : resolution.Status==GeometryRefResolutionStatus.Resolved && resolution.Candidate is not null && MatchesFrozen(resolution.Reference,resolution.Candidate);

    public static EntityQuery SelectionQuery(GeometryRefResolution resolution)
    {
        if(!IsVerifiedResolution(resolution)||resolution.Candidate is not {} candidate||resolution.CandidateIds.Count!=1)
            throw new ArgumentException("几何选择需通过重验的唯一解析回执。");
        var signature=candidate.Signature;
        return new()
        {
            Kind=signature.EntityKind,FeatureId=candidate.FeatureId,PersistentReference=candidate.NativePersistentReference,
            RequirePersistentIdentity=resolution.Reference.Semantic is not null,Geometry=signature.GeometryKind,
            PositionMm=signature.AnchorMm,Direction=signature.Direction,RadiusMm=signature.RadiusMm,
            AreaMm2=signature.EntityKind==EntityKind.Face?signature.AreaMm2:null,AreaToleranceMm2=signature.AreaToleranceMm2,
            ToleranceMm=signature.PositionToleranceMm,AllMatches=false
        };
    }

    internal static bool MatchesFrozen(GeometryRef reference, GeometryCandidate candidate)
    {
        if (!TryValidate(candidate, out _)) return false;
        if (!string.Equals(reference.InputToFeature,candidate.InputToFeature,StringComparison.Ordinal)) return false;
        if (candidate.Signature.EntityKind != reference.EntityKind || candidate.Signature.EntityKind != reference.Signature.EntityKind)
            return false;
        if (reference.GeometryKind != GeometryKind.Any && candidate.Signature.GeometryKind != reference.GeometryKind)
            return false;
        if (reference.Signature.GeometryKind != GeometryKind.Any && candidate.Signature.GeometryKind != reference.Signature.GeometryKind)
            return false;
        if (reference.FeatureId is { Length: > 0 } feature && !GeometryOwnership.HasFeature(candidate,feature))
            return false;
        var expected = reference.Signature;
        var actual = candidate.Signature;
        if (expected.AnchorMm is { } p && (actual.AnchorMm is not { } q || Distance(p, q) > expected.PositionToleranceMm)) return false;
        if (expected.RadiusMm is { } radius && (actual.RadiusMm is not { } actualRadius || Math.Abs(radius - actualRadius) > expected.RadiusToleranceMm)) return false;
        if (expected.AreaMm2 is { } area && (actual.AreaMm2 is not { } actualArea || Math.Abs(area - actualArea) > expected.AreaToleranceMm2)) return false;
        if (expected.Direction is { } direction)
        {
            if (actual.Direction is not { } actualDirection || !Finite(direction) || !Finite(actualDirection)) return false;
            var a = Norm(direction); var b = Norm(actualDirection);
            if (!double.IsFinite(a) || !double.IsFinite(b) || a <= 1e-12 || b <= 1e-12) return false;
            var cosine = Math.Clamp(Math.Abs(direction.X/a*(actualDirection.X/b)+direction.Y/a*(actualDirection.Y/b)+direction.Z/a*(actualDirection.Z/b)), -1, 1);
            var degrees = Math.Acos(cosine) * 180 / Math.PI;
            if (degrees > expected.DirectionToleranceDegrees) return false;
        }
        return true;
    }

    public static string Fingerprint(GeometryRef reference)
    {
        Validate(reference);
        static Vector3? Zero(Vector3? v) => v is null ? null : new(v.X==0?0:v.X,v.Y==0?0:v.Y,v.Z==0?0:v.Z);
        var canonical = reference with
        {
            ModelSha256=reference.ModelSha256.ToUpperInvariant(),SourceFactIds=reference.SourceFactIds.Order(StringComparer.Ordinal).ToArray(),
            Signature=reference.Signature with {AnchorMm=Zero(reference.Signature.AnchorMm),Direction=Zero(reference.Signature.Direction)},
            Semantic=reference.Semantic is null?null:reference.Semantic with {AllowedOperationIds=reference.Semantic.AllowedOperationIds.Order(StringComparer.Ordinal).ToArray()}
        };
        return Convert.ToHexString(SHA256.HashData(SemanticTopologyResolver.CanonicalBytes(canonical)));
    }

    public static void Validate(GeometryRef reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (string.IsNullOrWhiteSpace(reference.RefId) || string.IsNullOrWhiteSpace(reference.DocumentId) || !Sha(reference.ModelSha256))
            throw new ArgumentException("GeometryRef 需要参考 ID、文档 ID 和 SHA-256 模型指纹。", nameof(reference));
        if (reference.Contract != GeometryRef.ContractVersion || !Enum.IsDefined(reference.EntityKind) || !Enum.IsDefined(reference.GeometryKind)
            || reference.Signature.EntityKind != reference.EntityKind ||
            reference.GeometryKind != GeometryKind.Any && reference.Signature.GeometryKind != GeometryKind.Any && reference.Signature.GeometryKind != reference.GeometryKind)
            throw new ArgumentException("GeometryRef 特征/几何种类必须与签名一致。", nameof(reference));
        Validate(reference.Signature);
        if(reference.InputToFeature is not null&&(string.IsNullOrWhiteSpace(reference.InputToFeature)||reference.EntityKind!=EntityKind.Edge))
            throw new ArgumentException("特征输入的几何需要一个边和一个明确的原生特征名称。",nameof(reference));
        if (reference.SourceFactIds.Any(string.IsNullOrWhiteSpace) || reference.SourceFactIds.Distinct(StringComparer.Ordinal).Count() != reference.SourceFactIds.Count)
            throw new ArgumentException("GeometryRef 源事实 ID 必须非空且唯一。", nameof(reference));
        if (reference.SourceFactIds.Count > 0 && string.IsNullOrWhiteSpace(reference.SourceRevisionId))
            throw new ArgumentException("GeometryRef 从源事实推导出的值必须绑定源修订ID。", nameof(reference));
        SemanticTopologyResolver.Validate(reference);
    }

    public static void Validate(GeometrySignature signature)
    {
        ArgumentNullException.ThrowIfNull(signature);
        static bool Positive(double value) => double.IsFinite(value) && value > 0;
        if (!Positive(signature.PositionToleranceMm) || !Positive(signature.RadiusToleranceMm) || !Positive(signature.AreaToleranceMm2) ||
            !Positive(signature.DirectionToleranceDegrees) || signature.DirectionToleranceDegrees >= 90)
            throw new ArgumentException("几何签名公差必须是有限的、正数且有界的。", nameof(signature));
        if (!Enum.IsDefined(signature.EntityKind) || !Enum.IsDefined(signature.GeometryKind)
            || signature.AnchorMm is { } anchor && !Finite(anchor) || signature.Direction is { } direction && (!Finite(direction) || !double.IsFinite(Norm(direction)) || Norm(direction) <= 1e-12) ||
            signature.RadiusMm is { } radius && !Positive(radius) || signature.AreaMm2 is { } area && !Positive(area))
            throw new ArgumentException("几何签名包含无效的几何值。", nameof(signature));
    }

    public static bool TryValidate(GeometryCandidate candidate, out string? error)
    {
        if (candidate is null) { error = "候选项为空。"; return false; }
        if (string.IsNullOrWhiteSpace(candidate.CandidateId)) { error = "候选ID是必需的。"; return false; }
        if (!GeometryOwnership.TryValidate(candidate,out error)) return false;
        try { Validate(candidate.Signature); }
        catch (ArgumentException ex) { error = ex.Message; return false; }
        error = null;
        return true;
    }

    private static bool CandidateCouldAffectResolution(GeometryRef reference, GeometryCandidate candidate)
    {
        if (candidate is null) return true;
        var signature = candidate.Signature;
        if (signature is null) return true;
        if (signature.EntityKind != reference.EntityKind) return false;
        if (reference.GeometryKind != GeometryKind.Any && signature.GeometryKind != GeometryKind.Any && signature.GeometryKind != reference.GeometryKind) return false;
        return true;
    }

    private static void Validate(GeometryDocumentIdentity document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (string.IsNullOrWhiteSpace(document.DocumentId) || !Sha(document.ModelSha256))
            throw new ArgumentException("文档标识需要文档ID和模型SHA-256。", nameof(document));
    }

    private static IReadOnlyList<string> BuildEvidence(GeometryRef reference, GeometryDocumentIdentity document, GeometryCandidate? candidate,
        bool sameRevision, bool rebound)
    {
        var evidence = new List<string>
        {
            $"document_id={document.DocumentId}",
            $"expected_model_sha256={reference.ModelSha256}",
            $"actual_model_sha256={document.ModelSha256}",
            $"same_revision={sameRevision.ToString().ToLowerInvariant()}"
        };
        if (candidate is not null) evidence.Add($"candidate_id={candidate.CandidateId}");
        if (rebound) evidence.Add("unique_signature_rebind=true");
        return evidence;
    }

    private static bool Sha(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);
    private static bool Finite(Vector3 value) => double.IsFinite(value.X) && double.IsFinite(value.Y) && double.IsFinite(value.Z);
    private static double Dot(Vector3 a, Vector3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    private static double Norm(Vector3 value) => Math.Sqrt(Dot(value, value));
    private static double Distance(Vector3 a, Vector3 b) => Norm(new(a.X - b.X, a.Y - b.Y, a.Z - b.Z));
}
