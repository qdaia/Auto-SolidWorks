using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using CadModeling.Ir;

namespace CadModeling.Core;

[JsonConverter(typeof(JsonStringEnumConverter<MeasurementKind>))]
public enum MeasurementKind
{
    CylinderDiameter,
    CylinderRadius,
    PlaneSeparation,
    AxisPointX,
    AxisPointY,
    AxisPointZ,
    AxisDirection,
    FiniteEntityValidity
}

[JsonConverter(typeof(JsonStringEnumConverter<GeometryMeasurementStatus>))]
public enum GeometryMeasurementStatus { Measured, Unverifiable, Unsupported, InvalidGeometry, WrongModel }

[JsonConverter(typeof(JsonStringEnumConverter<RequirementCheckStatus>))]
public enum RequirementCheckStatus { Passed, Failed, Unverifiable, Unsupported, Stale, Ambiguous, Missing, WrongDocument }

public sealed record MeasurementQuery
{
    public const string ContractVersion = "1.0.0";
    public string Contract { get; init; } = ContractVersion;
    public required string QueryId { get; init; }
    public required GeometryRef Geometry { get; init; }
    public GeometryRef? SecondaryGeometry { get; init; }
    public MeasurementKind Kind { get; init; }
    public DrawingValueUnit Unit { get; init; } = DrawingValueUnit.Millimeter;
    public double NumericalTolerance { get; init; } = 0.001;
}

public sealed record MeasurementResult
{
    public required string QueryId { get; init; }
    public required string QueryFingerprint { get; init; }
    public MeasurementKind Kind { get; init; }
    public required GeometryRefResolution ReferenceResolution { get; init; }
    public GeometryRefResolution? SecondaryReferenceResolution { get; init; }
    public GeometryMeasurementStatus Status { get; init; }
    public double? ScalarValue { get; init; }
    public Vector3? VectorValue { get; init; }
    public DrawingValueUnit Unit { get; init; } = DrawingValueUnit.Millimeter;
    public required string Method { get; init; }
    public double NumericalUncertainty { get; init; }
    public required string ActualModelSha256 { get; init; }
    public bool ModelReopened { get; init; }
    public string SupportScope { get; init; } = string.Empty;
    public IReadOnlyDictionary<string, string> Evidence { get; init; } = new Dictionary<string, string>();
    public string Message { get; init; } = string.Empty;
}

public sealed record MeasurementRequirement
{
    public required string RequirementId { get; init; }
    public required string QueryId { get; init; }
    public required string SourceFactId { get; init; }
    public required string SourceRevisionId { get; init; }
    public string? SourceSha256 { get; init; }
    public string? SourceFactFingerprint { get; init; }
    public double? ExpectedScalar { get; init; }
    public Vector3? ExpectedVector { get; init; }
    public DrawingValueUnit Unit { get; init; } = DrawingValueUnit.Millimeter;
    public double Tolerance { get; init; } = 0.05;
    public double DirectionToleranceDegrees { get; init; } = 0.25;
}

public sealed record RequirementCheck
{
    public required string RequirementId { get; init; }
    public required string QueryId { get; init; }
    public required string SourceFactId { get; init; }
    public required string SourceRevisionId { get; init; }
    public string? SourceSha256 { get; init; }
    public string? SourceFactFingerprint { get; init; }
    public required string RequirementFingerprint { get; init; }
    public RequirementCheckStatus Status { get; init; }
    public double? ExpectedScalar { get; init; }
    public double? ActualScalar { get; init; }
    public Vector3? ExpectedVector { get; init; }
    public Vector3? ActualVector { get; init; }
    public DrawingValueUnit Unit { get; init; }
    public double? Difference { get; init; }
    public string Message { get; init; } = string.Empty;
    public IReadOnlyDictionary<string, string> Evidence { get; init; } = new Dictionary<string, string>();
}

