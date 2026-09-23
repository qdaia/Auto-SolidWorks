using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using CadModeling.Ir;

namespace CadModeling.Core;

[JsonConverter(typeof(JsonStringEnumConverter<ConnectivityKind>))]
public enum ConnectivityKind { ThroughHole, BlindHole, SteppedHole, InternalCavity, SolidConnection }

[JsonConverter(typeof(JsonStringEnumConverter<ConnectivityStatus>))]
public enum ConnectivityStatus { Passed, Failed, Unverifiable, Unsupported }

public sealed record AxialInterval(double StartMm, double EndMm)
{
    public double LengthMm => Math.Abs(EndMm - StartMm);
}

public sealed record AxialSegmentEvidence
{
    public required string SegmentId { get; init; }
    public double StartMm { get; init; }
    public double EndMm { get; init; }
    public double RadiusMm { get; init; }
    public required string BodyId { get; init; }
    public bool LateralBoundaryExcluded { get; init; }
    public double LengthMm => Math.Abs(EndMm - StartMm);
}

public sealed record AxialPassageEvidence
{
    public int SampleCount { get; init; }
    public int BlockedInteriorSampleCount { get; init; }
    public bool Complete { get; init; }
    public double CoverageStartMm { get; init; }
    public double CoverageEndMm { get; init; }
    public bool BodyScopeComplete { get; init; }
    public bool LateralOutletExcluded { get; init; }
    public IReadOnlyList<string> BodyIds { get; init; } = [];
    public double NumericalUncertaintyMm { get; init; }
    public string Method { get; init; } = string.Empty;
}

public sealed record AxialOpeningEvidence
{
    public required string EndId { get; init; }
    public int SampleCount { get; init; }
    public int MaterialSampleCount { get; init; }
    public int VoidSampleCount { get; init; }
    public bool Complete { get; init; }
    public double ProbeOffsetMm { get; init; }
    public double? MaterialThicknessBeyondMm { get; init; }
    public double NumericalUncertaintyMm { get; init; }
    public string Method { get; init; } = string.Empty;
}

public sealed record ConnectivityObservation
{
    public const string ContractVersion = "1.0.0";
    public string Contract { get; init; } = ContractVersion;
    public required string CheckId { get; init; }
    public required string ActualModelSha256 { get; init; }
    public bool ModelReopened { get; init; }
    public IReadOnlyList<GeometryRefResolution> GeometryResolutions { get; init; } = [];
    /// <summary>Model-space zero point used for AxialInterval/AxialSegment scalar stations.</summary>
    public Vector3? AxisOriginMm { get; init; }
    public Vector3? Axis { get; init; }
    public AxialInterval? AxialInterval { get; init; }
    public IReadOnlyList<AxialInterval> AxialSegments { get; init; } = [];
    public IReadOnlyList<AxialSegmentEvidence> AxialSegmentDetails { get; init; } = [];
    public AxialPassageEvidence? PassageEvidence { get; init; }
    public AxialOpeningEvidence? StartEvidence { get; init; }
    public AxialOpeningEvidence? EndEvidence { get; init; }
    public IReadOnlyList<string> MaterialGroupIds { get; init; } = [];
    public IReadOnlyList<string> VoidGroupIds { get; init; } = [];
    public double? MinimumGapMm { get; init; }
    public bool Complete { get; init; }
    public string Method { get; init; } = string.Empty;
    public string? Error { get; init; }
}

public sealed record ConnectivityRequirement
{
    public required string RequirementId { get; init; }
    public required string SourceFactId { get; init; }
    public required string SourceRevisionId { get; init; }
    public string? SourceSha256 { get; init; }
    public required string SourceFactFingerprint { get; init; }
    public ConnectivityKind Kind { get; init; }
    public bool RequireConnectedMaterial { get; init; } = true;
    public bool AllowMultipleBodies { get; init; }
    public int MinimumAxialSegments { get; init; } = 1;
    public double SealDetectionThresholdMm { get; init; } = 0.05;
    public double ContactToleranceMm { get; init; } = 0.01;
}

