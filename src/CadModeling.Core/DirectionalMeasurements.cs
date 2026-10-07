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
            throw new ArgumentException("测量查询的结果ID和源ID必须一致。");
        if (!measurement.QueryFingerprint.Equals(Fingerprint(query), StringComparison.OrdinalIgnoreCase) || measurement.Kind != query.Kind)
            return Result(RequirementCheckStatus.Stale, "测量结果不属于当前完整的查询指纹/类型。");
        if (!measurement.ReferenceResolution.Reference.RefId.Equals(query.Geometry.RefId, StringComparison.Ordinal))
            throw new ArgumentException("测量结果针对的是一个不同的GeometryRef。");
        if (!GeometryRefResolver.Fingerprint(measurement.ReferenceResolution.Reference).Equals(GeometryRefResolver.Fingerprint(query.Geometry), StringComparison.OrdinalIgnoreCase))
            return Result(RequirementCheckStatus.Stale, "主标识在测量产生后发生了GeometryRef更改。");
        if (query.SecondaryGeometry is null != (measurement.SecondaryReferenceResolution is null))
            return Result(RequirementCheckStatus.Unverifiable, "测量结果不包含当前查询所需的secondary GeometryRef分辨率要求。");
        if (query.SecondaryGeometry is not null && measurement.SecondaryReferenceResolution is { } measuredSecondary &&
            !GeometryRefResolver.Fingerprint(measuredSecondary.Reference).Equals(GeometryRefResolver.Fingerprint(query.SecondaryGeometry), StringComparison.OrdinalIgnoreCase))
            return Result(RequirementCheckStatus.Stale, "Secondary GeometryRef 身份在测量产生后更改。");
        if (query.Geometry.SourceRevisionId is { Length: > 0 } sourceRevision &&
            !sourceRevision.Equals(requirement.SourceRevisionId, StringComparison.Ordinal))
            return Result(RequirementCheckStatus.Stale, "几何参考和源属于不同的源修订版本。");
        if (query.Geometry.SourceFactIds.Count > 0 && !query.Geometry.SourceFactIds.Contains(requirement.SourceFactId, StringComparer.Ordinal))
            return Result(RequirementCheckStatus.Stale, "几何参考未绑定到此要求使用的源事实。");

        var refStatus = Map(measurement.ReferenceResolution.Status);
        if (refStatus is not RequirementCheckStatus.Passed)
            return Result(refStatus, "几何参考无法解析；源要求未被比较。");
        if (measurement.SecondaryReferenceResolution is { } secondary && Map(secondary.Status) is { } secondaryStatus && secondaryStatus is not RequirementCheckStatus.Passed)
            return Result(secondaryStatus, "二次几何参考无法解析；源要求未被比较。");
        if (!measurement.ActualModelSha256.Equals(measurement.ReferenceResolution.ResolvedModelSha256, StringComparison.OrdinalIgnoreCase))
            return Result(RequirementCheckStatus.Stale, "测量模型指纹与进行了GeometryRef分辨率的模型不同。");
        if (measurement.SecondaryReferenceResolution is { } secondaryResolution &&
            !measurement.ActualModelSha256.Equals(secondaryResolution.ResolvedModelSha256, StringComparison.OrdinalIgnoreCase))
            return Result(RequirementCheckStatus.Stale, "Secondary GeometryRef 被解析为与测量结果对应的不同的模型。");
        if (!measurement.ModelReopened)
            return Result(RequirementCheckStatus.Unverifiable, "实际几何并未从已保存并重新打开的模型中测量。");
        if (measurement.Status != GeometryMeasurementStatus.Measured)
            return Result(measurement.Status == GeometryMeasurementStatus.Unsupported ? RequirementCheckStatus.Unsupported : RequirementCheckStatus.Unverifiable,
                measurement.Message.Length == 0 ? "实际几何测量无效。" : measurement.Message);
        if (!double.IsFinite(measurement.NumericalUncertainty) || measurement.NumericalUncertainty < 0)
            return Result(RequirementCheckStatus.Unverifiable, "测量不确定度无效。");
        if (measurement.Unit != requirement.Unit || measurement.Unit != query.Unit)
            return Result(RequirementCheckStatus.Failed, "测量单位和源要求单位不同；比较时禁止隐式单位转换。");

        if (query.Kind == MeasurementKind.AxisDirection)
        {
            if (measurement.VectorValue is not { } actual || requirement.ExpectedVector is not { } expected || !Finite(actual) || !Finite(expected) || Norm(actual) <= 1e-12 || Norm(expected) <= 1e-12)
                return Result(RequirementCheckStatus.Unverifiable, "轴方向的比较需要有限且非零的预期向量和实际向量。");
            var cosine = Math.Clamp(Math.Abs(Dot(actual, expected) / (Norm(actual) * Norm(expected))), -1, 1);
            var angle = Math.Acos(cosine) * 180 / Math.PI;
            var status = angle <= requirement.DirectionToleranceDegrees ? RequirementCheckStatus.Passed : RequirementCheckStatus.Failed;
            return Result(status, status == RequirementCheckStatus.Passed ? "测量轴方向符合独立源要求。" : "测量轴的方向与独立源的要求不同。", angle);
        }

        if (requirement.ExpectedScalar is not { } expectedScalar || measurement.ScalarValue is not { } actualScalar ||
            !double.IsFinite(expectedScalar) || !double.IsFinite(actualScalar))
            return Result(RequirementCheckStatus.Unverifiable, "标量要求比较需要有限的预期值和实际值。");
        var difference = Math.Abs(actualScalar - expectedScalar);
        // A result whose uncertainty straddles the acceptance threshold is not promoted to pass.
        if (difference - measurement.NumericalUncertainty > requirement.Tolerance)
            return Result(RequirementCheckStatus.Failed, "测量几何体与独立源要求不同。", difference);
        if (difference + measurement.NumericalUncertainty > requirement.Tolerance)
            return Result(RequirementCheckStatus.Unverifiable, "测量不确定度覆盖源要求的公差边界。", difference);
        return Result(RequirementCheckStatus.Passed, "测量几何满足独立源要求。", difference);

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
            throw new ArgumentException("测量查询需要一个 id 和一个正数且有限的数值容差。", nameof(query));
        GeometryRefResolver.Validate(query.Geometry);
        if (query.SecondaryGeometry is not null) GeometryRefResolver.Validate(query.SecondaryGeometry);
        if (query.Kind == MeasurementKind.PlaneSeparation && query.SecondaryGeometry is null)
            throw new ArgumentException("平面分离需要一个次级几何参考。", nameof(query));
        if (query.Kind != MeasurementKind.PlaneSeparation && query.SecondaryGeometry is not null)
            throw new ArgumentException("二次几何仅适用于平面分离。", nameof(query));
        if (query.Kind == MeasurementKind.AxisDirection && query.Unit != DrawingValueUnit.Unitless)
            throw new ArgumentException("轴的方向是无单位的。", nameof(query));
        if (query.Kind != MeasurementKind.AxisDirection && query.Kind != MeasurementKind.FiniteEntityValidity && query.Unit is DrawingValueUnit.Degree or DrawingValueUnit.Unitless)
            throw new ArgumentException("长度测量需要长度单位。", nameof(query));
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
            throw new ArgumentException("测量要求缺少身份标识或包含无效的公差。", nameof(requirement));
        if (requirement.ExpectedScalar is null == (requirement.ExpectedVector is null))
            throw new ArgumentException("测量要求必须包含一个且仅包含一个标量或向量的期望值。", nameof(requirement));
        if (requirement.ExpectedScalar is { } scalar && !double.IsFinite(scalar) || requirement.ExpectedVector is { } vector && (!Finite(vector) || Norm(vector) <= 1e-12))
            throw new ArgumentException("测量要求预期无效。", nameof(requirement));
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
            throw new ArgumentException("实际测量模型 SHA-256 需要。", nameof(actualModelSha256));
        if (!primary.Reference.RefId.Equals(query.Geometry.RefId, StringComparison.Ordinal))
            throw new ArgumentException("主分辨率不属于测量查询 GeometryRef。");
        if (query.SecondaryGeometry is null != (secondary is null))
            throw new ArgumentException("二次分辨率的存在必须与测量查询匹配。");
        if (secondary is not null && !secondary.Reference.RefId.Equals(query.SecondaryGeometry!.RefId, StringComparison.Ordinal))
            throw new ArgumentException("二级分辨率不属于二次测量查询的二级 GeometryRef。");

        if (primary.Status != GeometryRefResolutionStatus.Resolved || primary.Candidate is null)
            return Result(GeometryMeasurementStatus.Unverifiable, "geometry_ref_resolution", "主要几何参考无法解析。");
        if (secondary is { Status: not GeometryRefResolutionStatus.Resolved } || secondary is { Candidate: null })
            return Result(GeometryMeasurementStatus.Unverifiable, "geometry_ref_resolution", "二次几何参考未解析。");
        if (!primary.ResolvedModelSha256.Equals(actualModelSha256, StringComparison.OrdinalIgnoreCase) ||
            secondary is not null && !secondary.ResolvedModelSha256.Equals(actualModelSha256, StringComparison.OrdinalIgnoreCase))
            return Result(GeometryMeasurementStatus.WrongModel, "model_fingerprint", "已解决的几何和请求的测量模型指纹不同。");

        var signature = primary.Candidate.Signature;
        return query.Kind switch
        {
            MeasurementKind.CylinderDiameter when signature.GeometryKind is GeometryKind.Cylinder or GeometryKind.Circle && signature.RadiusMm is { } radius =>
                Length(radius * 2, "analytic_radius_from_resolved_brep", "直径由实际解析圆柱/圆的半径推导得出。"),
            MeasurementKind.CylinderRadius when signature.GeometryKind is GeometryKind.Cylinder or GeometryKind.Circle && signature.RadiusMm is { } radius =>
                Length(radius, "analytic_radius_from_resolved_brep", "从实际解析圆柱/圆上读取的半径。"),
            MeasurementKind.AxisPointX when signature.AnchorMm is { } anchor => Length(anchor.X, "resolved_geometry_axis_anchor", "X轴上已解决几何轴/参考锚点的坐标。"),
            MeasurementKind.AxisPointY when signature.AnchorMm is { } anchor => Length(anchor.Y, "resolved_geometry_axis_anchor", "Y轴方向上已解决几何轴/参考锚点的坐标。"),
            MeasurementKind.AxisPointZ when signature.AnchorMm is { } anchor => Length(anchor.Z, "resolved_geometry_axis_anchor", "Z轴上已解决几何轴/参考锚点的坐标值。"),
            MeasurementKind.AxisDirection when signature.Direction is { } direction => Vector(direction, "resolved_analytic_direction", "从解析几何解算方向读取。"),
            MeasurementKind.PlaneSeparation => MeasurePlaneSeparation(signature, secondary!.Candidate!.Signature),
            MeasurementKind.FiniteEntityValidity => signature.AreaMm2 is { } area && double.IsFinite(area) && area > 0
                ? Scalar(1, "resolved_trimmed_entity_area", "已解决实体有有限正剪裁面积的证据。", DrawingValueUnit.Unitless)
                : Result(GeometryMeasurementStatus.Unverifiable, "resolved_trimmed_entity_area", "有限实体的有效性需要正数剪裁面积的证据。"),
            _ => Result(GeometryMeasurementStatus.Unsupported, "unsupported_measurement_kind", "解析几何无法提供此测量类型所需的分析数据。")
        };

        MeasurementResult MeasurePlaneSeparation(GeometrySignature a, GeometrySignature b)
        {
            if (a.GeometryKind != GeometryKind.Plane || b.GeometryKind != GeometryKind.Plane || a.AnchorMm is not { } p || b.AnchorMm is not { } q ||
                a.Direction is not { } na || b.Direction is not { } nb || !Finite(na) || !Finite(nb) || Norm(na) <= 1e-12 || Norm(nb) <= 1e-12)
                return Result(GeometryMeasurementStatus.Unsupported, "analytic_plane_separation", "平面分离需要两个具有锚点和法线的解析平面。");
            var ua = Unit(na); var ub = Unit(nb);
            if (Math.Abs(Dot(ua, ub)) < Math.Cos(.25 * Math.PI / 180))
                return Result(GeometryMeasurementStatus.InvalidGeometry, "analytic_plane_separation", "分离平面查询需要平行的平面。");
            var delta = new Vector3(q.X - p.X, q.Y - p.Y, q.Z - p.Z);
            return Length(Math.Abs(Dot(delta, ua)), "analytic_plane_separation", "解析平面解析后的垂直距离。");
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
            SupportScope = "仅解析分析 B-Rep 签名；未进行特征参数替换。",
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
            SupportScope = "仅解析分析 B-Rep 签名；未进行特征参数替换。",
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
            SupportScope = "失败闭合测量；未支持或未解决的几何体永远不会被提升为值。",
            Message = message
        };
    }

    private static bool Finite(Vector3 value) => double.IsFinite(value.X) && double.IsFinite(value.Y) && double.IsFinite(value.Z);
    private static double Dot(Vector3 a, Vector3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    private static double Norm(Vector3 value) => Math.Sqrt(Dot(value, value));
    private static Vector3 Unit(Vector3 value) { var n = Norm(value); return new(value.X / n, value.Y / n, value.Z / n); }
}