public static class DirectionalMeasurementVerifier
{
    public static RequirementCheck Compare(MeasurementQuery query, MeasurementResult measurement, MeasurementRequirement requirement)
    {
        Validate(query);
        Validate(requirement);
        ArgumentNullException.ThrowIfNull(measurement);
        if (!query.QueryId.Equals(measurement.QueryId, StringComparison.Ordinal) || !query.QueryId.Equals(requirement.QueryId, StringComparison.Ordinal))
            throw new ArgumentException("Measurement query, result and source requirement ids must agree.");
        if (!measurement.QueryFingerprint.Equals(Fingerprint(query), StringComparison.OrdinalIgnoreCase) || measurement.Kind != query.Kind)
            return Result(RequirementCheckStatus.Stale, "Measurement result does not belong to the current complete query fingerprint/kind.");
        if (!measurement.ReferenceResolution.Reference.RefId.Equals(query.Geometry.RefId, StringComparison.Ordinal))
            throw new ArgumentException("Measurement result was produced for a different GeometryRef.");
        if (!GeometryRefResolver.Fingerprint(measurement.ReferenceResolution.Reference).Equals(GeometryRefResolver.Fingerprint(query.Geometry), StringComparison.OrdinalIgnoreCase))
            return Result(RequirementCheckStatus.Stale, "Primary GeometryRef identity changed after the measurement was produced.");
        if (query.SecondaryGeometry is null != (measurement.SecondaryReferenceResolution is null))
            return Result(RequirementCheckStatus.Unverifiable, "Measurement result does not contain the secondary GeometryRef resolution required by the current query.");
        if (query.SecondaryGeometry is not null && measurement.SecondaryReferenceResolution is { } measuredSecondary &&
            !GeometryRefResolver.Fingerprint(measuredSecondary.Reference).Equals(GeometryRefResolver.Fingerprint(query.SecondaryGeometry), StringComparison.OrdinalIgnoreCase))
            return Result(RequirementCheckStatus.Stale, "Secondary GeometryRef identity changed after the measurement was produced.");
        if (query.Geometry.SourceRevisionId is { Length: > 0 } sourceRevision &&
            !sourceRevision.Equals(requirement.SourceRevisionId, StringComparison.Ordinal))
            return Result(RequirementCheckStatus.Stale, "Geometry reference and source requirement belong to different source revisions.");
        if (query.Geometry.SourceFactIds.Count > 0 && !query.Geometry.SourceFactIds.Contains(requirement.SourceFactId, StringComparer.Ordinal))
            return Result(RequirementCheckStatus.Stale, "Geometry reference is not bound to the source fact used by this requirement.");

        var refStatus = Map(measurement.ReferenceResolution.Status);
        if (refStatus is not RequirementCheckStatus.Passed)
            return Result(refStatus, "Geometry reference did not resolve; source requirement was not compared.");
        if (measurement.SecondaryReferenceResolution is { } secondary && Map(secondary.Status) is { } secondaryStatus && secondaryStatus is not RequirementCheckStatus.Passed)
            return Result(secondaryStatus, "Secondary geometry reference did not resolve; source requirement was not compared.");
        if (!measurement.ActualModelSha256.Equals(measurement.ReferenceResolution.ResolvedModelSha256, StringComparison.OrdinalIgnoreCase))
            return Result(RequirementCheckStatus.Stale, "Measurement model fingerprint differs from the model on which GeometryRef resolution was performed.");
        if (measurement.SecondaryReferenceResolution is { } secondaryResolution &&
            !measurement.ActualModelSha256.Equals(secondaryResolution.ResolvedModelSha256, StringComparison.OrdinalIgnoreCase))
            return Result(RequirementCheckStatus.Stale, "Secondary GeometryRef was resolved against a different model than the measurement result.");
        if (!measurement.ModelReopened)
            return Result(RequirementCheckStatus.Unverifiable, "Actual geometry was not measured from a saved and reopened model.");
        if (measurement.Status != GeometryMeasurementStatus.Measured)
            return Result(measurement.Status == GeometryMeasurementStatus.Unsupported ? RequirementCheckStatus.Unsupported : RequirementCheckStatus.Unverifiable,
                measurement.Message.Length == 0 ? "Actual geometry measurement is not valid." : measurement.Message);
        if (!double.IsFinite(measurement.NumericalUncertainty) || measurement.NumericalUncertainty < 0)
            return Result(RequirementCheckStatus.Unverifiable, "Measurement uncertainty is invalid.");
        if (measurement.Unit != requirement.Unit || measurement.Unit != query.Unit)
            return Result(RequirementCheckStatus.Failed, "Measurement and source requirement units differ; implicit unit conversion is forbidden at comparison time.");

        if (query.Kind == MeasurementKind.AxisDirection)
        {
            if (measurement.VectorValue is not { } actual || requirement.ExpectedVector is not { } expected || !Finite(actual) || !Finite(expected) || Norm(actual) <= 1e-12 || Norm(expected) <= 1e-12)
                return Result(RequirementCheckStatus.Unverifiable, "Axis-direction comparison requires finite nonzero expected and actual vectors.");
            var cosine = Math.Clamp(Math.Abs(Dot(actual, expected) / (Norm(actual) * Norm(expected))), -1, 1);
            var angle = Math.Acos(cosine) * 180 / Math.PI;
            var status = angle <= requirement.DirectionToleranceDegrees ? RequirementCheckStatus.Passed : RequirementCheckStatus.Failed;
            return Result(status, status == RequirementCheckStatus.Passed ? "Measured axis direction matches the independent source requirement." : "Measured axis direction differs from the independent source requirement.", angle);
        }

        if (requirement.ExpectedScalar is not { } expectedScalar || measurement.ScalarValue is not { } actualScalar ||
            !double.IsFinite(expectedScalar) || !double.IsFinite(actualScalar))
            return Result(RequirementCheckStatus.Unverifiable, "Scalar requirement comparison requires finite expected and actual values.");
        var difference = Math.Abs(actualScalar - expectedScalar);
        // A result whose uncertainty straddles the acceptance threshold is not promoted to pass.
        if (difference - measurement.NumericalUncertainty > requirement.Tolerance)
            return Result(RequirementCheckStatus.Failed, "Measured geometry differs from the independent source requirement.", difference);
        if (difference + measurement.NumericalUncertainty > requirement.Tolerance)
            return Result(RequirementCheckStatus.Unverifiable, "Measurement uncertainty overlaps the source-requirement tolerance boundary.", difference);
        return Result(RequirementCheckStatus.Passed, "Measured geometry satisfies the independent source requirement.", difference);

        RequirementCheck Result(RequirementCheckStatus status, string message, double? difference = null) => new()
        {
            RequirementId = requirement.RequirementId,
            QueryId = query.QueryId,
            SourceFactId = requirement.SourceFactId,
            SourceRevisionId = requirement.SourceRevisionId,
            SourceSha256 = requirement.SourceSha256,
            SourceFactFingerprint = requirement.SourceFactFingerprint,
            RequirementFingerprint = Fingerprint(requirement),
            Status = status,
            ExpectedScalar = requirement.ExpectedScalar,
            ActualScalar = measurement.ScalarValue,
            ExpectedVector = requirement.ExpectedVector,
            ActualVector = measurement.VectorValue,
            Unit = requirement.Unit,
            Difference = difference,
            Message = message,
            Evidence = new Dictionary<string, string>
            {
                ["actual_model_sha256"] = measurement.ActualModelSha256,
                ["model_reopened"] = measurement.ModelReopened.ToString().ToLowerInvariant(),
                ["measurement_method"] = measurement.Method,
                ["reference_status"] = measurement.ReferenceResolution.Status.ToString(),
                ["measurement_status"] = measurement.Status.ToString(),
                ["numerical_uncertainty"] = measurement.NumericalUncertainty.ToString("G12", System.Globalization.CultureInfo.InvariantCulture)
            }
        };
    }