/// <summary>Narrow executor query for actual T08 B-Rep evidence.</summary>
public sealed record ConnectivityInspectionQuery
{
    public const string ContractVersion = "1.0.0";
    public string Contract { get; init; } = ContractVersion;
    public required string QueryId { get; init; }
    public required ConnectivityRequirement Requirement { get; init; }
    public IReadOnlyList<GeometryRef> GeometryRefs { get; init; } = [];
    public int EndProbeCount { get; init; } = 3;
    public double ProbeInsetMm { get; init; } = 0.005;
    public double NumericalToleranceMm { get; init; } = 0.001;
}

public sealed record ConnectivityCheck
{
    public required string RequirementId { get; init; }
    public required string SourceFactId { get; init; }
    public required string SourceRevisionId { get; init; }
    public string? SourceSha256 { get; init; }
    public required string SourceFactFingerprint { get; init; }
    public required string RequirementFingerprint { get; init; }
    public required string ActualModelSha256 { get; init; }
    public bool ModelReopened { get; init; }
    public ConnectivityKind Kind { get; init; }
    public ConnectivityStatus Status { get; init; }
    /// <summary>Producer-owned anchor of the resolved geometry scope in model millimeters.</summary>
    public Vector3? GeometryScopeAnchorMm { get; init; }
    /// <summary>Producer-owned fingerprint of the resolved geometry references/candidates used for this check.</summary>
    public string? GeometryScopeFingerprint { get; init; }
    /// <summary>Producer-owned model-space zero point for ObservedAxialInterval scalar stations.</summary>
    public Vector3? ObservedAxisOriginMm { get; init; }
    public Vector3? ObservedAxis { get; init; }
    public AxialInterval? ObservedAxialInterval { get; init; }
    public AxialOpeningEvidence? StartEvidence { get; init; }
    public AxialOpeningEvidence? EndEvidence { get; init; }
    public IReadOnlyList<AxialSegmentEvidence> AxialSegmentDetails { get; init; } = [];
    public bool ObservationComplete { get; init; }
    public string Message { get; init; } = string.Empty;
    public IReadOnlyDictionary<string, string> Evidence { get; init; } = new Dictionary<string, string>();
}

public static class ConnectivityVerifier
{
    public static void Validate(ConnectivityInspectionQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        Validate(query.Requirement);
        if (string.IsNullOrWhiteSpace(query.QueryId) || query.GeometryRefs.Count == 0 ||
            query.GeometryRefs.Select(item => item.RefId).Distinct(StringComparer.Ordinal).Count() != query.GeometryRefs.Count)
            throw new ArgumentException("Connectivity inspection requires a nonempty query id and unique GeometryRefs.", nameof(query));
        if (query.EndProbeCount < 3 || query.EndProbeCount > 32 || !FinitePositive(query.ProbeInsetMm) ||
            !FinitePositive(query.NumericalToleranceMm) || query.NumericalToleranceMm >= query.ProbeInsetMm)
            throw new ArgumentException("Connectivity probe count/tolerances are outside the bounded supported range.", nameof(query));
    }

