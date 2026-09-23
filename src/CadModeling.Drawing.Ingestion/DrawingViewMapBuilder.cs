using System.Security.Cryptography;
using System.Text;
using CadModeling.Drawing.Contracts;

namespace CadModeling.Drawing.Ingestion;

public sealed record ViewMapCalibrationAnchor
{
    public required string ViewId { get; init; }
    public double ModelXmm { get; init; }
    public double ModelYmm { get; init; }
    public double ModelZmm { get; init; }
    public string EvidenceId { get; init; } = string.Empty;
    public string Basis { get; init; } = string.Empty;
}

public sealed record ViewMapScaleCalibration
{
    public required string ViewId { get; init; }
    /// <summary>Model-space millimeters represented by one physical millimeter on the drawing sheet.</summary>
    public double ModelMillimetersPerSheetMillimeter { get; init; }
    public string EvidenceId { get; init; } = string.Empty;
    public string Basis { get; init; } = string.Empty;
}

public sealed record DrawingViewMapBuildRequest
{
    public required DrawingSourceManifest SourceManifest { get; init; }
    public required IReadOnlyList<DrawingObservationDocument> Pages { get; init; }
    public IReadOnlyList<ProjectionConventionEvidence> ProjectionEvidence { get; init; } = [];
    public IReadOnlyList<ViewMapCalibrationAnchor> ModelAnchors { get; init; } = [];
    public IReadOnlyList<ViewMapScaleCalibration> ScaleCalibrations { get; init; } = [];
    public string ProducerName { get; init; } = "view-map-builder";
    public string ProducerVersion { get; init; } = "1.0.0";
    public string ConfigurationHash { get; init; } = new('0', 64);
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Builds an explicit page/crop/view-mm/model-mm coordinate chain. The model transform is always rigid;
/// page calibration may convert pixel/PDF units to millimeters but is never optimized against model geometry.
/// </summary>
public static class DrawingViewMapBuilder
{
    public static DrawingViewMapDocument Build(DrawingViewMapBuildRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Pages.Count == 0) throw new ArgumentException("At least one observation page is required.", nameof(request));
        var projection = ProjectionConventionResolver.Resolve(request.ProjectionEvidence);
        if (request.ModelAnchors.GroupBy(item => item.ViewId, StringComparer.Ordinal).Any(group => group.Count() > 1))
            throw new ArgumentException("Model anchors must be unique per view.", nameof(request));
        if (request.ScaleCalibrations.GroupBy(item => item.ViewId, StringComparer.Ordinal).Any(group => group.Count() > 1))
            throw new ArgumentException("Drawing-scale calibrations must be unique per view.", nameof(request));
        var anchors = request.ModelAnchors.ToDictionary(item => item.ViewId, StringComparer.Ordinal);
        var scales = request.ScaleCalibrations.ToDictionary(item => item.ViewId, StringComparer.Ordinal);
        var artifactManifest = request.Pages.SelectMany(item => item.ArtifactManifest)
            .GroupBy(item => item.ArtifactId, StringComparer.Ordinal).Select(group => group.First()).ToList();
        var modelArtifactId = "virtual-solidworks-model-frame";
        if (!artifactManifest.Any(item => item.ArtifactId == modelArtifactId))
            artifactManifest.Add(new()
            {
                ArtifactId = modelArtifactId,
                Kind = ArtifactKind.Other,
                Uri = "urn:auto-solidworks:coordinate-frame:model-mm",
                MediaType = "application/x-coordinate-frame",
                Sha256 = HexSha256("auto-solidworks:model-mm")
            });

        var frames = request.Pages.SelectMany(item => item.CoordinateFrames)
            .GroupBy(item => item.FrameId, StringComparer.Ordinal).Select(group => group.First()).ToList();
        const string modelFrameId = "solidworks-model-mm";
        if (!frames.Any(item => item.FrameId == modelFrameId))
            frames.Add(new()
            {
                FrameId = modelFrameId,
                Space = CoordinateSpace.SolidworksModel,
                Unit = MeasurementUnit.Millimeter,
                ArtifactId = modelArtifactId,
                OriginDescription = "SolidWorks model origin; right-handed x/y/z, millimeters.",
                AxisLabels = ["x_mm", "y_mm", "z_mm"]
            });

