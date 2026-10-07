using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using CadModeling.Ir;

namespace CadModeling.Core;

[JsonConverter(typeof(JsonStringEnumConverter<ObservationViewKind>))]
public enum ObservationViewKind
{
    SourceCrop,
    OrthographicFront,
    OrthographicTop,
    OrthographicRight,
    Isometric,
    FullPlaneSection
}

[JsonConverter(typeof(JsonStringEnumConverter<ObservationStatus>))]
public enum ObservationStatus { Completed, Reused, Unsupported, Unverifiable, Failed, BudgetExceeded }

public sealed record ObservationBudget
{
    public int MaxAttempts { get; init; } = 6;
    public int MaxUniqueRequests { get; init; } = 4;
    public int MaxRepeatsPerIdentity { get; init; } = 1;
}

public sealed record ObservationSourceRegion
{
    public required string RegionId { get; init; }
    public required string ViewId { get; init; }
    public required string CoordinateFrameId { get; init; }
    public required string SourceSha256 { get; init; }
    public double Left { get; init; }
    public double Top { get; init; }
    public double Right { get; init; }
    public double Bottom { get; init; }
}

public sealed record ObservationMeasurementIntent
{
    public MeasurementKind Kind { get; init; }
    public GeometryRef? SecondaryGeometry { get; init; }
    public DrawingValueUnit Unit { get; init; } = DrawingValueUnit.Millimeter;
    public double NumericalTolerance { get; init; } = 0.001;
}

public sealed record ObservationRequest
{
    public const string ContractVersion = "1.0.0";
    public string Contract { get; init; } = ContractVersion;
    public required string RequestId { get; init; }
    public required string Question { get; init; }
    public required string SourceRevisionId { get; init; }
    public required string SourceSha256 { get; init; }
    public required string ModelSha256 { get; init; }
    public GeometryRef? TargetGeometry { get; init; }
    public ObservationSourceRegion? SourceRegion { get; init; }
    public ObservationViewKind RequestedView { get; init; } = ObservationViewKind.Isometric;
    public SectionSpec? Section { get; init; }
    public ObservationBudget Budget { get; init; } = new();
    public IReadOnlyList<ObservationMeasurementIntent> MeasurementIntents { get; init; } = [];
}

public sealed record ObservationCapabilities
{
    public IReadOnlySet<ObservationViewKind> SupportedViews { get; init; } =
        new HashSet<ObservationViewKind>
        {
            ObservationViewKind.SourceCrop,
            ObservationViewKind.OrthographicFront,
            ObservationViewKind.OrthographicTop,
            ObservationViewKind.OrthographicRight,
            ObservationViewKind.Isometric
        };
    public bool SupportsFullPlaneSection { get; init; }
}

public sealed record ObservationRenderArtifact
{
    public string? RequestId { get; init; }
    public bool OutcomeUnknown { get; init; }
    public bool Success { get; init; }
    public string? OutputPath { get; init; }
    public string? OutputSha256 { get; init; }
    public ObservationViewKind ActualView { get; init; }
    public string? CameraIdentity { get; init; }
    public string? SectionPlaneFingerprint { get; init; }
    public IReadOnlyList<string> CoverageIds { get; init; } = [];
    public string Message { get; init; } = string.Empty;
}

public sealed record ObservationCaptureRequest
{
    public required ObservationRequest Observation { get; init; }
    public required string NativePath { get; init; }
    public required string OutputDirectory { get; init; }
    public int PixelWidth { get; init; } = 1280;
    public int PixelHeight { get; init; } = 960;
}

public sealed record ObservationResult
{
    public required string RequestId { get; init; }
    public required string RequestFingerprint { get; init; }
    public required string SourceRevisionId { get; init; }
    public required string SourceSha256 { get; init; }
    public required string ModelSha256 { get; init; }
    public ObservationStatus Status { get; init; }
    public bool Reused { get; init; }
    public int AttemptIndex { get; init; }
    public int UniqueRequestCount { get; init; }
    public ObservationRenderArtifact? Artifact { get; init; }
    public IReadOnlyList<MeasurementQuery> DerivedMeasurementRequests { get; init; } = [];
    public string StopReason { get; init; } = string.Empty;
}