    public static ConnectivityCheck Evaluate(ConnectivityRequirement requirement, ConnectivityObservation observation)
    {
        Validate(requirement);
        Validate(observation);
        if (observation.GeometryResolutions.Count == 0)
            return Result(ConnectivityStatus.Unverifiable, "Connectivity check has no resolved geometry scope.");
        if (observation.GeometryResolutions.Any(item => item.Status != GeometryRefResolutionStatus.Resolved))
            return Result(ConnectivityStatus.Unverifiable, "At least one geometry reference is stale, missing, ambiguous, unsupported, or belongs to another document.");
        if (observation.GeometryResolutions.Any(item => !item.ResolvedModelSha256.Equals(observation.ActualModelSha256, StringComparison.OrdinalIgnoreCase)))
            return Result(ConnectivityStatus.Unverifiable, "Connectivity geometry references were resolved against a different model fingerprint.");
        if (observation.GeometryResolutions.Any(item => item.Reference.SourceRevisionId is { Length: > 0 } revision &&
            !revision.Equals(requirement.SourceRevisionId, StringComparison.Ordinal)))
            return Result(ConnectivityStatus.Unverifiable, "Connectivity GeometryRef evidence belongs to a different source revision than the current requirement.");
        if (!observation.ModelReopened)
            return Result(ConnectivityStatus.Unverifiable, "Connectivity evidence was not captured from a saved and reopened model.");
        if (!observation.Complete)
            return Result(ConnectivityStatus.Unverifiable, observation.Error ?? "Connectivity evidence is incomplete.");

        return requirement.Kind switch
        {
            ConnectivityKind.ThroughHole => EvaluateHole(expectThrough: true),
            ConnectivityKind.BlindHole => EvaluateHole(expectThrough: false),
            ConnectivityKind.SteppedHole => EvaluateStepped(),
            ConnectivityKind.InternalCavity => EvaluateCavity(),
            ConnectivityKind.SolidConnection => EvaluateConnection(),
            _ => Result(ConnectivityStatus.Unsupported, "Connectivity kind is outside the supported first-stage topology scope.")
        };

        ConnectivityCheck EvaluateHole(bool expectThrough)
        {
            if (!ValidAxis(observation.Axis) || observation.AxialInterval is not { } interval || !FinitePositive(interval.LengthMm) ||
                observation.StartEvidence is not { } start || observation.EndEvidence is not { } end)
                return Result(ConnectivityStatus.Unverifiable, "Axial hole verification requires a finite axis, interval, and both end probes.");
            var passageProblem = ValidatePassage(interval, requireRadiusSteps: false);
            if (passageProblem is not null) return passageProblem;
            var a = ClassifyEnd(start); var b = ClassifyEnd(end);
            if (a == EndState.Unknown || b == EndState.Unknown)
                return Result(ConnectivityStatus.Unverifiable, "End probing is incomplete or mixed; one-point ray inference is not accepted.");
            if (expectThrough)
            {
                if (a == EndState.Open && b == EndState.Open)
                    return Result(ConnectivityStatus.Passed, "Both axial ends are open under bounded multi-point material probes.");
                return Result(ConnectivityStatus.Failed, "A required through hole has residual material at one or both axial ends.");
            }
            if ((a == EndState.Open && b == EndState.Closed) || (a == EndState.Closed && b == EndState.Open))
                return Result(ConnectivityStatus.Passed, "Exactly one axial end is open and the opposite end has material-bottom evidence.");
            return Result(ConnectivityStatus.Failed, "Blind-hole topology requires exactly one opening and one material bottom.");
        }

        ConnectivityCheck EvaluateStepped()
        {
            if (observation.AxialInterval is not { } interval || !FinitePositive(interval.LengthMm))
                return Result(ConnectivityStatus.Unverifiable, "Stepped-hole verification requires a bounded axial interval.");
            var passageProblem = ValidatePassage(interval, requireRadiusSteps: true);
            if (passageProblem is not null) return passageProblem;
            // Reuse end evidence without pretending the step count alone proves through/blind intent.
            if (observation.StartEvidence is null || observation.EndEvidence is null)
                return Result(ConnectivityStatus.Unverifiable, "Stepped-hole wall segments were measured, but end topology was not checked.");
            if (ClassifyEnd(observation.StartEvidence) == EndState.Unknown || ClassifyEnd(observation.EndEvidence) == EndState.Unknown)
                return Result(ConnectivityStatus.Unverifiable, "Stepped-hole end topology probes are incomplete, mixed, or below numerical confidence.");
            return Result(ConnectivityStatus.Passed, "Stepped-hole wall intervals are contiguous and both end states were explicitly probed.");
        }

        ConnectivityCheck EvaluateCavity()
        {
            if (observation.VoidGroupIds.Count == 0)
                return Result(ConnectivityStatus.Failed, "No bounded void/cavity group was observed.");
            if (observation.AxialInterval is not { } interval)
                return Result(ConnectivityStatus.Unverifiable, "Internal-cavity verification requires a bounded axial interval in the supported scope.");
            var passageProblem = ValidatePassage(interval, requireRadiusSteps: false);
            if (passageProblem is not null) return passageProblem;
            if (ClassifyEnd(observation.StartEvidence ?? EmptyEnd("start")) != EndState.Closed ||
                ClassifyEnd(observation.EndEvidence ?? EmptyEnd("end")) != EndState.Closed)
                return Result(ConnectivityStatus.Unverifiable, "Internal cavity requires independently measured closed ends in this bounded axial scope.");
            return Result(ConnectivityStatus.Passed, "At least one explicitly measured void group exists in the scoped cavity check.");
        }

        ConnectivityCheck? ValidatePassage(AxialInterval interval, bool requireRadiusSteps)
        {
            if (observation.PassageEvidence is not { } passage || !passage.Complete || passage.SampleCount < 3 ||
                passage.BlockedInteriorSampleCount < 0 || passage.BlockedInteriorSampleCount > passage.SampleCount ||
                !double.IsFinite(passage.CoverageStartMm) || !double.IsFinite(passage.CoverageEndMm) ||
                !double.IsFinite(passage.NumericalUncertaintyMm) || passage.NumericalUncertaintyMm < 0)
                return Result(ConnectivityStatus.Unverifiable, "Axial void continuity was not completely measured across the supported passage.");
            var expectedMin = Math.Min(interval.StartMm, interval.EndMm);
            var expectedMax = Math.Max(interval.StartMm, interval.EndMm);
            var actualMin = Math.Min(passage.CoverageStartMm, passage.CoverageEndMm);
            var actualMax = Math.Max(passage.CoverageStartMm, passage.CoverageEndMm);
            if (actualMin > expectedMin + requirement.ContactToleranceMm || actualMax < expectedMax - requirement.ContactToleranceMm)
                return Result(ConnectivityStatus.Unverifiable, "Axial passage probes do not cover the complete declared hole/cavity interval.");
            if (!passage.BodyScopeComplete || passage.BodyIds.Count == 0 || passage.BodyIds.Any(string.IsNullOrWhiteSpace))
                return Result(ConnectivityStatus.Unverifiable, "Axial passage evidence does not freeze the complete participating solid-body scope.");
            if (!passage.LateralOutletExcluded)
                return Result(ConnectivityStatus.Unverifiable, "A lateral outlet/branch cannot be excluded from the supported axial void scope.");
            if (passage.BlockedInteriorSampleCount > 0)
                return Result(ConnectivityStatus.Failed, "Material intersects the interior axial passage between the declared end conditions.");

            var details = observation.AxialSegmentDetails.OrderBy(segment => Math.Min(segment.StartMm, segment.EndMm)).ToArray();
            if (details.Length == 0)
                return Result(ConnectivityStatus.Unverifiable, "Axial passage has no radius/body segment evidence.");
            if (details.Any(segment => string.IsNullOrWhiteSpace(segment.SegmentId) || string.IsNullOrWhiteSpace(segment.BodyId) ||
                !FinitePositive(segment.LengthMm) || !FinitePositive(segment.RadiusMm) || !segment.LateralBoundaryExcluded))
                return Result(ConnectivityStatus.Unverifiable, "Axial segment evidence is incomplete, non-finite, or has unresolved lateral trimming.");
            var bodyScope = passage.BodyIds.ToHashSet(StringComparer.Ordinal);
            if (details.Any(segment => !bodyScope.Contains(segment.BodyId)))
                return Result(ConnectivityStatus.Unverifiable, "Axial segment evidence references a body outside the frozen complete passage body scope.");
            for (var i = 1; i < details.Length; i++)
            {
                var priorEnd = Math.Max(details[i - 1].StartMm, details[i - 1].EndMm);
                var nextStart = Math.Min(details[i].StartMm, details[i].EndMm);
                if (nextStart - priorEnd > requirement.ContactToleranceMm)
                    return Result(ConnectivityStatus.Failed, "Axial wall segments contain a material gap/discontinuity inside the declared void passage.");
                if (nextStart < priorEnd - requirement.ContactToleranceMm)
                    return Result(ConnectivityStatus.Unverifiable,
                        "Overlapping axial wall segments do not establish an ordered hole profile; nested/coextensive radii cannot be promoted to a step.");
            }
            var detailMin = details.Min(segment => Math.Min(segment.StartMm, segment.EndMm));
            var detailMax = details.Max(segment => Math.Max(segment.StartMm, segment.EndMm));
            if (detailMin > expectedMin + requirement.ContactToleranceMm || detailMax < expectedMax - requirement.ContactToleranceMm)
                return Result(ConnectivityStatus.Unverifiable, "Measured cylindrical wall segments do not cover the declared axial interval.");
            if (requireRadiusSteps)
            {
                if (details.Length < Math.Max(2, requirement.MinimumAxialSegments))
                    return Result(ConnectivityStatus.Failed, "Required stepped hole does not contain the declared number of independently measured axial segments.");
                var distinctRadii = details.Select(segment => segment.RadiusMm).Order().Aggregate(new List<double>(), (list, radius) =>
                { if (list.Count == 0 || Math.Abs(list[^1] - radius) > requirement.ContactToleranceMm) list.Add(radius); return list; });
                if (distinctRadii.Count < 2)
                    return Result(ConnectivityStatus.Failed, "Multiple intervals without a measured radius change do not prove a stepped hole.");
            }
            return null;
        }

        ConnectivityCheck EvaluateConnection()
        {
            if (observation.MaterialGroupIds.Count == 0)
                return Result(ConnectivityStatus.Unverifiable, "No material connectivity groups were measured.");
            var distinct = observation.MaterialGroupIds.Distinct(StringComparer.Ordinal).Count();
            if (requirement.RequireConnectedMaterial)
            {
                if (distinct == 1) return Result(ConnectivityStatus.Passed, "All scoped entities belong to one connected solid-material group.");
                return Result(ConnectivityStatus.Failed, observation.MinimumGapMm is { } gap
                    ? $"Scoped material is split into {distinct} groups; measured minimum gap is {gap:G6} mm."
                    : $"Scoped material is split into {distinct} disconnected solid groups.");
            }
            if (distinct > 1 && !requirement.AllowMultipleBodies)
                return Result(ConnectivityStatus.Failed, "Multiple solid bodies are present but the source requirement does not allow them.");
            return Result(ConnectivityStatus.Passed, distinct > 1
                ? "Multiple solid bodies are explicitly allowed; body count alone is not treated as a failure."
                : "Scoped material is valid for the declared non-connectivity requirement.");
        }

        EndState ClassifyEnd(AxialOpeningEvidence evidence)
        {
            if (!evidence.Complete || evidence.SampleCount < 3 || evidence.MaterialSampleCount < 0 || evidence.VoidSampleCount < 0 ||
                evidence.MaterialSampleCount + evidence.VoidSampleCount != evidence.SampleCount ||
                !FinitePositive(evidence.ProbeOffsetMm) || !double.IsFinite(evidence.NumericalUncertaintyMm) || evidence.NumericalUncertaintyMm < 0)
                return EndState.Unknown;
            if (evidence.MaterialSampleCount == 0 && evidence.VoidSampleCount == evidence.SampleCount) return EndState.Open;
            if (evidence.MaterialSampleCount == evidence.SampleCount)
            {
                if (evidence.MaterialThicknessBeyondMm is not { } thickness || !double.IsFinite(thickness) || thickness < 0)
                    return EndState.Unknown;
                // A positively measured thin seal is still a seal. The frozen threshold exists to prevent a
                // visually negligible cap from being treated as open; only uncertainty that reaches zero leaves it unknown.
                if (thickness <= evidence.NumericalUncertaintyMm)
                    return EndState.Unknown;
                return EndState.Closed;
            }
            return EndState.Unknown;
        }

        ConnectivityCheck Result(ConnectivityStatus status, string message) => new()
        {
            RequirementId = requirement.RequirementId,
            SourceFactId = requirement.SourceFactId,
            SourceRevisionId = requirement.SourceRevisionId,
            SourceSha256 = requirement.SourceSha256,
            SourceFactFingerprint = requirement.SourceFactFingerprint,
            RequirementFingerprint = Fingerprint(requirement),
            ActualModelSha256 = observation.ActualModelSha256,
            ModelReopened = observation.ModelReopened,
            Kind = requirement.Kind,
            Status = status,
            GeometryScopeAnchorMm = GeometryScopeAnchor(observation, requirement.ContactToleranceMm),
            GeometryScopeFingerprint = GeometryScopeFingerprint(observation),
            ObservedAxisOriginMm = observation.AxisOriginMm,
            ObservedAxis = observation.Axis,
            ObservedAxialInterval = observation.AxialInterval,
            StartEvidence = observation.StartEvidence,
            EndEvidence = observation.EndEvidence,
            AxialSegmentDetails = observation.AxialSegmentDetails,
            ObservationComplete = observation.Complete,
            Message = message,
            Evidence = new Dictionary<string, string>
            {
                ["actual_model_sha256"] = observation.ActualModelSha256,
                ["source_revision_id"] = requirement.SourceRevisionId,
                ["source_fact_fingerprint"] = requirement.SourceFactFingerprint,
                ["requirement_fingerprint"] = Fingerprint(requirement),
                ["model_reopened"] = observation.ModelReopened.ToString().ToLowerInvariant(),
                ["method"] = observation.Method,
                ["material_group_count"] = observation.MaterialGroupIds.Distinct(StringComparer.Ordinal).Count().ToString(),
                ["void_group_count"] = observation.VoidGroupIds.Distinct(StringComparer.Ordinal).Count().ToString()
            }
        };
    }