        var transforms = request.Pages.SelectMany(item => item.Transforms)
            .GroupBy(item => item.TransformId, StringComparer.Ordinal).Select(group => group.First()).ToList();
        var views = new List<ViewMapEntry>();
        foreach (var pageDocument in request.Pages)
        {
            var pageNumber = pageDocument.SourceRegions.Select(item => item.PageNumber).Where(item => item > 0).DefaultIfEmpty(1).Min();
            var page = request.SourceManifest.Pages.SingleOrDefault(item => item.PageNumber == pageNumber)
                ?? throw new InvalidOperationException($"Source manifest has no page {pageNumber}.");
            foreach (var observedView in pageDocument.ViewRegions)
            {
                var region = pageDocument.SourceRegions.SingleOrDefault(item => item.RegionId == observedView.SourceRegionId);
                if (region is null || region.Polygon.Count < 3) continue;
                var sourceFrameId = region.Polygon[0].CoordinateFrameId;
                if (region.Polygon.Any(point => point.CoordinateFrameId != sourceFrameId))
                    throw new InvalidOperationException($"View region '{region.RegionId}' mixes coordinate frames.");
                var sourceFrame = frames.SingleOrDefault(item => item.FrameId == sourceFrameId)
                    ?? throw new InvalidOperationException($"Coordinate frame '{sourceFrameId}' is absent.");
                if (sourceFrame.Unit is not (MeasurementUnit.Pixel or MeasurementUnit.PdfPoint))
                    throw new InvalidOperationException($"View source frame '{sourceFrameId}' is not a page-space frame.");

                var bounds = Bounds(region.Polygon);
                var pageWidthMm = ToMillimeters(page.PhysicalWidth, page.PhysicalUnit);
                var pageHeightMm = ToMillimeters(page.PhysicalHeight, page.PhysicalUnit);
                var (pageUnitsX, pageUnitsY) = PageExtentUnits(pageDocument, sourceFrame, page, pageWidthMm, pageHeightMm);
                var sheetSx = pageWidthMm / pageUnitsX;
                var sheetSy = pageHeightMm / pageUnitsY;
                scales.TryGetValue(observedView.ViewRegionId, out var scaleCalibration);
                var drawingScale = scaleCalibration?.ModelMillimetersPerSheetMillimeter ?? 1d;
                var validScale = scaleCalibration is not null && double.IsFinite(drawingScale) && drawingScale > 0 &&
                    !string.IsNullOrWhiteSpace(scaleCalibration.EvidenceId);
                if (!double.IsFinite(sheetSx) || !double.IsFinite(sheetSy) || sheetSx <= 0 || sheetSy <= 0)
                    throw new InvalidOperationException($"View '{observedView.ViewRegionId}' has invalid physical calibration.");
                var sx = sheetSx * drawingScale;
                var sy = sheetSy * drawingScale;

                var localFrameId = observedView.ViewRegionId + "-mm";
                frames.Add(new()
                {
                    FrameId = localFrameId,
                    Space = CoordinateSpace.ViewLocal,
                    Unit = MeasurementUnit.Millimeter,
                    ArtifactId = sourceFrame.ArtifactId,
                    OriginDescription = "View crop bottom-left; x right, y up; coordinates are model millimeters after explicit drawing-scale calibration.",
                    AxisLabels = ["u_mm", "v_mm"]
                });
                var sourceToViewId = observedView.ViewRegionId + "-source-to-view-mm";
                IReadOnlyList<IReadOnlyList<double>> forward =
                [
                    new[] { sx, 0d, -bounds.Left * sx },
                    new[] { 0d, -sy, bounds.Bottom * sy },
                    new[] { 0d, 0d, 1d }
                ];
                transforms.Add(new()
                {
                    TransformId = sourceToViewId,
                    FromFrameId = sourceFrameId,
                    ToFrameId = localFrameId,
                    ForwardMatrix = forward,
                    InverseMatrix = DrawingTransformMath.InvertAffine3(forward),
                    InputUnit = sourceFrame.Unit,
                    OutputUnit = MeasurementUnit.Millimeter,
                    Parameters = new SortedDictionary<string, double>(StringComparer.Ordinal)
                    {
                        ["page_width_mm"] = pageWidthMm,
                        ["page_height_mm"] = pageHeightMm,
                        ["source_units_per_page_x"] = pageUnitsX,
                        ["source_units_per_page_y"] = pageUnitsY,
                        ["sheet_mm_per_source_unit_x"] = sheetSx,
                        ["sheet_mm_per_source_unit_y"] = sheetSy,
                        ["model_mm_per_sheet_mm"] = drawingScale
                    },
                    ErrorEstimate = Math.Max(sx, sy),
                    ErrorUnit = MeasurementUnit.Millimeter,
                    Source = validScale
                        ? "source-page-physical-calibration-plus-explicit-drawing-scale-no-model-fit"
                        : "source-page-physical-calibration-with-unresolved-drawing-scale",
                    BeforeArtifactId = sourceFrame.ArtifactId,
                    AfterArtifactId = sourceFrame.ArtifactId
                });

                anchors.TryGetValue(observedView.ViewRegionId, out var anchor);
                var validAnchor = anchor is not null && !string.IsNullOrWhiteSpace(anchor.EvidenceId) &&
                    double.IsFinite(anchor.ModelXmm) && double.IsFinite(anchor.ModelYmm) && double.IsFinite(anchor.ModelZmm);
                var viewToModelId = observedView.ViewRegionId + "-view-to-model-mm";
                var modelForward = ModelTransform(observedView.ViewTypeHint, anchor);
                transforms.Add(new()
                {
                    TransformId = viewToModelId,
                    FromFrameId = localFrameId,
                    ToFrameId = modelFrameId,
                    ForwardMatrix = modelForward,
                    InverseMatrix = DrawingTransformMath.InvertRigid4(modelForward),
                    InputUnit = MeasurementUnit.Millimeter,
                    OutputUnit = MeasurementUnit.Millimeter,
                    ErrorEstimate = validAnchor ? 1e-9 : 0,
                    ErrorUnit = MeasurementUnit.Millimeter,
                    Source = validAnchor ? "standard-view-orientation-explicit-model-anchor" : "standard-view-orientation-unresolved-model-origin",
                    BeforeArtifactId = sourceFrame.ArtifactId,
                    AfterArtifactId = modelArtifactId
                });
                var viewStatus = projection.Status == ViewMapStatus.Conflict || observedView.ViewTypeHint == DrawingViewType.Unknown
                    ? ViewMapStatus.Conflict
                    : validScale && validAnchor ? ViewMapStatus.Resolved : ViewMapStatus.Unverifiable;
                views.Add(new()
                {
                    ViewId = observedView.ViewRegionId,
                    ViewType = observedView.ViewTypeHint,
                    PageNumber = pageNumber,
                    SourceRegionId = observedView.SourceRegionId,
                    SourceFrameId = sourceFrameId,
                    ViewMillimeterFrameId = localFrameId,
                    ModelFrameId = modelFrameId,
                    SourceToViewTransformId = sourceToViewId,
                    ViewToModelTransformId = viewToModelId,
                    CalibrationBasis = $"Page physical calibration from source manifest; drawing scale: {(validScale ? scaleCalibration!.Basis : "unresolved")}; model anchor: {(validAnchor ? anchor!.Basis : "unresolved")}.",
                    PositionUncertaintyMm = Math.Max(sx, sy),
                    Status = viewStatus,
                    Fact = viewStatus == ViewMapStatus.Resolved
                        ? new() { Status = FactStatus.Derived, SourceIds = [scaleCalibration!.EvidenceId, anchor!.EvidenceId], Rationale = $"{scaleCalibration.Basis}; {anchor.Basis}" }
                        : new() { Status = FactStatus.Unknown, Rationale = "Drawing scale and model origin must both be independently established before this view can drive model-space geometry." }
                });
            }
        }