public sealed class ObservationSession
{
    private readonly Dictionary<string, ObservationResult> _completed = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _attemptsByIdentity = new(StringComparer.OrdinalIgnoreCase);
    private int _attempts;

    public ObservationResult Observe(ObservationRequest request, ObservationCapabilities capabilities,
        Func<ObservationRequest, ObservationRenderArtifact> renderer)
    {
        using var timing = CadModeling.Ir.PerformanceTrace.Begin("observation.decide");
        Validate(request);
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(renderer);
        var fingerprint = Fingerprint(request);
        if (_completed.TryGetValue(fingerprint, out var cached))
        {
            if (cached.Artifact is { } cachedArtifact && ArtifactStillValid(request, cachedArtifact, out _))
                return cached with { RequestId=request.RequestId, Status = ObservationStatus.Reused, Reused = true,
                    DerivedMeasurementRequests=BuildMeasurementRequests(request), StopReason = "精确源/模型/问题/视图的身份以及不可变的输出字节被重复使用。" };

            // A cache entry is evidence only while the artifact bytes and frozen view/section identity
            // still match.  Invalidating it also permits one replacement render for this identity;
            // otherwise MaxRepeatsPerIdentity would turn storage corruption into a permanent dead end.
            _completed.Remove(fingerprint);
            _attemptsByIdentity.Remove(IdentityWithoutAttempt(request));
        }

        var supported=request.RequestedView==ObservationViewKind.FullPlaneSection
            ? capabilities.SupportsFullPlaneSection
            : capabilities.SupportedViews.Contains(request.RequestedView);
        if (!supported)
            return Result(ObservationStatus.Unsupported, "请求的观察能力未注册；没有较低精度的替代方案。");

        if (_attempts >= request.Budget.MaxAttempts || _completed.Count >= request.Budget.MaxUniqueRequests)
            return Result(ObservationStatus.BudgetExceeded, "观察预算在渲染之前耗尽。");
        var identityKey = IdentityWithoutAttempt(request);
        _attemptsByIdentity.TryGetValue(identityKey, out var repeats);
        if (repeats >= request.Budget.MaxRepeatsPerIdentity)
            return Result(ObservationStatus.BudgetExceeded, "重复观察达到配置的限制。");

        _attempts++;
        _attemptsByIdentity[identityKey] = repeats + 1;
        ObservationRenderArtifact artifact;
        try { artifact = renderer(request); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException or UnauthorizedAccessException)
        { return Result(ObservationStatus.Failed, "渲染器失败；未回退到过期输出：" + ex.Message); }

        var artifactValid = ArtifactStillValid(request, artifact, out var artifactFailure);
        if (!artifact.Success || !artifactValid)
            return Result(ObservationStatus.Failed, string.IsNullOrWhiteSpace(artifact.Message)
                ? artifactFailure : artifact.Message + " " + artifactFailure, artifact);

        var result = Result(ObservationStatus.Completed, "观察完成，源/模型/输出的身份保持不变。", artifact,
            BuildMeasurementRequests(request));
        _completed[fingerprint] = result;
        return result;

        ObservationResult Result(ObservationStatus status, string reason, ObservationRenderArtifact? artifact = null,
            IReadOnlyList<MeasurementQuery>? derived = null) => new()
        {
            RequestId = request.RequestId,
            RequestFingerprint = fingerprint,
            SourceRevisionId = request.SourceRevisionId,
            SourceSha256 = request.SourceSha256,
            ModelSha256 = request.ModelSha256,
            Status = status,
            AttemptIndex = _attempts,
            UniqueRequestCount = _completed.Count,
            Artifact = artifact,
            DerivedMeasurementRequests = derived ?? [],
            StopReason = reason
        };
    }

    public static ObservationRequest HolePositionQuestion(string requestId, string question, string sourceRevisionId,
        string sourceSha256, string modelSha256, GeometryRef cylindricalGeometry, ObservationSourceRegion? sourceRegion = null) => new()
    {
        RequestId = requestId,
        Question = question,
        SourceRevisionId = sourceRevisionId,
        SourceSha256 = sourceSha256,
        ModelSha256 = modelSha256,
        TargetGeometry = cylindricalGeometry,
        SourceRegion = sourceRegion,
        RequestedView = ObservationViewKind.OrthographicFront,
        MeasurementIntents =
        [
            new() { Kind = MeasurementKind.AxisPointX },
            new() { Kind = MeasurementKind.AxisPointY }
        ]
    };