    public static void Validate(MeasurementQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (string.IsNullOrWhiteSpace(query.QueryId) || !double.IsFinite(query.NumericalTolerance) || query.NumericalTolerance <= 0)
            throw new ArgumentException("Measurement query requires an id and a positive finite numerical tolerance.", nameof(query));
        GeometryRefResolver.Validate(query.Geometry);
        if (query.SecondaryGeometry is not null) GeometryRefResolver.Validate(query.SecondaryGeometry);
        if (query.Kind == MeasurementKind.PlaneSeparation && query.SecondaryGeometry is null)
            throw new ArgumentException("Plane separation requires a secondary geometry reference.", nameof(query));
        if (query.Kind != MeasurementKind.PlaneSeparation && query.SecondaryGeometry is not null)
            throw new ArgumentException("Secondary geometry is only valid for plane separation.", nameof(query));
        if (query.Kind == MeasurementKind.AxisDirection && query.Unit != DrawingValueUnit.Unitless)
            throw new ArgumentException("Axis direction is unitless.", nameof(query));
        if (query.Kind != MeasurementKind.AxisDirection && query.Kind != MeasurementKind.FiniteEntityValidity && query.Unit is DrawingValueUnit.Degree or DrawingValueUnit.Unitless)
            throw new ArgumentException("Length measurements require a length unit.", nameof(query));
    }

