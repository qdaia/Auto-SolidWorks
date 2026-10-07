using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using CadModeling.Ir;

namespace CadModeling.Core;

[JsonConverter(typeof(JsonStringEnumConverter<SectionType>))]
public enum SectionType { FullPlane, Stepped, Local, Revolved, Unknown }

[JsonConverter(typeof(JsonStringEnumConverter<SectionStatus>))]
public enum SectionStatus { Passed, Failed, Unverifiable, Unsupported }

public sealed record SectionConnectivityRequirement
{
    public required string RequirementId { get; init; }
    public required string SourceFactId { get; init; }
    public required string SourceRevisionId { get; init; }
    public required string SourceFactFingerprint { get; init; }
    public required string RequirementFingerprint { get; init; }
}

public sealed record SectionSpec
{
    public const string ContractVersion = "1.0.0";
    public string Contract { get; init; } = ContractVersion;
    public required string SectionId { get; init; }
    public required string SourceFactId { get; init; }
    public required string SourceRevisionId { get; init; }
    public IReadOnlyList<string> SourceRegionIds { get; init; } = [];
    public required string SourceSha256 { get; init; }
    public required string ViewMapId { get; init; }
    public required string ViewId { get; init; }
    public required string CoordinateFrameId { get; init; }
    public SectionType Type { get; init; } = SectionType.FullPlane;
    public required Vector3 PlaneOriginMm { get; init; }
    public required Vector3 PlaneNormal { get; init; }
    public required Vector3 ViewingDirection { get; init; }
    public required Vector3 InPlaneXDirection { get; init; }
    public IReadOnlyList<string> RequiredConnectivityRequirementIds { get; init; } = [];
    public IReadOnlyList<SectionConnectivityRequirement> RequiredConnectivity { get; init; } = [];
    public double PlaneToleranceMm { get; init; } = 0.05;
    public double DirectionToleranceDegrees { get; init; } = 0.25;
    public double LoopAreaToleranceMm2 { get; init; } = 0.10;
}

public sealed record SectionLoop
{
    public required string LoopId { get; init; }
    public bool IsVoid { get; init; }
    public IReadOnlyList<string> PrimitiveIds { get; init; } = [];
    public double? AreaMm2 { get; init; }
}

public sealed record SectionSnapshot
{
    public required string SectionId { get; init; }
    public required string SourceSha256 { get; init; }
    public required string ViewId { get; init; }
    public required string CoordinateFrameId { get; init; }
    public SectionType Type { get; init; } = SectionType.FullPlane;
    public required Vector3 PlaneOriginMm { get; init; }
    public required Vector3 PlaneNormal { get; init; }
    public required Vector3 ViewingDirection { get; init; }
    public required Vector3 InPlaneXDirection { get; init; }
    public string? NativeModelSha256 { get; init; }
    public bool ModelReopened { get; init; }
    public bool CaptureComplete { get; init; } = true;
    public IReadOnlyList<string> CaptureLimitations { get; init; } = [];
    public IReadOnlyList<ProjectionPrimitive> BoundaryPrimitives { get; init; } = [];
    public IReadOnlyList<SectionLoop> Loops { get; init; } = [];
}

public sealed record SectionDifference
{
    public required string DifferenceId { get; init; }
    public required string Kind { get; init; }
    public string Message { get; init; } = string.Empty;
}

public sealed record SectionReport
{
    public const string ContractVersion = "1.0.0";
    public string Contract { get; init; } = ContractVersion;
    public required string SectionId { get; init; }
    public required string SourceFactId { get; init; }
    public required string SourceRevisionId { get; init; }
    public required string SourceSha256 { get; init; }
    public required string ActualModelSha256 { get; init; }
    public required string SectionSpecFingerprint { get; init; }
    public SectionStatus Status { get; init; }
    public bool ModelReopened { get; init; }
    public ProjectionReport? BoundaryReport { get; init; }
    public IReadOnlyList<SectionDifference> Differences { get; init; } = [];
    public IReadOnlyList<string> ConnectivityRequirementIds { get; init; } = [];
    public string Scope { get; init; } = "单个完整的平面剖面，固定平面和观察方向；阶梯/局部/旋转剖面不支持。";
}