    private static Vector3? GeometryScopeAnchor(ConnectivityObservation observation,double toleranceMm)
    {
        var anchors=observation.GeometryResolutions
            .Where(item=>item.Status==GeometryRefResolutionStatus.Resolved)
            .Select(item=>item.Candidate?.Signature.AnchorMm)
            .OfType<Vector3>()
            .Where(Finite)
            .ToArray();
        if(anchors.Length==0)return null;
        if(observation.Axis is not { } axis||!ValidAxis(axis))return anchors.Length==1?anchors[0]:null;
        var unit=Unit(axis);var first=anchors[0];
        if(anchors.Any(anchor=>LateralDistance(anchor,first,unit)>toleranceMm))return null;
        return new(anchors.Average(a=>a.X),anchors.Average(a=>a.Y),anchors.Average(a=>a.Z));
    }

    private static string? GeometryScopeFingerprint(ConnectivityObservation observation)
    {
        if(observation.GeometryResolutions.Count==0||observation.GeometryResolutions.Any(item=>item.Status!=GeometryRefResolutionStatus.Resolved||item.Candidate is null))return null;
        var rows=observation.GeometryResolutions.OrderBy(item=>item.Reference.RefId,StringComparer.Ordinal).Select(item=>string.Join("|",new[]{
            item.Reference.RefId,item.ResolvedModelSha256,item.Candidate!.CandidateId,item.Candidate.NativePersistentReference??string.Empty,
            item.Candidate.Signature.EntityKind.ToString(),item.Candidate.Signature.GeometryKind.ToString(),
            Vec(item.Candidate.Signature.AnchorMm),Vec(item.Candidate.Signature.Direction),
            item.Candidate.Signature.RadiusMm?.ToString("R",System.Globalization.CultureInfo.InvariantCulture)??string.Empty
        }));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n",rows))));
    }

