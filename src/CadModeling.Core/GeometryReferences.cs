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
}

public static class GeometryRefResolver
{
    public static GeometryRefResolution Resolve(GeometryRef reference, GeometryDocumentIdentity document,
        IReadOnlyList<GeometryCandidate> candidates)
    {
        Validate(reference);
        Validate(document);
        ArgumentNullException.ThrowIfNull(candidates);

        var sameRevision = reference.ModelSha256.Equals(document.ModelSha256, StringComparison.OrdinalIgnoreCase) &&
            (reference.DocumentRevision is null || document.DocumentRevision is null || reference.DocumentRevision == document.DocumentRevision);

        var relevantCandidates = candidates.Where(candidate => CandidateCouldAffectResolution(reference, candidate)).ToArray();
        var invalidCandidates = relevantCandidates.Where(candidate => !TryValidate(candidate, out _)).ToArray();
        if (invalidCandidates.Length > 0)
            return Result(GeometryRefResolutionStatus.Unsupported,
                $"{invalidCandidates.Length} candidate(s) in the GeometryRef resolution scope contain invalid/non-finite geometry and cannot be certified.",
                ids: invalidCandidates.Select(candidate => candidate.CandidateId).ToArray());

        if (reference.SourceRevisionId is { Length: > 0 } expectedSourceRevision &&
            !expectedSourceRevision.Equals(document.SourceRevisionId, StringComparison.Ordinal))
            return Result(GeometryRefResolutionStatus.Stale, "Source-fact revision changed; geometry reference requires revalidation before reuse.");

        if (!reference.DocumentId.Equals(document.DocumentId, StringComparison.Ordinal) ||
            reference.DocumentPath is { Length: > 0 } expectedPath && document.DocumentPath is { Length: > 0 } actualPath &&
            !Path.GetFullPath(expectedPath).Equals(Path.GetFullPath(actualPath), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            return Result(GeometryRefResolutionStatus.WrongDocument, "Geometry reference belongs to a different saved document.");
        var native = reference.NativePersistentReference is null ? null : candidates.FirstOrDefault(candidate =>
            candidate.NativePersistentReference is not null && candidate.NativePersistentReference.Equals(reference.NativePersistentReference, StringComparison.Ordinal));

        if (sameRevision && native is not null)
        {
            if (!Matches(reference, native))
                return Result(GeometryRefResolutionStatus.Stale,
                    "Native persistent reference recovered an object whose geometry signature no longer matches; refusing silent retargeting.", native,
                    nativeRecovered: true);
            return Result(GeometryRefResolutionStatus.Resolved, "Native persistent reference and geometry signature both match.", native,
                nativeRecovered: true);
        }

        var matches = candidates.Where(candidate => Matches(reference, candidate)).ToArray();
        if (matches.Length == 0)
            return Result(sameRevision ? GeometryRefResolutionStatus.Missing : GeometryRefResolutionStatus.Stale,
                sameRevision ? "No object matches the frozen geometry signature." : "Document revision changed and the referenced object cannot be uniquely recovered.",
                native, nativeRecovered: native is not null);
        if (matches.Length > 1)
            return Result(GeometryRefResolutionStatus.Ambiguous,
                $"Geometry signature matches {matches.Length} objects; automatic first-match rebinding is forbidden.",
                native, matches.Select(item => item.CandidateId).ToArray(), nativeRecovered: native is not null);

        return Result(GeometryRefResolutionStatus.Resolved,
            sameRevision ? "A unique geometry-signature match resolved the reference." : "A changed document revision was uniquely rebound by the frozen geometry signature.",
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
    {
        if (!TryValidate(candidate, out _)) return false;
        if (!string.Equals(reference.InputToFeature,candidate.InputToFeature,StringComparison.Ordinal)) return false;
        if (candidate.Signature.EntityKind != reference.EntityKind || candidate.Signature.EntityKind != reference.Signature.EntityKind)
            return false;
        if (reference.GeometryKind != GeometryKind.Any && candidate.Signature.GeometryKind != reference.GeometryKind)
            return false;
        if (reference.Signature.GeometryKind != GeometryKind.Any && candidate.Signature.GeometryKind != reference.Signature.GeometryKind)
            return false;
        if (reference.FeatureId is { Length: > 0 } feature && !feature.Equals(candidate.FeatureId, StringComparison.Ordinal))
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
            if (a <= 1e-12 || b <= 1e-12) return false;
            var cosine = Math.Clamp(Math.Abs(Dot(direction, actualDirection) / (a * b)), -1, 1);
            var degrees = Math.Acos(cosine) * 180 / Math.PI;
            if (degrees > expected.DirectionToleranceDegrees) return false;
        }
        return true;
    }

    public static string Fingerprint(GeometryRef reference)
    {
        Validate(reference);
        var signature = reference.Signature;
        static string V(Vector3? value) => value is null ? "" : string.Join(",", new[] { value.X, value.Y, value.Z }
            .Select(x => x.ToString("R", System.Globalization.CultureInfo.InvariantCulture)));
        static string D(double? value) => value?.ToString("R", System.Globalization.CultureInfo.InvariantCulture) ?? "";
        var canonical = string.Join("|", new[]
        {
            reference.Contract, reference.RefId, reference.DocumentId, reference.DocumentPath ?? "", reference.DocumentRevision ?? "",
            reference.ModelSha256.ToUpperInvariant(), reference.NativePersistentReference ?? "", reference.EntityKind.ToString(), reference.GeometryKind.ToString(),
            reference.FeatureId ?? "", reference.OperationId ?? "", reference.SourceRevisionId ?? "", reference.InputToFeature ?? "",
            string.Join(",", reference.SourceFactIds.Order(StringComparer.Ordinal)), signature.EntityKind.ToString(), signature.GeometryKind.ToString(),
            V(signature.AnchorMm), V(signature.Direction), D(signature.RadiusMm), D(signature.AreaMm2),
            signature.PositionToleranceMm.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            signature.RadiusToleranceMm.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            signature.AreaToleranceMm2.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            signature.DirectionToleranceDegrees.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    public static void Validate(GeometryRef reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (string.IsNullOrWhiteSpace(reference.RefId) || string.IsNullOrWhiteSpace(reference.DocumentId) || !Sha(reference.ModelSha256))
            throw new ArgumentException("GeometryRef requires ref id, document id and a SHA-256 model fingerprint.", nameof(reference));
        if (reference.Signature.EntityKind != reference.EntityKind ||
            reference.GeometryKind != GeometryKind.Any && reference.Signature.GeometryKind != GeometryKind.Any && reference.Signature.GeometryKind != reference.GeometryKind)
            throw new ArgumentException("GeometryRef entity/geometry kinds must agree with its signature.", nameof(reference));
        Validate(reference.Signature);
        if(reference.InputToFeature is not null&&(string.IsNullOrWhiteSpace(reference.InputToFeature)||reference.EntityKind!=EntityKind.Edge))
            throw new ArgumentException("Feature-input geometry requires an edge and an explicit native feature name.",nameof(reference));
        if (reference.SourceFactIds.Any(string.IsNullOrWhiteSpace) || reference.SourceFactIds.Distinct(StringComparer.Ordinal).Count() != reference.SourceFactIds.Count)
            throw new ArgumentException("GeometryRef source fact ids must be nonempty and unique.", nameof(reference));
        if (reference.SourceFactIds.Count > 0 && string.IsNullOrWhiteSpace(reference.SourceRevisionId))
            throw new ArgumentException("GeometryRef values derived from source facts must bind the source revision id.", nameof(reference));
    }

    public static void Validate(GeometrySignature signature)
    {
        ArgumentNullException.ThrowIfNull(signature);
        static bool Positive(double value) => double.IsFinite(value) && value > 0;
        if (!Positive(signature.PositionToleranceMm) || !Positive(signature.RadiusToleranceMm) || !Positive(signature.AreaToleranceMm2) ||
            !Positive(signature.DirectionToleranceDegrees) || signature.DirectionToleranceDegrees >= 90)
            throw new ArgumentException("Geometry signature tolerances must be finite, positive and bounded.", nameof(signature));
        if (signature.AnchorMm is { } anchor && !Finite(anchor) || signature.Direction is { } direction && (!Finite(direction) || Norm(direction) <= 1e-12) ||
            signature.RadiusMm is { } radius && !Positive(radius) || signature.AreaMm2 is { } area && !Positive(area))
            throw new ArgumentException("Geometry signature contains invalid geometric values.", nameof(signature));
    }

    public static bool TryValidate(GeometryCandidate candidate, out string? error)
    {
        if (candidate is null) { error = "Candidate is null."; return false; }
        if (string.IsNullOrWhiteSpace(candidate.CandidateId)) { error = "Candidate id is required."; return false; }
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
            throw new ArgumentException("Document identity requires document id and model SHA-256.", nameof(document));
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