    public static void Validate(MeasurementRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(requirement);
        if (string.IsNullOrWhiteSpace(requirement.RequirementId) || string.IsNullOrWhiteSpace(requirement.QueryId) ||
            string.IsNullOrWhiteSpace(requirement.SourceFactId) || string.IsNullOrWhiteSpace(requirement.SourceRevisionId) ||
            requirement.SourceSha256 is { Length: > 0 } sourceSha && !IsSha256(sourceSha) ||
            requirement.SourceFactFingerprint is { Length: > 0 } factFingerprint && !IsSha256(factFingerprint) ||
            !double.IsFinite(requirement.Tolerance) || requirement.Tolerance <= 0 ||
            !double.IsFinite(requirement.DirectionToleranceDegrees) || requirement.DirectionToleranceDegrees <= 0 || requirement.DirectionToleranceDegrees >= 90)
            throw new ArgumentException("Measurement requirement contains missing identity or invalid tolerances.", nameof(requirement));
        if (requirement.ExpectedScalar is null == (requirement.ExpectedVector is null))
            throw new ArgumentException("Measurement requirement must contain exactly one scalar or vector expectation.", nameof(requirement));
        if (requirement.ExpectedScalar is { } scalar && !double.IsFinite(scalar) || requirement.ExpectedVector is { } vector && (!Finite(vector) || Norm(vector) <= 1e-12))
            throw new ArgumentException("Measurement requirement expectation is invalid.", nameof(requirement));
    }