    private static string Vec(Vector3? value)=>value is { } v
        ?string.Join(",",new[]{v.X.ToString("R",System.Globalization.CultureInfo.InvariantCulture),v.Y.ToString("R",System.Globalization.CultureInfo.InvariantCulture),v.Z.ToString("R",System.Globalization.CultureInfo.InvariantCulture)})
        :string.Empty;
    private static bool Finite(Vector3 value)=>double.IsFinite(value.X)&&double.IsFinite(value.Y)&&double.IsFinite(value.Z);
    private static Vector3 Unit(Vector3 value){var n=Math.Sqrt(value.X*value.X+value.Y*value.Y+value.Z*value.Z);return new(value.X/n,value.Y/n,value.Z/n);}
    private static double LateralDistance(Vector3 point,Vector3 origin,Vector3 axis)
    {
        var dx=point.X-origin.X;var dy=point.Y-origin.Y;var dz=point.Z-origin.Z;var along=dx*axis.X+dy*axis.Y+dz*axis.Z;
        var x=dx-along*axis.X;var y=dy-along*axis.Y;var z=dz-along*axis.Z;return Math.Sqrt(x*x+y*y+z*z);
    }

    public static void Validate(ConnectivityRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(requirement);
        if (string.IsNullOrWhiteSpace(requirement.RequirementId) || string.IsNullOrWhiteSpace(requirement.SourceFactId) || string.IsNullOrWhiteSpace(requirement.SourceRevisionId) || !Sha(requirement.SourceFactFingerprint) ||
            requirement.SourceSha256 is { Length: > 0 } sourceSha && !Sha(sourceSha) ||
            requirement.MinimumAxialSegments < 1 || !FinitePositive(requirement.SealDetectionThresholdMm) || !FinitePositive(requirement.ContactToleranceMm))
            throw new ArgumentException("Connectivity requirement contains missing identity or invalid thresholds.", nameof(requirement));
    }