    public static string Fingerprint(ObservationRequest request)
    {
        Validate(request);
        var region = request.SourceRegion is null ? "" : string.Join("|", request.SourceRegion.RegionId,
            request.SourceRegion.ViewId, request.SourceRegion.CoordinateFrameId, request.SourceRegion.SourceSha256,
            D(request.SourceRegion.Left), D(request.SourceRegion.Top), D(request.SourceRegion.Right), D(request.SourceRegion.Bottom));
        var section = request.Section is null ? "" : SectionVerifier.Fingerprint(request.Section);
        var geometry = request.TargetGeometry is null ? "" : GeometryRefResolver.Fingerprint(request.TargetGeometry);
        var intents = string.Join(";", request.MeasurementIntents.Select((intent, index) => string.Join(",", index,
            intent.Kind, intent.Unit, D(intent.NumericalTolerance), intent.SecondaryGeometry is null ? "" : GeometryRefResolver.Fingerprint(intent.SecondaryGeometry))));
        // RequestId is trace identity, not observation identity: a retry/new caller id for the same
        // source/model/question/view must reuse the same immutable observation evidence.
        var canonical = string.Join("\n", request.Contract, Normalize(request.Question), request.SourceRevisionId,
            request.SourceSha256.ToUpperInvariant(), request.ModelSha256.ToUpperInvariant(), geometry, region, request.RequestedView, section, intents);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    public static void Validate(ObservationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Contract != ObservationRequest.ContractVersion || string.IsNullOrWhiteSpace(request.RequestId) ||
            string.IsNullOrWhiteSpace(request.Question) || string.IsNullOrWhiteSpace(request.SourceRevisionId) ||
            !ValidHash(request.SourceSha256) || !ValidHash(request.ModelSha256))
            throw new ArgumentException("观察请求需要版本化的身份、问题、源修订版和SHA-256源/模型指纹。", nameof(request));
        if (request.Budget.MaxAttempts < 1 || request.Budget.MaxUniqueRequests < 1 || request.Budget.MaxRepeatsPerIdentity < 1)
            throw new ArgumentException("观察预算限制必须是正数。", nameof(request));
        if (request.TargetGeometry is not null)
        {
            GeometryRefResolver.Validate(request.TargetGeometry);
            if (!request.TargetGeometry.ModelSha256.Equals(request.ModelSha256, StringComparison.OrdinalIgnoreCase) ||
                request.TargetGeometry.SourceRevisionId is { Length: > 0 } revision && revision != request.SourceRevisionId)
                throw new ArgumentException("观察目标 GeometryRef 必须属于请求的模型/源修订版本。", nameof(request));
        }
        if (request.SourceRegion is { } region)
        {
            if (string.IsNullOrWhiteSpace(region.RegionId) || string.IsNullOrWhiteSpace(region.ViewId) || string.IsNullOrWhiteSpace(region.CoordinateFrameId) ||
                !region.SourceSha256.Equals(request.SourceSha256, StringComparison.OrdinalIgnoreCase) ||
                !double.IsFinite(region.Left + region.Top + region.Right + region.Bottom) || region.Left < 0 || region.Top < 0 ||
                region.Right > 1 || region.Bottom > 1 || region.Right <= region.Left || region.Bottom <= region.Top)
                throw new ArgumentException("观察源区域必须是一个有效的归一化盒，并且绑定到相同的源哈希。", nameof(request));
        }
        if (request.RequestedView == ObservationViewKind.SourceCrop && request.SourceRegion is null)
            throw new ArgumentException("源裁剪观察需要映射的源区域。", nameof(request));
        if (request.RequestedView == ObservationViewKind.FullPlaneSection && request.Section is null ||
            request.RequestedView != ObservationViewKind.FullPlaneSection && request.Section is not null)
            throw new ArgumentException("需要一个 SectionSpec 仅用于全平面剖面观察。", nameof(request));
        if (request.Section is { Type: not SectionType.FullPlane })
            throw new ArgumentException("T11 只能请求 T10 支持的固定全平面剖面能力。", nameof(request));
        if (request.Section is { } section)
        {
            SectionVerifier.Validate(section);
            if (!section.SourceSha256.Equals(request.SourceSha256,StringComparison.OrdinalIgnoreCase) ||
                !section.SourceRevisionId.Equals(request.SourceRevisionId,StringComparison.Ordinal))
                throw new ArgumentException("剖面观察证据必须与观察请求来自相同的源 SHA/修订版本。",nameof(request));
        }
        if (request.MeasurementIntents.Count > 16 || request.MeasurementIntents.Any(i => !double.IsFinite(i.NumericalTolerance) || i.NumericalTolerance <= 0))
            throw new ArgumentException("观察衍生的测量意图必须是有限且有界的。", nameof(request));
        if (request.MeasurementIntents.Count > 0 && request.TargetGeometry is null)
            throw new ArgumentException("基于观测的测量需要一个目标 GeometryRef。", nameof(request));
        if (request.TargetGeometry is { } target)
        {
            for (var i=0;i<request.MeasurementIntents.Count;i++)
            {
                var intent=request.MeasurementIntents[i];
                if (intent.SecondaryGeometry is { } secondary &&
                    (!secondary.ModelSha256.Equals(request.ModelSha256,StringComparison.OrdinalIgnoreCase) ||
                     secondary.SourceRevisionId is {Length:>0} secondaryRevision && secondaryRevision!=request.SourceRevisionId))
                    throw new ArgumentException("观察所得的secondary GeometryRef属于另一个模型/源修订版本。",nameof(request));
                DirectionalMeasurementVerifier.Validate(new MeasurementQuery
                {
                    QueryId=$"validate:{request.RequestId}:{i}",Geometry=target,SecondaryGeometry=intent.SecondaryGeometry,
                    Kind=intent.Kind,Unit=intent.Unit,NumericalTolerance=intent.NumericalTolerance
                });
            }
        }
    }