    public static string Fingerprint(MeasurementQuery query)
    {
        Validate(query);
        var canonical = string.Join("|", new[]
        {
            query.Contract, query.QueryId, query.Kind.ToString(), query.Unit.ToString(),
            query.NumericalTolerance.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            GeometryRefResolver.Fingerprint(query.Geometry),
            query.SecondaryGeometry is null ? "" : GeometryRefResolver.Fingerprint(query.SecondaryGeometry)
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    public static string Fingerprint(MeasurementRequirement requirement)
    {
        Validate(requirement);
        static string D(double? value) => value?.ToString("R", System.Globalization.CultureInfo.InvariantCulture) ?? "";
        static string V(Vector3? value) => value is null ? "" : string.Join(',', D(value.X), D(value.Y), D(value.Z));
        var canonical = string.Join("|", requirement.RequirementId, requirement.QueryId, requirement.SourceFactId,
            requirement.SourceRevisionId, requirement.SourceSha256?.ToUpperInvariant() ?? "",
            requirement.SourceFactFingerprint?.ToUpperInvariant() ?? "", D(requirement.ExpectedScalar), V(requirement.ExpectedVector),
            requirement.Unit, requirement.Tolerance.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            requirement.DirectionToleranceDegrees.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static RequirementCheckStatus Map(GeometryRefResolutionStatus status) => status switch
    {
        GeometryRefResolutionStatus.Resolved => RequirementCheckStatus.Passed,
        GeometryRefResolutionStatus.Stale => RequirementCheckStatus.Stale,
        GeometryRefResolutionStatus.Ambiguous => RequirementCheckStatus.Ambiguous,
        GeometryRefResolutionStatus.Missing => RequirementCheckStatus.Missing,
        GeometryRefResolutionStatus.WrongDocument => RequirementCheckStatus.WrongDocument,
        _ => RequirementCheckStatus.Unsupported
    };

    private static bool IsSha256(string value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    private static bool Finite(Vector3 value) => double.IsFinite(value.X) && double.IsFinite(value.Y) && double.IsFinite(value.Z);
    private static double Dot(Vector3 a, Vector3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    private static double Norm(Vector3 value) => Math.Sqrt(Dot(value, value));
}

public static class DirectionalMeasurementEngine
{
    public static MeasurementResult Measure(MeasurementQuery query, GeometryRefResolution primary,
        GeometryRefResolution? secondary, string actualModelSha256, bool modelReopened)
    {
        DirectionalMeasurementVerifier.Validate(query);
        ArgumentNullException.ThrowIfNull(primary);
        if (actualModelSha256.Length != 64 || !actualModelSha256.All(Uri.IsHexDigit))
            throw new ArgumentException("Actual measurement model SHA-256 is required.", nameof(actualModelSha256));
        if (!primary.Reference.RefId.Equals(query.Geometry.RefId, StringComparison.Ordinal))
            throw new ArgumentException("Primary resolution does not belong to the measurement query GeometryRef.");
        if (query.SecondaryGeometry is null != (secondary is null))
            throw new ArgumentException("Secondary resolution presence must match the measurement query.");
        if (secondary is not null && !secondary.Reference.RefId.Equals(query.SecondaryGeometry!.RefId, StringComparison.Ordinal))
            throw new ArgumentException("Secondary resolution does not belong to the measurement query secondary GeometryRef.");

        if (primary.Status != GeometryRefResolutionStatus.Resolved || primary.Candidate is null)
            return Result(GeometryMeasurementStatus.Unverifiable, "geometry_ref_resolution", "Primary geometry reference did not resolve.");
        if (secondary is { Status: not GeometryRefResolutionStatus.Resolved } || secondary is { Candidate: null })
            return Result(GeometryMeasurementStatus.Unverifiable, "geometry_ref_resolution", "Secondary geometry reference did not resolve.");
        if (!primary.ResolvedModelSha256.Equals(actualModelSha256, StringComparison.OrdinalIgnoreCase) ||
            secondary is not null && !secondary.ResolvedModelSha256.Equals(actualModelSha256, StringComparison.OrdinalIgnoreCase))
            return Result(GeometryMeasurementStatus.WrongModel, "model_fingerprint", "Resolved geometry and requested measurement model fingerprints differ.");

        var signature = primary.Candidate.Signature;
        return query.Kind switch
        {
            MeasurementKind.CylinderDiameter when signature.GeometryKind is GeometryKind.Cylinder or GeometryKind.Circle && signature.RadiusMm is { } radius =>
                Length(radius * 2, "analytic_radius_from_resolved_brep", "Diameter derived from actual resolved analytic cylinder/circle radius."),
            MeasurementKind.CylinderRadius when signature.GeometryKind is GeometryKind.Cylinder or GeometryKind.Circle && signature.RadiusMm is { } radius =>
                Length(radius, "analytic_radius_from_resolved_brep", "Radius read from actual resolved analytic cylinder/circle."),
            MeasurementKind.AxisPointX when signature.AnchorMm is { } anchor => Length(anchor.X, "resolved_geometry_axis_anchor", "X coordinate of resolved geometry axis/reference anchor."),
            MeasurementKind.AxisPointY when signature.AnchorMm is { } anchor => Length(anchor.Y, "resolved_geometry_axis_anchor", "Y coordinate of resolved geometry axis/reference anchor."),
            MeasurementKind.AxisPointZ when signature.AnchorMm is { } anchor => Length(anchor.Z, "resolved_geometry_axis_anchor", "Z coordinate of resolved geometry axis/reference anchor."),
            MeasurementKind.AxisDirection when signature.Direction is { } direction => Vector(direction, "resolved_analytic_direction", "Direction read from resolved analytic geometry."),
            MeasurementKind.PlaneSeparation => MeasurePlaneSeparation(signature, secondary!.Candidate!.Signature),
            MeasurementKind.FiniteEntityValidity => signature.AreaMm2 is { } area && double.IsFinite(area) && area > 0
                ? Scalar(1, "resolved_trimmed_entity_area", "Resolved entity has finite positive trimmed area evidence.", DrawingValueUnit.Unitless)
                : Result(GeometryMeasurementStatus.Unverifiable, "resolved_trimmed_entity_area", "Finite-entity validity requires positive trimmed-area evidence."),
            _ => Result(GeometryMeasurementStatus.Unsupported, "unsupported_measurement_kind", "Resolved geometry does not expose the analytic data required for this measurement kind.")
        };

        MeasurementResult MeasurePlaneSeparation(GeometrySignature a, GeometrySignature b)
        {
            if (a.GeometryKind != GeometryKind.Plane || b.GeometryKind != GeometryKind.Plane || a.AnchorMm is not { } p || b.AnchorMm is not { } q ||
                a.Direction is not { } na || b.Direction is not { } nb || !Finite(na) || !Finite(nb) || Norm(na) <= 1e-12 || Norm(nb) <= 1e-12)
                return Result(GeometryMeasurementStatus.Unsupported, "analytic_plane_separation", "Plane separation requires two resolved analytic planes with anchors and normals.");
            var ua = Unit(na); var ub = Unit(nb);
            if (Math.Abs(Dot(ua, ub)) < Math.Cos(.25 * Math.PI / 180))
                return Result(GeometryMeasurementStatus.InvalidGeometry, "analytic_plane_separation", "Plane-separation query requires parallel planes.");
            var delta = new Vector3(q.X - p.X, q.Y - p.Y, q.Z - p.Z);
            return Length(Math.Abs(Dot(delta, ua)), "analytic_plane_separation", "Perpendicular distance between resolved analytic planes.");
        }

        MeasurementResult Length(double millimeters, string method, string message)
        {
            var value = query.Unit switch
            {
                DrawingValueUnit.Millimeter => millimeters,
                DrawingValueUnit.Inch => millimeters / 25.4,
                DrawingValueUnit.Meter => millimeters / 1000,
                _ => double.NaN
            };
            return Scalar(value, method, message);
        }

        MeasurementResult Scalar(double value, string method, string message, DrawingValueUnit? unit = null) => new()
        {
            QueryId = query.QueryId,
            QueryFingerprint = DirectionalMeasurementVerifier.Fingerprint(query),
            Kind = query.Kind,
            ReferenceResolution = primary,
            SecondaryReferenceResolution = secondary,
            Status = double.IsFinite(value) ? GeometryMeasurementStatus.Measured : GeometryMeasurementStatus.InvalidGeometry,
            ScalarValue = value,
            Unit = unit ?? query.Unit,
            Method = method,
            NumericalUncertainty = query.NumericalTolerance,
            ActualModelSha256 = actualModelSha256,
            ModelReopened = modelReopened,
            SupportScope = "Resolved analytic B-Rep signature only; no feature-parameter substitution.",
            Message = message
        };

        MeasurementResult Vector(Vector3 value, string method, string message) => new()
        {
            QueryId = query.QueryId,
            QueryFingerprint = DirectionalMeasurementVerifier.Fingerprint(query),
            Kind = query.Kind,
            ReferenceResolution = primary,
            SecondaryReferenceResolution = secondary,
            Status = Finite(value) && Norm(value) > 1e-12 ? GeometryMeasurementStatus.Measured : GeometryMeasurementStatus.InvalidGeometry,
            VectorValue = value,
            Unit = query.Unit,
            Method = method,
            NumericalUncertainty = query.NumericalTolerance,
            ActualModelSha256 = actualModelSha256,
            ModelReopened = modelReopened,
            SupportScope = "Resolved analytic B-Rep signature only; no feature-parameter substitution.",
            Message = message
        };

        MeasurementResult Result(GeometryMeasurementStatus status, string method, string message) => new()
        {
            QueryId = query.QueryId,
            QueryFingerprint = DirectionalMeasurementVerifier.Fingerprint(query),
            Kind = query.Kind,
            ReferenceResolution = primary,
            SecondaryReferenceResolution = secondary,
            Status = status,
            Unit = query.Unit,
            Method = method,
            NumericalUncertainty = query.NumericalTolerance,
            ActualModelSha256 = actualModelSha256,
            ModelReopened = modelReopened,
            SupportScope = "Fail-closed measurement; unsupported or unresolved geometry is never promoted to a value.",
            Message = message
        };
    }

    private static bool Finite(Vector3 value) => double.IsFinite(value.X) && double.IsFinite(value.Y) && double.IsFinite(value.Z);
    private static double Dot(Vector3 a, Vector3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    private static double Norm(Vector3 value) => Math.Sqrt(Dot(value, value));
    private static Vector3 Unit(Vector3 value) { var n = Norm(value); return new(value.X / n, value.Y / n, value.Z / n); }
}
