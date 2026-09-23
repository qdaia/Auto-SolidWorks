using CadModeling.Core;
using CadModeling.Drawing.Contracts;
using CadModeling.Drawing.Ingestion;

namespace AutoSolidWorks.ModelingMcp.Internal;

internal sealed record DrawingSourceProjectionRequest
{
    public required DrawingObservationDocument Observation { get; init; }
    public required DrawingViewMapDocument ViewMap { get; init; }
    public required string ViewId { get; init; }
    public string? SourceRevisionId { get; init; }
    /// <summary>Only interpretation-approved geometry IDs are admitted; raw page detections are never all promoted.</summary>
    public required IReadOnlySet<string> IncludedObservationIds { get; init; }
    public IReadOnlyDictionary<string, string> RequirementByObservationId { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, string> FactByObservationId { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, string> SourceFactFingerprintByObservationId { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, string> RequirementFingerprintByObservationId { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
    public double CircleRoundnessToleranceMm { get; init; } = 0.20;
    public double ArcCircularityToleranceMm { get; init; } = 0.20;
}

/// <summary>
/// Builds the T05 source-side projection snapshot in the T03 frozen millimeter frame. This helper intentionally
/// has no MCP annotation: stage-1 code uses it internally and does not expand the public tool surface.
/// </summary>
internal static class DrawingSourceProjectionBuilder
{
    public static ProjectionSnapshot Build(DrawingSourceProjectionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var view = request.ViewMap.Views.SingleOrDefault(item => item.ViewId == request.ViewId)
            ?? throw new ArgumentException($"View '{request.ViewId}' does not exist in the view map.", nameof(request));
        if (view.Status is ViewMapStatus.Conflict or ViewMapStatus.Unverifiable)
            throw new InvalidOperationException($"View '{view.ViewId}' is not resolved enough for projection verification.");
        var transform = request.ViewMap.Transforms.SingleOrDefault(item => item.TransformId == view.SourceToViewTransformId)
            ?? throw new InvalidOperationException($"Source-to-view transform '{view.SourceToViewTransformId}' is absent.");
        var selected = request.Observation.Observations
            .Where(item => request.IncludedObservationIds.Contains(item.ObservationId))
            .OrderBy(item => item.ObservationId, StringComparer.Ordinal).ToArray();
        var missing = request.IncludedObservationIds.Except(selected.Select(item => item.ObservationId), StringComparer.Ordinal).ToArray();
        if (missing.Length > 0)
            throw new InvalidOperationException("Approved source geometry is missing from observation evidence: " + string.Join(", ", missing));

        var primitives = new List<ProjectionPrimitive>();
        foreach (var observation in selected)
        {
            request.RequirementByObservationId.TryGetValue(observation.ObservationId, out var requirementId);
            string? factId=null, factFingerprint=null, requirementFingerprint=null;
            if (!string.IsNullOrWhiteSpace(requirementId))
            {
                if (string.IsNullOrWhiteSpace(request.SourceRevisionId) ||
                    !request.FactByObservationId.TryGetValue(observation.ObservationId,out factId) || string.IsNullOrWhiteSpace(factId) ||
                    !request.SourceFactFingerprintByObservationId.TryGetValue(observation.ObservationId,out factFingerprint) || !IsSha(factFingerprint) ||
                    !request.RequirementFingerprintByObservationId.TryGetValue(observation.ObservationId,out requirementFingerprint) || !IsSha(requirementFingerprint))
                    throw new InvalidOperationException($"Projection requirement '{requirementId}' lacks frozen source revision/fact/requirement identity.");
            }
            var points = observation.Geometry.Where(point => point.CoordinateFrameId == view.SourceFrameId).ToArray();
            switch (observation.Kind)
            {
                case ObservationKind.VisibleLine or ObservationKind.HiddenLine or ObservationKind.CenterLine when points.Length >= 2:
                    var lineStyle = observation.Kind switch
                    {
                        ObservationKind.HiddenLine => "hidden",
                        ObservationKind.CenterLine => "center",
                        _ => "visible"
                    };
                    for (var i = 0; i < points.Length - 1; i++)
                    {
                        var a = Map(transform.ForwardMatrix, points[i]);
                        var b = Map(transform.ForwardMatrix, points[i + 1]);
                        if (Distance(a, b) <= 1e-9) continue;
                        primitives.Add(new()
                        {
                            Id = $"source-{observation.ObservationId}-line-{i + 1:D2}",
                            Kind = ProjectionPrimitiveKind.Line,
                            Start = a,
                            End = b,
                            RequirementId = requirementId,
                            SourceFactId = factId,
                            SourceFactFingerprint = factFingerprint,
                            RequirementFingerprint = requirementFingerprint,
                            Required = true,
                            LineStyle = lineStyle
                        });
                    }
                    break;
                case ObservationKind.Circle when points.Length >= 2:
                    var mapped = points.Select(point => Map(transform.ForwardMatrix, point)).ToArray();
                    var left = mapped.Min(point => point.X); var right = mapped.Max(point => point.X);
                    var bottom = mapped.Min(point => point.Y); var top = mapped.Max(point => point.Y);
                    var rx = (right - left) / 2; var ry = (top - bottom) / 2;
                    if (rx <= 0 || ry <= 0 || Math.Abs(rx - ry) > request.CircleRoundnessToleranceMm)
                        throw new InvalidOperationException($"Circle '{observation.ObservationId}' is not round in the frozen millimeter frame.");
                    primitives.Add(new()
                    {
                        Id = $"source-{observation.ObservationId}-circle",
                        Kind = ProjectionPrimitiveKind.Circle,
                        Center = new((left + right) / 2, (bottom + top) / 2),
                        RadiusMm = (rx + ry) / 2,
                        RequirementId = requirementId,
                        SourceFactId = factId,
                        SourceFactFingerprint = factFingerprint,
                        RequirementFingerprint = requirementFingerprint,
                        Required = true,
                        LineStyle = ExplicitLineStyle(observation)
                    });
                    break;
                case ObservationKind.Arc when points.Length is 3 or 4:
                    var control = points.Select(point => Map(transform.ForwardMatrix, point)).ToArray();
                    var primitive = observation.CandidateProperties.TryGetValue("primitive", out var primitiveName) ? primitiveName : string.Empty;
                    ProjectionPointMm Sample(double t)
                    {
                        if (control.Length == 3)
                        {
                            var u = 1 - t;
                            return new(u * u * control[0].X + 2 * u * t * control[1].X + t * t * control[2].X,
                                u * u * control[0].Y + 2 * u * t * control[1].Y + t * t * control[2].Y);
                        }
                        var v = 1 - t;
                        return new(v * v * v * control[0].X + 3 * v * v * t * control[1].X + 3 * v * t * t * control[2].X + t * t * t * control[3].X,
                            v * v * v * control[0].Y + 3 * v * v * t * control[1].Y + 3 * v * t * t * control[2].Y + t * t * t * control[3].Y);
                    }
                    if (primitive is not ("quadratic_bezier_arc_candidate" or "cubic_bezier_arc_candidate" or "circular_arc"))
                        throw new NotSupportedException($"Arc '{observation.ObservationId}' lacks a supported circular-arc evidence type.");
                    var start = Sample(0); var mid = Sample(.5); var end = Sample(1);
                    var (center, radius) = FitCircle(start, mid, end);
                    var residual = new[] { .25, .5, .75 }.Select(t => Math.Abs(Distance(center, Sample(t)) - radius)).Max();
                    if (residual > request.ArcCircularityToleranceMm)
                        throw new InvalidOperationException($"Arc '{observation.ObservationId}' deviates from a circular arc by {residual:0.###} mm in the frozen frame.");
                    primitives.Add(new()
                    {
                        Id = $"source-{observation.ObservationId}-arc",
                        Kind = ProjectionPrimitiveKind.Arc,
                        Center = center,
                        Start = start,
                        End = end,
                        RadiusMm = radius,
                        SweepDegrees = SweepThrough(center, start, mid, end),
                        RequirementId = requirementId,
                        SourceFactId = factId,
                        SourceFactFingerprint = factFingerprint,
                        RequirementFingerprint = requirementFingerprint,
                        Required = true,
                        LineStyle = ExplicitLineStyle(observation)
                    });
                    break;
                default:
                    throw new NotSupportedException($"Projection source builder supports only approved line/circle/circular-arc observations; got '{observation.Kind}'.");
            }
        }
        if (primitives.Count == 0) throw new InvalidOperationException("No approved source line/circle/arc primitives were supplied.");
        return new()
        {
            ViewId = view.ViewId,
            CoordinateFrameId = view.ViewMillimeterFrameId,
            SourceSha256 = request.ViewMap.SourceSha256,
            SourceRevisionId = request.SourceRevisionId,
            Primitives = primitives
        };
    }

    private static bool IsSha(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static ProjectionPointMm Map(IReadOnlyList<IReadOnlyList<double>> matrix, LocatedPoint2 point)
    {
        var mapped = DrawingTransformMath.Transform2(matrix, point.X, point.Y);
        return new(mapped.X, mapped.Y);
    }

    private static double Distance(ProjectionPointMm a, ProjectionPointMm b) =>
        Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    private static string ExplicitLineStyle(ObservationEntity observation)
    {
        if (!observation.CandidateProperties.TryGetValue("line_style", out var value)) return "visible";
        var normalized = value.Trim().ToLowerInvariant();
        return normalized is "visible" or "hidden" or "center" ? normalized
            : throw new InvalidOperationException($"Observation '{observation.ObservationId}' has unsupported line_style '{value}'.");
    }

    private static (ProjectionPointMm Center, double Radius) FitCircle(ProjectionPointMm a, ProjectionPointMm b, ProjectionPointMm c)
    {
        var d = 2 * (a.X * (b.Y - c.Y) + b.X * (c.Y - a.Y) + c.X * (a.Y - b.Y));
        if (!double.IsFinite(d) || Math.Abs(d) <= 1e-9)
            throw new InvalidOperationException("Arc samples are collinear or numerically unstable; circular arc cannot be certified.");
        var aa = a.X * a.X + a.Y * a.Y; var bb = b.X * b.X + b.Y * b.Y; var cc = c.X * c.X + c.Y * c.Y;
        var center = new ProjectionPointMm((aa * (b.Y - c.Y) + bb * (c.Y - a.Y) + cc * (a.Y - b.Y)) / d,
            (aa * (c.X - b.X) + bb * (a.X - c.X) + cc * (b.X - a.X)) / d);
        var radius = Distance(center, a);
        if (!center.IsFinite || !double.IsFinite(radius) || radius <= 0)
            throw new InvalidOperationException("Circular arc fit returned invalid center/radius.");
        return (center, radius);
    }

    private static double SweepThrough(ProjectionPointMm center, ProjectionPointMm start, ProjectionPointMm mid, ProjectionPointMm end)
    {
        static double Angle(ProjectionPointMm center, ProjectionPointMm point) => Math.Atan2(point.Y - center.Y, point.X - center.X) * 180 / Math.PI;
        static double Ccw(double from, double to)
        {
            var delta = (to - from) % 360;
            return delta < 0 ? delta + 360 : delta;
        }
        var a = Angle(center, start); var m = Angle(center, mid); var b = Angle(center, end);
        var positive = Ccw(a, b); var toMid = Ccw(a, m);
        var sweep = toMid <= positive + 1e-7 ? positive : positive - 360;
        if (!double.IsFinite(sweep) || Math.Abs(sweep) <= 1e-7 || Math.Abs(sweep) >= 360 - 1e-7)
            throw new InvalidOperationException("Arc sweep is degenerate or indistinguishable from a full circle.");
        return sweep;
    }
}