public static class SectionVerifier
{
    public static SectionReport Compare(SectionSpec spec, SectionSnapshot source, SectionSnapshot model,
        IReadOnlyList<ConnectivityCheck>? connectivityChecks = null, ProjectionComparisonOptions? projectionOptions = null)
    {
        using var timing = CadModeling.Ir.PerformanceTrace.Begin("review.section");
        Validate(spec);
        Validate(source, isModel: false);
        Validate(model, isModel: true);
        var fingerprint = Fingerprint(spec);
        var differences = new List<SectionDifference>();

        if (spec.Type != SectionType.FullPlane || source.Type != SectionType.FullPlane || model.Type != SectionType.FullPlane)
            return Report(SectionStatus.Unsupported, null, "section-type", "仅支持一个完整的平面剖面。");
        if (!spec.SectionId.Equals(source.SectionId, StringComparison.Ordinal) || !spec.SectionId.Equals(model.SectionId, StringComparison.Ordinal))
            throw new ArgumentException("剖面规格/源/模型的剖面ID必须一致。");
        if (!spec.SourceSha256.Equals(source.SourceSha256, StringComparison.OrdinalIgnoreCase) ||
            !spec.SourceSha256.Equals(model.SourceSha256, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("剖面源/模型必须携带与剖面规格相同的独立源 SHA-256。");
        if (!spec.ViewId.Equals(source.ViewId, StringComparison.Ordinal) || !spec.ViewId.Equals(model.ViewId, StringComparison.Ordinal) ||
            !spec.CoordinateFrameId.Equals(source.CoordinateFrameId, StringComparison.Ordinal) ||
            !spec.CoordinateFrameId.Equals(model.CoordinateFrameId, StringComparison.Ordinal))
            throw new ArgumentException("剖面比较需要冻结的源视图和坐标框架；不会进行自由对齐。");
        if (!model.ModelReopened)
            return Report(SectionStatus.Unverifiable, null, "model-not-reopened", "实际剖面未能从保存并重新打开的模型中捕获。");
        if (!source.CaptureComplete || !model.CaptureComplete)
            return Report(SectionStatus.Unverifiable, null, "capture-incomplete",
                string.Join(" ", source.CaptureLimitations.Select(x => "源：" + x).Concat(model.CaptureLimitations.Select(x => "模型：" + x))));

        if (Distance(spec.PlaneOriginMm, source.PlaneOriginMm) > spec.PlaneToleranceMm ||
            Distance(spec.PlaneOriginMm, model.PlaneOriginMm) > spec.PlaneToleranceMm)
            differences.Add(Diff("plane-position", "剖面平面的原点与源定义的固定平面不同；不允许进行平移/对齐。"));
        if (!SameDirected(spec.PlaneNormal, source.PlaneNormal, spec.DirectionToleranceDegrees) ||
            !SameDirected(spec.PlaneNormal, model.PlaneNormal, spec.DirectionToleranceDegrees))
            differences.Add(Diff("plane-normal", "剖面平面的法线不同或反转。"));
        if (!SameDirected(spec.ViewingDirection, source.ViewingDirection, spec.DirectionToleranceDegrees) ||
            !SameDirected(spec.ViewingDirection, model.ViewingDirection, spec.DirectionToleranceDegrees))
            differences.Add(Diff("view-direction", "剖面视图的方向不同或相反。"));
        if (!SameDirected(spec.InPlaneXDirection, source.InPlaneXDirection, spec.DirectionToleranceDegrees) ||
            !SameDirected(spec.InPlaneXDirection, model.InPlaneXDirection, spec.DirectionToleranceDegrees))
            differences.Add(Diff("frame-x-direction", "剖面在平面X方向上不同或反向；不允许自由剖面旋转。"));

        var sourceProjection = Projection(source, nativeModelSha: null, reopened: false);
        var modelProjection = Projection(model, model.NativeModelSha256, model.ModelReopened);
        // Source snapshots are evidence, not native CAD. ProjectionVerifier only requires reopening on the model snapshot.
        var options = projectionOptions ?? new ProjectionComparisonOptions { CompareVisibleOnly = false };
        var boundary = ProjectionVerifier.Compare(sourceProjection, modelProjection, options);
        if (!boundary.Passed)
            differences.Add(Diff("boundary", "剖面边界 图元 匹配不起来，处于已锁定的剖面框架中。"));

        CompareLoops(source, model, options, spec.LoopAreaToleranceMm2, differences);
        CompareConnectivity(spec, connectivityChecks ?? [], model.NativeModelSha256!, differences, out var connectivityUnverifiable);
        if (differences.Count == 0)
            return Report(SectionStatus.Passed, boundary, null, "剖面平面、方向、材料/空洞环和所需的连通性证据匹配。");
        return Report(connectivityUnverifiable || differences.Any(d => d.Kind is "connectivity-missing" or "connectivity-unverifiable")
            ? SectionStatus.Unverifiable : SectionStatus.Failed, boundary);

        SectionReport Report(SectionStatus status, ProjectionReport? boundaryReport, string? kind = null, string? message = null)
        {
            if (kind is not null) differences.Add(Diff(kind, message ?? string.Empty));
            return new()
            {
                SectionId = spec.SectionId,
                SourceFactId = spec.SourceFactId,
                SourceRevisionId = spec.SourceRevisionId,
                SourceSha256 = spec.SourceSha256,
                ActualModelSha256 = model.NativeModelSha256 ?? new string('0', 64),
                SectionSpecFingerprint = fingerprint,
                Status = status,
                ModelReopened = model.ModelReopened,
                BoundaryReport = boundaryReport,
                Differences = differences.ToArray(),
                ConnectivityRequirementIds = spec.RequiredConnectivity.Select(item => item.RequirementId)
                    .Concat(spec.RequiredConnectivityRequirementIds).Distinct(StringComparer.Ordinal).ToArray()
            };
        }
    }

    public static void Validate(SectionSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (string.IsNullOrWhiteSpace(spec.SectionId) || string.IsNullOrWhiteSpace(spec.SourceFactId) || string.IsNullOrWhiteSpace(spec.SourceRevisionId) ||
            string.IsNullOrWhiteSpace(spec.ViewMapId) || string.IsNullOrWhiteSpace(spec.ViewId) || string.IsNullOrWhiteSpace(spec.CoordinateFrameId) || !Sha(spec.SourceSha256))
            throw new ArgumentException("SectionSpec 需要源、视图和坐标框架的身份。", nameof(spec));
        if (!FiniteVector(spec.PlaneOriginMm) || !UnitCapable(spec.PlaneNormal) || !UnitCapable(spec.ViewingDirection) || !UnitCapable(spec.InPlaneXDirection))
            throw new ArgumentException("SectionSpec 需要有限平面的原点、法线、观察方向和平面内的 X 方向。", nameof(spec));
        if (Math.Abs(Dot(Unit(spec.PlaneNormal), Unit(spec.InPlaneXDirection))) > 1e-6)
            throw new ArgumentException("剖面在平面X方向的切线必须位于剖面平面内。", nameof(spec));
        if (!Positive(spec.PlaneToleranceMm) || !Positive(spec.DirectionToleranceDegrees) || spec.DirectionToleranceDegrees >= 90 || !Positive(spec.LoopAreaToleranceMm2))
            throw new ArgumentException("SectionSpec 允差必须是有限且正数。", nameof(spec));
        if (spec.SourceRegionIds.Any(string.IsNullOrWhiteSpace) || spec.SourceRegionIds.Distinct(StringComparer.Ordinal).Count() != spec.SourceRegionIds.Count ||
            spec.RequiredConnectivityRequirementIds.Any(string.IsNullOrWhiteSpace) || spec.RequiredConnectivityRequirementIds.Distinct(StringComparer.Ordinal).Count() != spec.RequiredConnectivityRequirementIds.Count)
            throw new ArgumentException("SectionSpec 源/连接性 ID 必须非空且唯一。", nameof(spec));
        if (spec.RequiredConnectivity.Select(item => item.RequirementId).Distinct(StringComparer.Ordinal).Count() != spec.RequiredConnectivity.Count ||
            spec.RequiredConnectivity.Any(item => string.IsNullOrWhiteSpace(item.RequirementId) || string.IsNullOrWhiteSpace(item.SourceFactId) ||
                string.IsNullOrWhiteSpace(item.SourceRevisionId) || !Sha(item.SourceFactFingerprint) || !Sha(item.RequirementFingerprint)))
            throw new ArgumentException("剖面结构包含T08的依赖项需要具有唯一的ID和冻结的源/要求指纹。", nameof(spec));
    }

    public static string Fingerprint(SectionSpec spec)
    {
        Validate(spec);
        var canonical = string.Join("|", new[]
        {
            spec.Contract, spec.SectionId, spec.SourceFactId, spec.SourceRevisionId, spec.SourceSha256.ToUpperInvariant(), spec.ViewMapId,
            spec.ViewId, spec.CoordinateFrameId, spec.Type.ToString(), Vector(spec.PlaneOriginMm), Vector(spec.PlaneNormal), Vector(spec.ViewingDirection), Vector(spec.InPlaneXDirection),
            spec.PlaneToleranceMm.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            spec.DirectionToleranceDegrees.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            spec.LoopAreaToleranceMm2.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            string.Join(",", spec.SourceRegionIds.Order(StringComparer.Ordinal)),
            string.Join(",", spec.RequiredConnectivityRequirementIds.Order(StringComparer.Ordinal)),
            string.Join(";", spec.RequiredConnectivity.OrderBy(item => item.RequirementId, StringComparer.Ordinal)
                .Select(item => string.Join(",", item.RequirementId, item.SourceFactId, item.SourceRevisionId,
                    item.SourceFactFingerprint.ToUpperInvariant(), item.RequirementFingerprint.ToUpperInvariant())))
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static void Validate(SectionSnapshot snapshot, bool isModel)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (string.IsNullOrWhiteSpace(snapshot.SectionId) || string.IsNullOrWhiteSpace(snapshot.ViewId) ||
            string.IsNullOrWhiteSpace(snapshot.CoordinateFrameId) || !Sha(snapshot.SourceSha256) ||
            !FiniteVector(snapshot.PlaneOriginMm) || !UnitCapable(snapshot.PlaneNormal) || !UnitCapable(snapshot.ViewingDirection) || !UnitCapable(snapshot.InPlaneXDirection))
            throw new ArgumentException("截面快照的平面标识无效。", nameof(snapshot));
        if (Math.Abs(Dot(Unit(snapshot.PlaneNormal), Unit(snapshot.InPlaneXDirection))) > 1e-6)
            throw new ArgumentException("截面平面内X方向的截取点必须位于其平面内。", nameof(snapshot));
        if (isModel && (snapshot.NativeModelSha256 is null || !Sha(snapshot.NativeModelSha256)))
            throw new ArgumentException("模型剖面快照需要实际的原生模型 SHA-256。", nameof(snapshot));
        if (snapshot.Loops.Select(loop => loop.LoopId).Distinct(StringComparer.Ordinal).Count() != snapshot.Loops.Count)
            throw new ArgumentException("剖面循环的标识符必须唯一。", nameof(snapshot));
        var primitiveIds = snapshot.BoundaryPrimitives.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var loop in snapshot.Loops)
        {
            if (string.IsNullOrWhiteSpace(loop.LoopId) || loop.PrimitiveIds.Count == 0 || loop.PrimitiveIds.Any(id => !primitiveIds.Contains(id)) ||
                loop.PrimitiveIds.Distinct(StringComparer.Ordinal).Count() != loop.PrimitiveIds.Count || loop.AreaMm2 is { } area && !Positive(area))
                throw new ArgumentException($"剖面环 '{loop.LoopId}' 无效或缺少边界基本元素。", nameof(snapshot));
        }
    }

    private static ProjectionSnapshot Projection(SectionSnapshot snapshot, string? nativeModelSha, bool reopened) => new()
    {
        ViewId = snapshot.ViewId,
        CoordinateFrameId = snapshot.CoordinateFrameId,
        SourceSha256 = snapshot.SourceSha256,
        NativeModelSha256 = nativeModelSha,
        NativeModelReopened = reopened,
        CaptureComplete = snapshot.CaptureComplete,
        CaptureLimitations = snapshot.CaptureLimitations,
        Primitives = snapshot.BoundaryPrimitives
    };

    private sealed record LoopGeometry(bool Closed, double? AreaMm2, ProjectionPointMm? BoundaryProbePoint,
        Func<ProjectionPointMm, bool>? Contains);

    private static void CompareLoops(SectionSnapshot expectedSnapshot, SectionSnapshot actualSnapshot,
        ProjectionComparisonOptions options, double tolerance, List<SectionDifference> differences)
    {
        var expected = expectedSnapshot.Loops;
        var actual = actualSnapshot.Loops;
        if (expected.Count == 0)
        {
            differences.Add(Diff("loop-scope-empty", "源剖面没有必要的材料/空洞环；空剖面范围无法通过。"));
            return;
        }

        var primitiveMap = BuildPrimitiveMap(expectedSnapshot.BoundaryPrimitives, actualSnapshot.BoundaryPrimitives, options);
        if (primitiveMap is null)
        {
            differences.Add(Diff("loop-boundary-map", "源/模型的剖面边界没有一个唯一的几何原语映射，因此无法认证循环的所有权。"));
            return;
        }

        var expectedGeometry = expected.ToDictionary(loop => loop.LoopId,
            loop => AnalyzeLoop(loop, expectedSnapshot.BoundaryPrimitives, tolerance), StringComparer.Ordinal);
        var actualGeometry = actual.ToDictionary(loop => loop.LoopId,
            loop => AnalyzeLoop(loop, actualSnapshot.BoundaryPrimitives, tolerance), StringComparer.Ordinal);
        foreach (var item in expectedGeometry)
            if (!item.Value.Closed) differences.Add(Diff("loop-open", $"源环 '{item.Key}' 不是封闭的支持线/圆边界。"));
        foreach (var item in actualGeometry)
            if (!item.Value.Closed) differences.Add(Diff("loop-open", $"实际闭环 '{item.Key}' 不是一个封闭的支持线/圆边界。"));
        if (differences.Any(item => item.Kind == "loop-open")) return;

        ValidateLoopNesting(expected, expectedGeometry, "source", differences);
        ValidateLoopNesting(actual, actualGeometry, "actual", differences);

        var used = new HashSet<int>();
        foreach (var sourceLoop in expected)
        {
            var mappedBoundary = sourceLoop.PrimitiveIds.Select(id => primitiveMap[id]).Order(StringComparer.Ordinal).ToArray();
            var candidates = actual.Select((loop, index) => (loop, index)).Where(item => !used.Contains(item.index) &&
                item.loop.IsVoid == sourceLoop.IsVoid &&
                item.loop.PrimitiveIds.Order(StringComparer.Ordinal).SequenceEqual(mappedBoundary, StringComparer.Ordinal)).ToArray();
            if (candidates.Length == 0)
            {
                differences.Add(Diff("loop-membership", $"封闭 '{sourceLoop.LoopId}' 材质/空洞分类没有几何对应的模型边界。"));
                continue;
            }
            if (candidates.Length > 1)
            {
                differences.Add(Diff("loop-ambiguous", $"封闭环{sourceLoop.LoopId}映射到具有相同边界成员的多个实际环。"));
                continue;
            }
            var match = candidates[0];
            used.Add(match.index);
            var sourceShape = expectedGeometry[sourceLoop.LoopId];
            var actualShape = actualGeometry[match.loop.LoopId];
            if (sourceShape.AreaMm2 is not { } sourceArea || actualShape.AreaMm2 is not { } actualArea)
            {
                differences.Add(Diff("loop-geometry-unsupported", $"封闭环{sourceLoop.LoopId}的区域无法从支持的边界几何体独立重建。"));
                continue;
            }
            if (sourceLoop.AreaMm2 is { } declaredSource && Math.Abs(declaredSource - sourceArea) > tolerance)
                differences.Add(Diff("loop-source-area-inconsistent", $"源环 '{sourceLoop.LoopId}' 宣称的区域与其自身的边界不一致。"));
            if (match.loop.AreaMm2 is { } declaredActual && Math.Abs(declaredActual - actualArea) > tolerance)
                differences.Add(Diff("loop-model-area-inconsistent", $"实际闭环 '{match.loop.LoopId}' 宣布的面积与其自身的边界不一致。"));
            var error = Math.Abs(sourceArea - actualArea);
            if (error > tolerance)
                differences.Add(Diff("loop-area", $"循环 '{sourceLoop.LoopId}' 的几何面积相差{error:0.###}² 毫米²。"));
        }
        if (actual.Select((loop, index) => (loop, index)).Any(item => !used.Contains(item.index)))
            differences.Add(Diff("loop-extra", "实际剖面包含未匹配的额外材料/空洞环(s)."));
    }

    private static void CompareConnectivity(SectionSpec spec, IReadOnlyList<ConnectivityCheck> checks, string modelSha,
        List<SectionDifference> differences, out bool unverifiable)
    {
        unverifiable = false;
        if (spec.RequiredConnectivityRequirementIds.Count > 0 && spec.RequiredConnectivity.Count == 0)
        {
            differences.Add(Diff("connectivity-unversioned", "遗留的T08要求ID不携带冻结的源/要求指纹，无法证明一个剖面依赖性。"));
            unverifiable = true;
        }
        foreach (var dependency in spec.RequiredConnectivity)
        {
            var id = dependency.RequirementId;
            var matches = checks.Where(check => check.RequirementId == id && check.SourceFactId == dependency.SourceFactId &&
                check.SourceRevisionId == dependency.SourceRevisionId &&
                check.SourceFactFingerprint.Equals(dependency.SourceFactFingerprint, StringComparison.OrdinalIgnoreCase) &&
                check.RequirementFingerprint.Equals(dependency.RequirementFingerprint, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length != 1)
            {
                differences.Add(Diff("connectivity-missing", $"剖面需要一个T08结果对于'{id}'，并且源修订/事实/要求指纹已锁定。"));
                unverifiable = true;
                continue;
            }
            var check = matches[0];
            if (!check.ActualModelSha256.Equals(modelSha, StringComparison.OrdinalIgnoreCase) || !check.ModelReopened)
            {
                differences.Add(Diff("connectivity-model", $"连接性结果 '{id}' 属于不同的模型，或者不是从一个重新打开并保存的模型中测量得到的。"));
                unverifiable = true;
                continue;
            }
            if (check.Status == ConnectivityStatus.Failed)
                differences.Add(Diff("connectivity-failed", $"连接要求 '{id}' 失败，无法被匹配的 ' 2D ' 部分所覆盖。"));
            else if (check.Status != ConnectivityStatus.Passed)
            {
                differences.Add(Diff("connectivity-unverifiable", $"连接性要求 '{id}' 未被验证。"));
                unverifiable = true;
            }
        }
    }

    private readonly record struct PrimitiveMetric(double Position, double Size, double Parameter)
    {
        public bool Within(ProjectionComparisonOptions options, ProjectionPrimitiveKind kind) =>
            Position <= options.PositionToleranceMm &&
            Size <= (kind is ProjectionPrimitiveKind.Circle or ProjectionPrimitiveKind.Arc ? options.RadiusToleranceMm : options.LineLengthToleranceMm) &&
            (kind != ProjectionPrimitiveKind.Arc || Parameter <= options.ArcSweepToleranceDegrees);
    }

    private static IReadOnlyDictionary<string, string>? BuildPrimitiveMap(IReadOnlyList<ProjectionPrimitive> source,
        IReadOnlyList<ProjectionPrimitive> model, ProjectionComparisonOptions options)
    {
        var used = new HashSet<int>();
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var expected in source)
        {
            var candidates = model.Select((item, index) => (item, index, error: PrimitiveError(expected, item)))
                .Where(candidate => !used.Contains(candidate.index) && candidate.item.Kind == expected.Kind &&
                    candidate.item.LineStyle.Equals(expected.LineStyle, StringComparison.OrdinalIgnoreCase) && candidate.error.Within(options, expected.Kind))
                .OrderBy(candidate => candidate.error.Position).ThenBy(candidate => candidate.error.Size).ThenBy(candidate => candidate.error.Parameter).ToArray();
            if (candidates.Length == 0) return null;
            if (candidates.Length > 1 && SameMetric(candidates[0].error, candidates[1].error)) return null;
            used.Add(candidates[0].index);
            result[expected.Id] = candidates[0].item.Id;
        }
        return used.Count == model.Count ? result : null;
    }

    private static PrimitiveMetric PrimitiveError(ProjectionPrimitive expected, ProjectionPrimitive actual)
    {
        if (expected.Kind != actual.Kind) return new(double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity);
        if (expected.Kind == ProjectionPrimitiveKind.Circle)
            return new(Distance(expected.Center, actual.Center), Math.Abs(expected.RadiusMm - actual.RadiusMm), 0);
        if (expected.Kind == ProjectionPrimitiveKind.Arc)
        {
            var direct = Math.Max(Distance(expected.Start, actual.Start), Distance(expected.End, actual.End));
            var reverse = Math.Max(Distance(expected.Start, actual.End), Distance(expected.End, actual.Start));
            return new(Math.Max(Distance(expected.Center, actual.Center), Math.Min(direct, reverse)),
                Math.Abs(expected.RadiusMm - actual.RadiusMm),
                Math.Min(Math.Abs(expected.SweepDegrees - actual.SweepDegrees), Math.Abs(expected.SweepDegrees + actual.SweepDegrees)));
        }
        var a = Math.Max(Distance(expected.Start, actual.Start), Distance(expected.End, actual.End));
        var b = Math.Max(Distance(expected.Start, actual.End), Distance(expected.End, actual.Start));
        return new(Math.Min(a, b), Math.Abs(Distance(expected.Start, expected.End) - Distance(actual.Start, actual.End)), 0);
    }

    private static bool SameMetric(PrimitiveMetric a, PrimitiveMetric b) =>
        Math.Abs(a.Position - b.Position) <= 1e-12 && Math.Abs(a.Size - b.Size) <= 1e-12 && Math.Abs(a.Parameter - b.Parameter) <= 1e-12;

    private static LoopGeometry AnalyzeLoop(SectionLoop loop, IReadOnlyList<ProjectionPrimitive> primitives, double tolerance)
    {
        var byId = primitives.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var members = loop.PrimitiveIds.Select(id => byId[id]).ToArray();
        if (members.Length == 1 && members[0].Kind == ProjectionPrimitiveKind.Circle)
        {
            var circle = members[0];
            var circleBoundaryProbe = new ProjectionPointMm(circle.Center.X + circle.RadiusMm, circle.Center.Y);
            return new(true, Math.PI * circle.RadiusMm * circle.RadiusMm, circleBoundaryProbe,
                point => Distance(point, circle.Center) < circle.RadiusMm - 1e-9);
        }
        if (members.Any(item => item.Kind != ProjectionPrimitiveKind.Line)) return new(false, null, null, null);
        var remaining = members.ToList();
        var ordered = new List<ProjectionPointMm> { remaining[0].Start, remaining[0].End };
        remaining.RemoveAt(0);
        while (remaining.Count > 0)
        {
            var tail = ordered[^1];
            var matches = remaining.Select((line, index) => (line, index, ds: Distance(tail, line.Start), de: Distance(tail, line.End)))
                .Where(item => Math.Min(item.ds, item.de) <= tolerance).ToArray();
            if (matches.Length != 1) return new(false, null, null, null);
            var next = matches[0];
            remaining.RemoveAt(next.index);
            ordered.Add(next.ds <= next.de ? next.line.End : next.line.Start);
        }
        if (ordered.Count < 4 || Distance(ordered[^1], ordered[0]) > tolerance) return new(false, null, null, null);
        ordered.RemoveAt(ordered.Count - 1);
        var doubleArea = 0d;
        for (var i = 0; i < ordered.Count; i++)
        {
            var p = ordered[i]; var q = ordered[(i + 1) % ordered.Count];
            doubleArea += p.X * q.Y - q.X * p.Y;
        }
        if (!double.IsFinite(doubleArea) || Math.Abs(doubleArea) <= 1e-12) return new(false, null, null, null);
        var area = Math.Abs(doubleArea) / 2;
        var boundaryProbe = new ProjectionPointMm((ordered[0].X + ordered[1].X) / 2, (ordered[0].Y + ordered[1].Y) / 2);
        return new(true, area, boundaryProbe, point => PointInPolygon(point, ordered));
    }

    private static void ValidateLoopNesting(IReadOnlyList<SectionLoop> loops, IReadOnlyDictionary<string, LoopGeometry> geometry,
        string label, List<SectionDifference> differences)
    {
        foreach (var loop in loops)
        {
            var current = geometry[loop.LoopId];
            var point = current.BoundaryProbePoint;
            if (point is null || current.AreaMm2 is not { } currentArea)
            {
                differences.Add(Diff("loop-nesting", $"{label}循环 '{loop.LoopId}' 缺少独立界定/面积证据，无法证明包含关系。"));
                continue;
            }
            var containers = loops.Where(candidate => candidate.LoopId != loop.LoopId &&
                    geometry[candidate.LoopId].AreaMm2 is { } area && area > currentArea + 1e-9 &&
                    geometry[candidate.LoopId].Contains?.Invoke(point.Value) == true)
                .OrderBy(candidate => geometry[candidate.LoopId].AreaMm2!.Value).ToArray();
            if (containers.Length == 0)
            {
                if (loop.IsVoid)
                    differences.Add(Diff("loop-nesting", $" {label} 空腔闭环 ' {loop.LoopId} ' 没有包含材料的父对象。"));
                continue;
            }
            var parentArea = geometry[containers[0].LoopId].AreaMm2!.Value;
            if (containers.Length > 1 && Math.Abs(geometry[containers[1].LoopId].AreaMm2!.Value - parentArea) <= 1e-9)
            {
                differences.Add(Diff("loop-nesting", $"{label}循环 '{loop.LoopId}' 有模糊的等面积直接容器。"));
                continue;
            }
            var parent = containers[0];
            if (loop.IsVoid == parent.IsVoid)
                differences.Add(Diff("loop-nesting", $"{label}循环 '{loop.LoopId}' 和它的直接父循环 '{parent.LoopId}' 不交替材料/空洞分类。"));
        }
    }

    private static bool PointInPolygon(ProjectionPointMm point, IReadOnlyList<ProjectionPointMm> polygon)
    {
        var inside = false;
        for (var i = 0; i < polygon.Count; i++)
        {
            var a = polygon[i]; var b = polygon[(i + 1) % polygon.Count];
            if ((a.Y > point.Y) != (b.Y > point.Y) && point.X < (b.X - a.X) * (point.Y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
        }
        return inside;
    }

    private static SectionDifference Diff(string kind, string message)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(kind + "\n" + message)))[..16];
        return new() { DifferenceId = "section-diff-" + hash.ToLowerInvariant(), Kind = kind, Message = message };
    }
    private static bool SameDirected(Vector3 a, Vector3 b, double toleranceDegrees)
    {
        var cosine = Math.Clamp(Dot(a, b) / (Norm(a) * Norm(b)), -1, 1);
        return Math.Acos(cosine) * 180 / Math.PI <= toleranceDegrees;
    }
    private static bool Sha(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);
    private static bool Positive(double value) => double.IsFinite(value) && value > 0;
    private static bool FiniteVector(Vector3 v) => double.IsFinite(v.X) && double.IsFinite(v.Y) && double.IsFinite(v.Z);
    private static bool UnitCapable(Vector3 v) => FiniteVector(v) && Norm(v) > 1e-12;
    private static double Dot(Vector3 a, Vector3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    private static double Norm(Vector3 v) => Math.Sqrt(Dot(v, v));
    private static Vector3 Unit(Vector3 v) { var n = Norm(v); return new(v.X / n, v.Y / n, v.Z / n); }
    private static double Distance(Vector3 a, Vector3 b) => Norm(new(a.X - b.X, a.Y - b.Y, a.Z - b.Z));
    private static double Distance(ProjectionPointMm a, ProjectionPointMm b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
    private static string Vector(Vector3 v) => string.Join(",", new[] { v.X, v.Y, v.Z }.Select(x => x.ToString("R", System.Globalization.CultureInfo.InvariantCulture)));
}
