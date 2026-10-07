using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace CadModeling.Core;

[JsonConverter(typeof(JsonStringEnumConverter<ProjectionPrimitiveKind>))]
public enum ProjectionPrimitiveKind { Line, Circle, Arc }

[JsonConverter(typeof(JsonStringEnumConverter<ProjectionDifferenceKind>))]
public enum ProjectionDifferenceKind
{
    Missing,
    Extra,
    PositionMismatch,
    SizeMismatch,
    ParameterMismatch,
    MirrorDetected,
    Unsupported
}

[JsonConverter(typeof(JsonStringEnumConverter<ProjectionRequirementStatus>))]
public enum ProjectionRequirementStatus { Passed, Failed, Unverifiable }

public readonly record struct ProjectionPointMm(double X, double Y)
{
    public bool IsFinite => double.IsFinite(X) && double.IsFinite(Y);
}

public sealed record ProjectionPrimitive
{
    public required string Id { get; init; }
    public ProjectionPrimitiveKind Kind { get; init; }
    public ProjectionPointMm Start { get; init; }
    public ProjectionPointMm End { get; init; }
    public ProjectionPointMm Center { get; init; }
    public double RadiusMm { get; init; }
    public double SweepDegrees { get; init; }
    public string? RequirementId { get; init; }
    public string? SourceFactId { get; init; }
    public string? SourceFactFingerprint { get; init; }
    public string? RequirementFingerprint { get; init; }
    public bool Required { get; init; } = true;
    public string LineStyle { get; init; } = "visible";
}

public sealed record ProjectionSnapshot
{
    public required string ViewId { get; init; }
    public required string CoordinateFrameId { get; init; }
    public required string SourceSha256 { get; init; }
    public string? SourceRevisionId { get; init; }
    public string? NativeModelPath { get; init; }
    public string? NativeModelSha256 { get; init; }
    public bool NativeModelReopened { get; init; }
    public bool CaptureComplete { get; init; } = true;
    public IReadOnlyList<string> CaptureLimitations { get; init; } = [];
    public IReadOnlyList<ProjectionPrimitive> Primitives { get; init; } = [];
}

public sealed record ProjectionComparisonOptions
{
    public double PositionToleranceMm { get; init; } = 0.15;
    public double RadiusToleranceMm { get; init; } = 0.10;
    public double LineLengthToleranceMm { get; init; } = 0.15;
    public double ArcSweepToleranceDegrees { get; init; } = 0.5;
    public bool CompareVisibleOnly { get; init; } = true;
    public bool DiagnoseMirror { get; init; } = true;
    /// <summary>Fixed source-coordinate mirror axis used only for diagnosis, never to accept the model.</summary>
    public double MirrorAxisXmm { get; init; }
}

public sealed record ProjectionDifference
{
    public required string DifferenceId { get; init; }
    public ProjectionDifferenceKind Kind { get; init; }
    public string? SourcePrimitiveId { get; init; }
    public string? ModelPrimitiveId { get; init; }
    public string? RequirementId { get; init; }
    public double? PositionErrorMm { get; init; }
    public double? SizeErrorMm { get; init; }
    public double? ParameterErrorDegrees { get; init; }
    public string Message { get; init; } = string.Empty;
}

public sealed record ProjectionRequirementResult
{
    public required string RequirementId { get; init; }
    public string? SourceFactId { get; init; }
    public string? SourceFactFingerprint { get; init; }
    public string? RequirementFingerprint { get; init; }
    public ProjectionRequirementStatus Status { get; init; }
    public IReadOnlyList<string> DifferenceIds { get; init; } = [];
}

public sealed record ProjectionReport
{
    public const string ContractVersion = "2.0.0";
    public string Contract { get; init; } = ContractVersion;
    public required string SourceViewId { get; init; }
    public required string SourceSha256 { get; init; }
    public string? SourceRevisionId { get; init; }
    public required string ActualModelSha256 { get; init; }
    public string CoordinateFrameId { get; init; } = string.Empty;
    public bool ModelReopened { get; init; }
    public bool Passed { get; init; }
    public bool MirrorSignatureDetected { get; init; }
    public int RequiredPrimitiveCount { get; init; }
    public int CheckedRequiredPrimitiveCount { get; init; }
    public IReadOnlyList<ProjectionDifference> Differences { get; init; } = [];
    public IReadOnlyList<ProjectionRequirementResult> Requirements { get; init; } = [];
    public IReadOnlyList<string> CaptureLimitations { get; init; } = [];
    public IReadOnlyList<string> SupportedGeometry { get; init; } = ["visible_line", "hidden_line", "center_line_classification", "circle", "arc"];
    public string Scope { get; init; } = "正交线/圆/弧在固定毫米坐标系下的投影；保留可见线/隐藏线/中心线类；无自由缩放、反射、任意曲线或剖面接受。";
}