    public static string Fingerprint(ConnectivityRequirement requirement)
    {
        Validate(requirement);
        var canonical = string.Join("|", new[]
        {
            requirement.RequirementId, requirement.SourceFactId, requirement.SourceRevisionId, requirement.SourceFactFingerprint.ToUpperInvariant(),
            requirement.Kind.ToString(), requirement.RequireConnectedMaterial.ToString(), requirement.AllowMultipleBodies.ToString(),
            requirement.MinimumAxialSegments.ToString(System.Globalization.CultureInfo.InvariantCulture),
            requirement.SealDetectionThresholdMm.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            requirement.ContactToleranceMm.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    public static void Validate(ConnectivityObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (string.IsNullOrWhiteSpace(observation.CheckId) || observation.ActualModelSha256.Length != 64 || !observation.ActualModelSha256.All(Uri.IsHexDigit))
            throw new ArgumentException("Connectivity observation requires check id and actual model SHA-256.", nameof(observation));
        if (observation.MinimumGapMm is { } gap && (!double.IsFinite(gap) || gap < 0))
            throw new ArgumentException("Connectivity minimum gap must be finite and non-negative.", nameof(observation));
        if (observation.AxisOriginMm is { } axisOrigin && !Finite(axisOrigin))
            throw new ArgumentException("Connectivity axial-coordinate origin must be finite when supplied.", nameof(observation));
        if (observation.AxialSegments.Any(segment => !double.IsFinite(segment.StartMm) || !double.IsFinite(segment.EndMm) || segment.LengthMm <= 0))
            throw new ArgumentException("Connectivity axial segments must be finite and nonzero.", nameof(observation));
        if (observation.AxialSegmentDetails.Any(segment => !double.IsFinite(segment.StartMm) || !double.IsFinite(segment.EndMm) ||
            !FinitePositive(segment.LengthMm) || !FinitePositive(segment.RadiusMm)))
            throw new ArgumentException("Connectivity axial segment details must contain finite nonzero intervals and radii.", nameof(observation));
    }

    private enum EndState { Open, Closed, Unknown }
    private static AxialOpeningEvidence EmptyEnd(string id) => new() { EndId = id, Complete = false };
    private static bool Sha(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);
    private static bool FinitePositive(double value) => double.IsFinite(value) && value > 0;
    private static bool ValidAxis(Vector3? value) => value is { } axis && double.IsFinite(axis.X) && double.IsFinite(axis.Y) && double.IsFinite(axis.Z) &&
        Math.Sqrt(axis.X * axis.X + axis.Y * axis.Y + axis.Z * axis.Z) > 1e-12;
}