        var status = projection.Status == ViewMapStatus.Conflict || views.Any(item => item.Status == ViewMapStatus.Conflict)
            ? DocumentStatus.Conflict : views.All(item => item.Status == ViewMapStatus.Resolved) ? DocumentStatus.Valid : DocumentStatus.Candidate;
        return new()
        {
            DocumentId = "drawing-view-map",
            SourceSha256 = request.SourceManifest.SourceSha256,
            ProducerName = request.ProducerName,
            ProducerVersion = request.ProducerVersion,
            ConfigurationHash = request.ConfigurationHash,
            CreatedAt = request.CreatedAt,
            Status = status,
            EvidenceStatus = status == DocumentStatus.Conflict ? EvidenceStatus.Conflict : EvidenceStatus.Candidate,
            FactStatus = projection.Fact.Status,
            ArtifactManifest = artifactManifest,
            CoordinateFrames = frames.GroupBy(item => item.FrameId, StringComparer.Ordinal).Select(group => group.First()).ToArray(),
            Transforms = transforms.GroupBy(item => item.TransformId, StringComparer.Ordinal).Select(group => group.First()).ToArray(),
            ProjectionConvention = projection.Convention,
            ProjectionFact = projection.Fact,
            DrawingUnit = MeasurementUnit.Millimeter,
            UnitFact = new() { Status = FactStatus.Derived, SourceIds = [request.SourceManifest.DocumentId], Rationale = "Page coordinates are physically calibrated from the source manifest; per-view drawing scale is separately required before model-space use." },
            Views = views
        };
    }

