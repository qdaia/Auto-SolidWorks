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
                    DerivedMeasurementRequests=BuildMeasurementRequests(request), StopReason = "Exact source/model/question/view identity and immutable output bytes reused." };

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
            return Result(ObservationStatus.Unsupported, "Requested observation capability is not registered; no lower-fidelity fallback was substituted.");

        if (_attempts >= request.Budget.MaxAttempts || _completed.Count >= request.Budget.MaxUniqueRequests)
            return Result(ObservationStatus.BudgetExceeded, "Observation budget exhausted before rendering.");
        var identityKey = IdentityWithoutAttempt(request);
        _attemptsByIdentity.TryGetValue(identityKey, out var repeats);
        if (repeats >= request.Budget.MaxRepeatsPerIdentity)
            return Result(ObservationStatus.BudgetExceeded, "Repeated observation identity reached its configured limit.");

        _attempts++;
        _attemptsByIdentity[identityKey] = repeats + 1;
        ObservationRenderArtifact artifact;
        try { artifact = renderer(request); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException or UnauthorizedAccessException)
        { return Result(ObservationStatus.Failed, "Renderer failed without stale-output fallback: " + ex.Message); }

        var artifactValid = ArtifactStillValid(request, artifact, out var artifactFailure);
        if (!artifact.Success || !artifactValid)
            return Result(ObservationStatus.Failed, string.IsNullOrWhiteSpace(artifact.Message)
                ? artifactFailure : artifact.Message + " " + artifactFailure, artifact);

        var result = Result(ObservationStatus.Completed, "Observation completed with source/model/output identity preserved.", artifact,
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
            throw new ArgumentException("Observation request requires versioned identity, question, source revision and SHA-256 source/model fingerprints.", nameof(request));
        if (request.Budget.MaxAttempts < 1 || request.Budget.MaxUniqueRequests < 1 || request.Budget.MaxRepeatsPerIdentity < 1)
            throw new ArgumentException("Observation budget limits must be positive.", nameof(request));
        if (request.TargetGeometry is not null)
        {
            GeometryRefResolver.Validate(request.TargetGeometry);
            if (!request.TargetGeometry.ModelSha256.Equals(request.ModelSha256, StringComparison.OrdinalIgnoreCase) ||
                request.TargetGeometry.SourceRevisionId is { Length: > 0 } revision && revision != request.SourceRevisionId)
                throw new ArgumentException("Observation target GeometryRef must belong to the requested model/source revision.", nameof(request));
        }
        if (request.SourceRegion is { } region)
        {
            if (string.IsNullOrWhiteSpace(region.RegionId) || string.IsNullOrWhiteSpace(region.ViewId) || string.IsNullOrWhiteSpace(region.CoordinateFrameId) ||
                !region.SourceSha256.Equals(request.SourceSha256, StringComparison.OrdinalIgnoreCase) ||
                !double.IsFinite(region.Left + region.Top + region.Right + region.Bottom) || region.Left < 0 || region.Top < 0 ||
                region.Right > 1 || region.Bottom > 1 || region.Right <= region.Left || region.Bottom <= region.Top)
                throw new ArgumentException("Observation source region must be a valid normalized box bound to the same source hash.", nameof(request));
        }
        if (request.RequestedView == ObservationViewKind.SourceCrop && request.SourceRegion is null)
            throw new ArgumentException("Source-crop observations require a mapped source region.", nameof(request));
        if (request.RequestedView == ObservationViewKind.FullPlaneSection && request.Section is null ||
            request.RequestedView != ObservationViewKind.FullPlaneSection && request.Section is not null)
            throw new ArgumentException("A SectionSpec is required only for full-plane section observations.", nameof(request));
        if (request.Section is { Type: not SectionType.FullPlane })
            throw new ArgumentException("T11 may request only the T10-supported fixed full-plane section capability.", nameof(request));
        if (request.Section is { } section)
        {
            SectionVerifier.Validate(section);
            if (!section.SourceSha256.Equals(request.SourceSha256,StringComparison.OrdinalIgnoreCase) ||
                !section.SourceRevisionId.Equals(request.SourceRevisionId,StringComparison.Ordinal))
                throw new ArgumentException("Observation section evidence must belong to the same source SHA/revision as the observation request.",nameof(request));
        }
        if (request.MeasurementIntents.Count > 16 || request.MeasurementIntents.Any(i => !double.IsFinite(i.NumericalTolerance) || i.NumericalTolerance <= 0))
            throw new ArgumentException("Observation-derived measurement intents must be finite and bounded.", nameof(request));
        if (request.MeasurementIntents.Count > 0 && request.TargetGeometry is null)
            throw new ArgumentException("Observation-derived measurements require a target GeometryRef.", nameof(request));
        if (request.TargetGeometry is { } target)
        {
            for (var i=0;i<request.MeasurementIntents.Count;i++)
            {
                var intent=request.MeasurementIntents[i];
                if (intent.SecondaryGeometry is { } secondary &&
                    (!secondary.ModelSha256.Equals(request.ModelSha256,StringComparison.OrdinalIgnoreCase) ||
                     secondary.SourceRevisionId is {Length:>0} secondaryRevision && secondaryRevision!=request.SourceRevisionId))
                    throw new ArgumentException("Observation-derived secondary GeometryRef belongs to another model/source revision.",nameof(request));
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
        { reason = "Renderer returned a failed or different view than requested."; return false; }
        if (!ValidHash(artifact.OutputSha256) || string.IsNullOrWhiteSpace(artifact.OutputPath) ||
            !Path.IsPathFullyQualified(artifact.OutputPath) || !File.Exists(artifact.OutputPath))
        { reason = "Observation output path/hash is missing, non-absolute, or no longer exists."; return false; }
        string actualHash;
        try { actualHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(artifact.OutputPath))); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { reason = "Observation output bytes cannot be reread: " + ex.Message; return false; }
        if (!actualHash.Equals(artifact.OutputSha256, StringComparison.OrdinalIgnoreCase))
        { reason = "Observation output bytes changed after rendering; cached evidence is invalid."; return false; }
        if (request.RequestedView == ObservationViewKind.FullPlaneSection)
        {
            if (request.Section is null || string.IsNullOrWhiteSpace(artifact.SectionPlaneFingerprint) ||
                !artifact.SectionPlaneFingerprint.Equals(SectionVerifier.Fingerprint(request.Section), StringComparison.OrdinalIgnoreCase))
            { reason = "Section observation does not match the complete frozen SectionSpec fingerprint."; return false; }
        }
        reason = string.Empty;
        return true;
    }
    private static string Normalize(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim();
    private static string D(double value) => value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
    private static bool ValidHash(string? hash) => hash is { Length: 64 } && hash.All(Uri.IsHexDigit);
}