    private static IReadOnlyList<MeasurementQuery> BuildMeasurementRequests(ObservationRequest request)
    {
        if (request.TargetGeometry is null) return [];
        var queries = new List<MeasurementQuery>();
        for (var i = 0; i < request.MeasurementIntents.Count; i++)
        {
            var intent = request.MeasurementIntents[i];
            var query = new MeasurementQuery
            {
                QueryId = $"obs:{request.RequestId}:{i + 1}:{intent.Kind}",
                Geometry = request.TargetGeometry,
                SecondaryGeometry = intent.SecondaryGeometry,
                Kind = intent.Kind,
                Unit = intent.Unit,
                NumericalTolerance = intent.NumericalTolerance
            };
            DirectionalMeasurementVerifier.Validate(query);
            queries.Add(query);
        }
        return queries;
    }

    private static string IdentityWithoutAttempt(ObservationRequest request) => Fingerprint(request);
    private static bool ArtifactStillValid(ObservationRequest request, ObservationRenderArtifact artifact, out string reason)
    {
        if (!artifact.Success || artifact.ActualView != request.RequestedView)
        { reason = "渲染器返回的视图与请求的不同或失败。"; return false; }
        if (!ValidHash(artifact.OutputSha256) || string.IsNullOrWhiteSpace(artifact.OutputPath) ||
            !Path.IsPathFullyQualified(artifact.OutputPath) || !File.Exists(artifact.OutputPath))
        { reason = "观察输出路径/哈希缺失，非绝对路径，或已不再存在。"; return false; }
        string actualHash;
        try { actualHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(artifact.OutputPath))); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { reason = "观察输出字节无法读回：" + ex.Message; return false; }
        if (!actualHash.Equals(artifact.OutputSha256, StringComparison.OrdinalIgnoreCase))
        { reason = "观察输出字节在渲染后改变；缓存证据无效。"; return false; }
        if (request.RequestedView == ObservationViewKind.FullPlaneSection)
        {
            if (request.Section is null || string.IsNullOrWhiteSpace(artifact.SectionPlaneFingerprint) ||
                !artifact.SectionPlaneFingerprint.Equals(SectionVerifier.Fingerprint(request.Section), StringComparison.OrdinalIgnoreCase))
            { reason = "剖面观察与冻结的 SectionSpec 指纹不匹配。"; return false; }
        }
        reason = string.Empty;
        return true;
    }
    private static string Normalize(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim();
    private static string D(double value) => value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
    private static bool ValidHash(string? hash) => hash is { Length: 64 } && hash.All(Uri.IsHexDigit);
}