    private static IReadOnlyList<IReadOnlyList<double>> ModelTransform(DrawingViewType view, ViewMapCalibrationAnchor? anchor)
    {
        var (u, v, n) = view switch
        {
            DrawingViewType.Front => (new[] { 1d, 0, 0 }, new[] { 0d, 1, 0 }, new[] { 0d, 0, 1 }),
            DrawingViewType.Top => (new[] { 1d, 0, 0 }, new[] { 0d, 0, -1 }, new[] { 0d, 1, 0 }),
            DrawingViewType.Bottom => (new[] { 1d, 0, 0 }, new[] { 0d, 0, 1 }, new[] { 0d, -1, 0 }),
            DrawingViewType.Left => (new[] { 0d, 0, 1 }, new[] { 0d, 1, 0 }, new[] { -1d, 0, 0 }),
            DrawingViewType.Right => (new[] { 0d, 0, -1 }, new[] { 0d, 1, 0 }, new[] { 1d, 0, 0 }),
            DrawingViewType.Rear => (new[] { -1d, 0, 0 }, new[] { 0d, 1, 0 }, new[] { 0d, 0, -1 }),
            _ => (new[] { 1d, 0, 0 }, new[] { 0d, 1, 0 }, new[] { 0d, 0, 1 })
        };
        var tx = anchor?.ModelXmm ?? 0; var ty = anchor?.ModelYmm ?? 0; var tz = anchor?.ModelZmm ?? 0;
        return
        [
            new[] { u[0], v[0], n[0], tx },
            new[] { u[1], v[1], n[1], ty },
            new[] { u[2], v[2], n[2], tz },
            new[] { 0d, 0d, 0d, 1d }
        ];
    }

    private static (double Left, double Top, double Right, double Bottom) Bounds(IReadOnlyList<LocatedPoint2> points) =>
        (points.Min(item => item.X), points.Min(item => item.Y), points.Max(item => item.X), points.Max(item => item.Y));

    private static (double X, double Y) PageExtentUnits(DrawingObservationDocument document, CoordinateFrame sourceFrame,
        SourcePage page, double pageWidthMm, double pageHeightMm)
    {
        if (sourceFrame.Unit == MeasurementUnit.PdfPoint)
            return (pageWidthMm / 25.4 * 72d, pageHeightMm / 25.4 * 72d);
        if (sourceFrame.Unit != MeasurementUnit.Pixel || page.PixelWidth <= 0 || page.PixelHeight <= 0)
            throw new InvalidOperationException($"Cannot derive full-page extent for frame '{sourceFrame.FrameId}' with unit '{sourceFrame.Unit}'.");
        if (sourceFrame.Space == CoordinateSpace.SourcePixel)
            return (page.PixelWidth, page.PixelHeight);

        var sourcePixel = document.CoordinateFrames.FirstOrDefault(item => item.Space == CoordinateSpace.SourcePixel && item.Unit == MeasurementUnit.Pixel);
        if (sourcePixel is null)
        {
            // Raster-only legacy observations may expose one pixel frame without a recorded preprocessing chain.
            // In that case the manifest pixel dimensions are the only authoritative page extent.
            if (document.CoordinateFrames.Count(item => item.Unit == MeasurementUnit.Pixel) == 1)
                return (page.PixelWidth, page.PixelHeight);
            throw new InvalidOperationException($"Normalized frame '{sourceFrame.FrameId}' has no source-pixel frame from which to derive full-page extent.");
        }
        var corners = new[]
        {
            new LocatedPoint2 { CoordinateFrameId = sourcePixel.FrameId, X = 0, Y = 0 },
            new LocatedPoint2 { CoordinateFrameId = sourcePixel.FrameId, X = page.PixelWidth, Y = 0 },
            new LocatedPoint2 { CoordinateFrameId = sourcePixel.FrameId, X = page.PixelWidth, Y = page.PixelHeight },
            new LocatedPoint2 { CoordinateFrameId = sourcePixel.FrameId, X = 0, Y = page.PixelHeight }
        };
        var mapped = new List<(double X, double Y)>();
        foreach (var corner in corners)
        {
            if (!DrawingOmissionInventoryBuilder.TryTransform(corner, sourceFrame.FrameId, document.Transforms, out var point))
                throw new InvalidOperationException($"Cannot transform full source-page extent into frame '{sourceFrame.FrameId}'.");
            mapped.Add(point);
        }
        var width = mapped.Max(item => item.X) - mapped.Min(item => item.X);
        var height = mapped.Max(item => item.Y) - mapped.Min(item => item.Y);
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0)
            throw new InvalidOperationException($"Derived page extent for frame '{sourceFrame.FrameId}' is invalid.");
        return (width, height);
    }

    private static double ToMillimeters(double value, MeasurementUnit unit) => unit switch
    {
        MeasurementUnit.Millimeter => value,
        MeasurementUnit.Inch => value * 25.4,
        _ => throw new InvalidOperationException($"Physical source page unit '{unit}' cannot be converted to millimeters.")
    };

    private static string HexSha256(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