public static class ProjectionVerifier
{
    public static ProjectionReport Compare(ProjectionSnapshot source, ProjectionSnapshot model, ProjectionComparisonOptions? options = null)
    {
        using var timing = CadModeling.Ir.PerformanceTrace.Begin("review.projection");
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(model);
        options ??= new();
        ValidateOptions(options);
        Validate(source, nameof(source));
        Validate(model, nameof(model));
        if (!source.ViewId.Equals(model.ViewId, StringComparison.Ordinal))
            throw new ArgumentException("源和模型快照必须描述相同的已锁定视图。");
        if (!source.CoordinateFrameId.Equals(model.CoordinateFrameId, StringComparison.Ordinal))
            throw new ArgumentException("投影比较需要一个已经校准的毫米坐标框架；验证器不会对框架进行对齐或缩放。");
        if (!source.SourceSha256.Equals(model.SourceSha256, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("源图和模型投影快照必须携带相同的独立源图SHA-256。");
        if (string.IsNullOrWhiteSpace(model.NativeModelSha256) || !IsSha256(model.NativeModelSha256))
            throw new ArgumentException("模型投影必须携带实际保存的原生模型SHA-256。", nameof(model));

        var result = CompareCore(source.Primitives, model.Primitives, options, includeExtras: true);
        var requiredPrimitives = source.Primitives.Where(item => item.Required).ToArray();
        var checkedRequiredPrimitives = requiredPrimitives.Where(item => Included(item, options)).ToArray();
        var skippedRequired = requiredPrimitives.Where(item => !Included(item, options)).ToArray();
        foreach (var skipped in skippedRequired)
        {
            result.Differences.Add(new()
            {
                DifferenceId = NextId(result.Differences.Count),
                Kind = ProjectionDifferenceKind.Unsupported,
                SourcePrimitiveId = skipped.Id,
                RequirementId = skipped.RequirementId,
                Message = $"需要的原始 '{skipped.Id}' 与线样式 '{skipped.LineStyle}' 处于配置的验证范围之外。"
            });
        }
        if (requiredPrimitives.Length == 0)
        {
            result.Differences.Add(new()
            {
                DifferenceId = NextId(result.Differences.Count),
                Kind = ProjectionDifferenceKind.Unsupported,
                Message = "投影验证没有必需的源基本体；空的必需范围无法通过。"
            });
        }
        if (!model.CaptureComplete)
        {
            foreach (var limitation in model.CaptureLimitations.DefaultIfEmpty("模型投影捕捉不完整。"))
            {
                result.Differences.Add(new()
                {
                    DifferenceId = NextId(result.Differences.Count),
                    Kind = ProjectionDifferenceKind.Unsupported,
                    Message = limitation
                });
            }
        }
        if (!source.CaptureComplete)
        {
            foreach (var limitation in source.CaptureLimitations.DefaultIfEmpty("源投影捕捉不完整。"))
            {
                result.Differences.Add(new()
                {
                    DifferenceId = NextId(result.Differences.Count),
                    Kind = ProjectionDifferenceKind.Unsupported,
                    Message = limitation
                });
            }
        }
        var mirrorSignature = false;
        if (options.DiagnoseMirror && result.Differences.Any(item => item.Kind != ProjectionDifferenceKind.Extra))
        {
            var mirrored = model.Primitives.Select(item => MirrorX(item, options.MirrorAxisXmm)).ToArray();
            var mirrorResult = CompareCore(source.Primitives, mirrored, options with { DiagnoseMirror = false }, includeExtras: true);
            // A mirror signature is diagnostic only when reflection materially resolves all required source failures.
            if (mirrorResult.RequiredFailureCount == 0 && result.RequiredFailureCount > 0)
            {
                mirrorSignature = true;
                result.Differences.Add(new()
                {
                    DifferenceId = NextId(result.Differences.Count),
                    Kind = ProjectionDifferenceKind.MirrorDetected,
                    Message = "关于已锁定的源 X 轴反射模型将匹配所需的原生特征。反射被禁止，原始模型仍然失败。"
                });
            }
        }

        var requirements = requiredPrimitives.Where(item => !string.IsNullOrWhiteSpace(item.RequirementId))
            .Select(item => item.RequirementId!).Distinct(StringComparer.Ordinal)
            .Select(requirement =>
            {
                var requirementPrimitives = requiredPrimitives.Where(item => item.RequirementId == requirement).ToArray();
                var sourceFactIds = requirementPrimitives.Select(item => item.SourceFactId).Where(item => !string.IsNullOrWhiteSpace(item)).Distinct(StringComparer.Ordinal).ToArray();
                var sourceFactFingerprints = requirementPrimitives.Select(item => item.SourceFactFingerprint).Where(item => !string.IsNullOrWhiteSpace(item)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                var requirementFingerprints = requirementPrimitives.Select(item => item.RequirementFingerprint).Where(item => !string.IsNullOrWhiteSpace(item)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                var diffs = result.Differences.Where(item => item.RequirementId == requirement && item.Kind is not ProjectionDifferenceKind.Extra).ToArray();
                var status = diffs.Any(item => item.Kind == ProjectionDifferenceKind.Unsupported)
                    ? ProjectionRequirementStatus.Unverifiable
                    : diffs.Length == 0 ? ProjectionRequirementStatus.Passed : ProjectionRequirementStatus.Failed;
                return new ProjectionRequirementResult
                {
                    RequirementId = requirement,
                    SourceFactId = sourceFactIds.Length == 1 ? sourceFactIds[0] : null,
                    SourceFactFingerprint = sourceFactFingerprints.Length == 1 ? sourceFactFingerprints[0] : null,
                    RequirementFingerprint = requirementFingerprints.Length == 1 ? requirementFingerprints[0] : null,
                    Status = status,
                    DifferenceIds = diffs.Select(item => item.DifferenceId).ToArray()
                };
            }).ToArray();
        var requiredUnidentifiedFailures = result.Differences.Any(diff => diff.Kind is not (ProjectionDifferenceKind.Extra or ProjectionDifferenceKind.MirrorDetected) &&
            diff.SourcePrimitiveId is { } id && source.Primitives.Any(item => item.Id == id && item.Required && string.IsNullOrWhiteSpace(item.RequirementId)));
        var hasUnexpectedGeometry = result.Differences.Any(item => item.Kind == ProjectionDifferenceKind.Extra);
        var passed = requiredPrimitives.Length > 0 && checkedRequiredPrimitives.Length > 0 && skippedRequired.Length == 0 &&
            source.CaptureComplete && model.CaptureComplete && model.NativeModelReopened && !mirrorSignature && !requiredUnidentifiedFailures && !hasUnexpectedGeometry &&
            requirements.All(item => item.Status == ProjectionRequirementStatus.Passed);
        return new()
        {
            SourceViewId = source.ViewId,
            SourceSha256 = source.SourceSha256,
            SourceRevisionId = source.SourceRevisionId,
            ActualModelSha256 = model.NativeModelSha256!,
            CoordinateFrameId = source.CoordinateFrameId,
            ModelReopened = model.NativeModelReopened,
            Passed = passed,
            MirrorSignatureDetected = mirrorSignature,
            RequiredPrimitiveCount = requiredPrimitives.Length,
            CheckedRequiredPrimitiveCount = checkedRequiredPrimitives.Length,
            Differences = result.Differences,
            Requirements = requirements,
            CaptureLimitations = source.CaptureLimitations.Select(item => "源：" + item)
                .Concat(model.CaptureLimitations.Select(item => "模型：" + item)).ToArray()
        };
    }

    private sealed record CoreResult(List<ProjectionDifference> Differences, int RequiredFailureCount);

    private static CoreResult CompareCore(IReadOnlyList<ProjectionPrimitive> source, IReadOnlyList<ProjectionPrimitive> model,
        ProjectionComparisonOptions options, bool includeExtras)
    {
        var differences = new List<ProjectionDifference>();
        var used = new HashSet<int>();
        var requiredFailures = 0;
        foreach (var expected in source.Where(item => Included(item, options)))
        {
            var compatible = model.Select((item, index) => (item, index))
                .Where(pair => !used.Contains(pair.index) && pair.item.Kind == expected.Kind &&
                    Included(pair.item, options) && SameLineStyle(expected, pair.item))
                .Select(pair => (pair.item, pair.index, error: Error(expected, pair.item)))
                .OrderBy(pair => pair.error.Position)
                .ThenBy(pair => pair.error.Size)
                .ThenBy(pair => pair.error.ParameterDegrees)
                .ToArray();
            if (compatible.Length == 0)
            {
                Add(ProjectionDifferenceKind.Missing, expected, null, null, null, "所需的投影原生对象缺失。");
                continue;
            }
            var best = compatible[0];
            var positionOk = best.error.Position <= options.PositionToleranceMm;
            var sizeTolerance = expected.Kind is ProjectionPrimitiveKind.Circle or ProjectionPrimitiveKind.Arc ? options.RadiusToleranceMm : options.LineLengthToleranceMm;
            var sizeOk = best.error.Size <= sizeTolerance;
            var parameterOk = expected.Kind != ProjectionPrimitiveKind.Arc || best.error.ParameterDegrees <= options.ArcSweepToleranceDegrees;
            // Consume a target only when it is the nearest plausible primitive. This preserves a shifted hole as
            // position mismatch instead of reporting an unrelated extra plus missing pair.
            used.Add(best.index);
            if (!positionOk) Add(ProjectionDifferenceKind.PositionMismatch, expected, best.item, best.error.Position, best.error.Size,
                $"投影原始位置相差{best.error.Position:0.###}mm。");
            else if (!sizeOk) Add(ProjectionDifferenceKind.SizeMismatch, expected, best.item, best.error.Position, best.error.Size,
                $"投影原始的尺寸相差{best.error.Size:0.###}mm。");
            else if (!parameterOk) Add(ProjectionDifferenceKind.ParameterMismatch, expected, best.item, best.error.Position, best.error.Size,
                $"弧投影扫掠差异为{best.error.ParameterDegrees:0.###}度。", best.error.ParameterDegrees);
        }
        if (includeExtras)
        {
            foreach (var pair in model.Select((item, index) => (item, index)).Where(pair => !used.Contains(pair.index) &&
                         Included(pair.item, options)))
            {
                differences.Add(new()
                {
                    DifferenceId = NextId(differences.Count),
                    Kind = ProjectionDifferenceKind.Extra,
                    ModelPrimitiveId = pair.item.Id,
                    Message = "模型投影包含一个未匹配的额外基本体。"
                });
            }
        }
        return new(differences, requiredFailures);

        void Add(ProjectionDifferenceKind kind, ProjectionPrimitive expected, ProjectionPrimitive? actual, double? position, double? size, string message,
            double? parameterDegrees = null)
        {
            differences.Add(new()
            {
                DifferenceId = NextId(differences.Count),
                Kind = kind,
                SourcePrimitiveId = expected.Id,
                ModelPrimitiveId = actual?.Id,
                RequirementId = expected.RequirementId,
                PositionErrorMm = position,
                SizeErrorMm = size,
                ParameterErrorDegrees = parameterDegrees,
                Message = message
            });
            if (expected.Required) requiredFailures++;
        }
    }

    private static (double Position, double Size, double ParameterDegrees) Error(ProjectionPrimitive expected, ProjectionPrimitive actual)
    {
        if (expected.Kind == ProjectionPrimitiveKind.Circle)
            return (Distance(expected.Center, actual.Center), Math.Abs(expected.RadiusMm - actual.RadiusMm), 0);
        if (expected.Kind == ProjectionPrimitiveKind.Arc)
        {
            var arcPosition = Math.Max(Distance(expected.Center, actual.Center),
                Math.Max(Distance(expected.Start, actual.Start), Distance(expected.End, actual.End)));
            return (arcPosition, Math.Abs(expected.RadiusMm - actual.RadiusMm), Math.Abs(expected.SweepDegrees - actual.SweepDegrees));
        }
        var direct = Math.Max(Distance(expected.Start, actual.Start), Distance(expected.End, actual.End));
        var reversed = Math.Max(Distance(expected.Start, actual.End), Distance(expected.End, actual.Start));
        var position = Math.Min(direct, reversed);
        var expectedLength = Distance(expected.Start, expected.End);
        var actualLength = Distance(actual.Start, actual.End);
        return (position, Math.Abs(expectedLength - actualLength), 0);
    }

    private static ProjectionPrimitive MirrorX(ProjectionPrimitive primitive, double axis) => primitive with
    {
        Start = Mirror(primitive.Start, axis),
        End = Mirror(primitive.End, axis),
        Center = Mirror(primitive.Center, axis),
        SweepDegrees = primitive.Kind == ProjectionPrimitiveKind.Arc ? -primitive.SweepDegrees : primitive.SweepDegrees,
        Id = primitive.Id + "-mirror-diagnostic"
    };

    private static ProjectionPointMm Mirror(ProjectionPointMm point, double axis) => new(2 * axis - point.X, point.Y);
    private static double Distance(ProjectionPointMm a, ProjectionPointMm b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
    private static string NextId(int index) => $"projection-diff-{index + 1:D4}";
    private static bool IsSha256(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);
    private static bool Included(ProjectionPrimitive primitive, ProjectionComparisonOptions options) =>
        !options.CompareVisibleOnly || primitive.LineStyle.Equals("visible", StringComparison.OrdinalIgnoreCase);

    private static void ValidateOptions(ProjectionComparisonOptions options)
    {
        static void Tolerance(double value, string name)
        {
            if (!double.IsFinite(value) || value < 0) throw new ArgumentOutOfRangeException(name, "投影公差必须是有限且非负的。");
        }
        Tolerance(options.PositionToleranceMm, nameof(options.PositionToleranceMm));
        Tolerance(options.RadiusToleranceMm, nameof(options.RadiusToleranceMm));
        Tolerance(options.LineLengthToleranceMm, nameof(options.LineLengthToleranceMm));
        Tolerance(options.ArcSweepToleranceDegrees, nameof(options.ArcSweepToleranceDegrees));
        if (options.DiagnoseMirror && !double.IsFinite(options.MirrorAxisXmm))
            throw new ArgumentOutOfRangeException(nameof(options.MirrorAxisXmm), "对镜像诊断轴必须是有限的。");
    }

    private static void Validate(ProjectionSnapshot snapshot, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(snapshot.ViewId) || string.IsNullOrWhiteSpace(snapshot.CoordinateFrameId) || !IsSha256(snapshot.SourceSha256))
            throw new ArgumentException("截面快照需要视图、坐标框架和SHA-256源指纹。", parameterName);
        if (snapshot.Primitives.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != snapshot.Primitives.Count)
            throw new ArgumentException("投影原语的ID必须唯一。", parameterName);
        foreach (var primitive in snapshot.Primitives)
        {
            if (string.IsNullOrWhiteSpace(primitive.Id)) throw new ArgumentException("投影原语ID是必需的。", parameterName);
            if (primitive.LineStyle is not ("visible" or "hidden" or "center"))
                throw new ArgumentException($"基本体 '{primitive.Id}' 不支持线条样式 '{primitive.LineStyle}'.", parameterName);
            if (primitive.Kind == ProjectionPrimitiveKind.Line && (!primitive.Start.IsFinite || !primitive.End.IsFinite || Distance(primitive.Start, primitive.End) <= 1e-12))
                throw new ArgumentException($"直线 '{primitive.Id}' 无效。", parameterName);
            if (primitive.Kind == ProjectionPrimitiveKind.Circle && (!primitive.Center.IsFinite || !double.IsFinite(primitive.RadiusMm) || primitive.RadiusMm <= 0))
                throw new ArgumentException($"圆 '{primitive.Id}' 无效。", parameterName);
            if (primitive.Kind == ProjectionPrimitiveKind.Arc && (!primitive.Center.IsFinite || !primitive.Start.IsFinite || !primitive.End.IsFinite ||
                !double.IsFinite(primitive.RadiusMm) || primitive.RadiusMm <= 0 || !double.IsFinite(primitive.SweepDegrees) ||
                Math.Abs(primitive.SweepDegrees) <= 1e-9 || Math.Abs(primitive.SweepDegrees) >= 360 - 1e-7 ||
                Math.Abs(Distance(primitive.Center, primitive.Start) - primitive.RadiusMm) > 1e-4 ||
                Math.Abs(Distance(primitive.Center, primitive.End) - primitive.RadiusMm) > 1e-4 || !ArcSweepConsistent(primitive)))
                throw new ArgumentException($"圆弧{primitive.Id}无效。", parameterName);
        }
    }

    private static bool ArcSweepConsistent(ProjectionPrimitive primitive)
    {
        static double Angle(ProjectionPointMm center, ProjectionPointMm point) => Math.Atan2(point.Y - center.Y, point.X - center.X) * 180 / Math.PI;
        static double Ccw(double from, double to)
        {
            var delta = (to - from) % 360;
            return delta < 0 ? delta + 360 : delta;
        }
        var start = Angle(primitive.Center, primitive.Start);
        var end = Angle(primitive.Center, primitive.End);
        var ccw = Ccw(start, end);
        var expected = primitive.SweepDegrees > 0 ? ccw : ccw - 360;
        return Math.Abs(expected - primitive.SweepDegrees) <= 1e-4;
    }

    private static bool SameLineStyle(ProjectionPrimitive expected, ProjectionPrimitive actual) =>
        expected.LineStyle.Equals(actual.LineStyle, StringComparison.OrdinalIgnoreCase);
}

public static class ProjectionFingerprints
{
    public static string FileSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
