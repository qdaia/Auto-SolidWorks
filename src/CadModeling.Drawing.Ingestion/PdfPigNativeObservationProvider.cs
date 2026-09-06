using System.Globalization;
using System.IO;
using CadModeling.Drawing.Contracts;
using CadModeling.Drawing.Providers.Abstractions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Graphics;

namespace CadModeling.Drawing.Ingestion;

public sealed class PdfPigNativeObservationProvider : IPdfNativeObservationProvider
{
    public const string ProviderName = "pdfpig-native";
    public const string ProviderVersion = "0.1.16";

    public Task<int> GetPageCountAsync(string pdfPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var document = PdfDocument.Open(pdfPath);
        return Task.FromResult(document.NumberOfPages);
    }

    public Task<NativePdfPageObservation> ObservePageAsync(ProviderPageContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var document = PdfDocument.Open(context.SourcePath);
        var page = document.GetPage(context.PageNumber);
        var frameId = $"page-{context.PageNumber:D4}-pdf-user-space";
        var regionId = $"page-{context.PageNumber:D4}-native-region";
        var observations = new List<ObservationEntity>();
        var pdfPigAssembly = typeof(PdfDocument).Assembly.Location;
        var provenance = IngestionUtilities.Provenance(ProviderName, ProviderVersion, context.ProviderProfile, context.DeterministicSeed,
            binarySha: string.IsNullOrWhiteSpace(pdfPigAssembly) || !File.Exists(pdfPigAssembly) ? null : IngestionUtilities.Sha256File(pdfPigAssembly));

        var wordIndex = 0;
        foreach (var word in page.GetWords().OrderByDescending(word => word.BoundingBox.Top).ThenBy(word => word.BoundingBox.Left))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var letters = word.Letters.ToArray();
            if (letters.Length == 0) continue;
            var literal = word.Text;
            var left = word.BoundingBox.Left;
            var right = word.BoundingBox.Right;
            var bottom = word.BoundingBox.Bottom;
            var top = word.BoundingBox.Top;
            var id = IngestionUtilities.StableId("obs-native-text", context.SourceSha256, context.PageNumber, wordIndex, literal, left, bottom);
            observations.Add(new()
            {
                ObservationId = id,
                SourceRegionId = regionId,
                Kind = ObservationKind.Text,
                RawLiteral = literal,
                Geometry = Rectangle(frameId, left, bottom, right, top),
                EvidenceStatus = EvidenceStatus.Candidate,
                Confidence = 0.99,
                CandidateProperties = Properties(
                    ("modality", "native_pdf"),
                    ("provider", ProviderName),
                    ("coordinate_unit", "pdf_user_space_point"),
                    ("font_name", letters[0].FontName ?? string.Empty),
                    ("font_size", letters[0].PointSize.ToString("R", CultureInfo.InvariantCulture)),
                    ("word_index", wordIndex.ToString(CultureInfo.InvariantCulture)),
                    ("fusion_bbox", FusionBox(left, bottom, right, top, page.Height, context.RenderDpi)))
            });
            wordIndex++;
        }

        var pathIndex = 0;
        foreach (PdfPath path in page.Paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var dashPattern = path.LineDashPattern;
            var dash = !dashPattern.HasValue || dashPattern.Value.Array.Count == 0
                ? "solid"
                : string.Join(",", dashPattern.Value.Array.Select(value => value.ToString("R", CultureInfo.InvariantCulture)));
            var subpathIndex = 0;
            foreach (var subpath in path)
            {
                var commands = subpath.Commands;
                var bbox = subpath.GetBoundingRectangle();
                var curveCount = commands.Count(command => command is PdfSubpath.CubicBezierCurve or PdfSubpath.QuadraticBezierCurve);
                var isCircle = bbox is not null && curveCount >= 4 &&
                               Math.Abs(bbox.Value.Width - bbox.Value.Height) <= Math.Max(bbox.Value.Width, bbox.Value.Height) * 0.04;
                if (isCircle)
                {
                    var box = bbox!.Value;
                    observations.Add(PathObservation(context, regionId, frameId, path, pathIndex, subpathIndex, -1,
                        ObservationKind.Circle, Rectangle(frameId, box.Left, box.Bottom, box.Right, box.Top), dash,
                        ("native_shape", "closed_equal_axis_bezier")));
                }

                var commandIndex = 0;
                foreach (var command in commands)
                {
                    switch (command)
                    {
                        case PdfSubpath.Line line:
                            observations.Add(PathObservation(context, regionId, frameId, path, pathIndex, subpathIndex, commandIndex,
                                ObservationKind.VisibleLine,
                                [Point(frameId, line.From), Point(frameId, line.To)], dash,
                                ("primitive", "line")));
                            break;
                        case PdfSubpath.CubicBezierCurve curve when !isCircle:
                            observations.Add(PathObservation(context, regionId, frameId, path, pathIndex, subpathIndex, commandIndex,
                                ObservationKind.Arc,
                                [Point(frameId, curve.StartPoint), Point(frameId, curve.FirstControlPoint), Point(frameId, curve.SecondControlPoint), Point(frameId, curve.EndPoint)], dash,
                                ("primitive", "cubic_bezier_arc_candidate")));
                            break;
                        case PdfSubpath.QuadraticBezierCurve curve when !isCircle:
                            observations.Add(PathObservation(context, regionId, frameId, path, pathIndex, subpathIndex, commandIndex,
                                ObservationKind.Arc,
                                [Point(frameId, curve.StartPoint), Point(frameId, curve.ControlPoint), Point(frameId, curve.EndPoint)], dash,
                                ("primitive", "quadratic_bezier_arc_candidate")));
                            break;
                    }
                    commandIndex++;
                }
                subpathIndex++;
            }
            pathIndex++;
        }

        var diagnostics = new List<IngestionDiagnostic>();
        if (observations.Count == 0)
            diagnostics.Add(new() { Code = "ING-NATIVE-EMPTY", Severity = ContractDiagnosticSeverity.Warning, Message = "The PDF page exposed no native text or path observations.", PageNumber = context.PageNumber, ProviderName = ProviderName });

        return Task.FromResult(new NativePdfPageObservation
        {
            PageNumber = context.PageNumber,
            WidthPoints = page.Width,
            HeightPoints = page.Height,
            RotationDegrees = page.Rotation.Value,
            HasOptionalContent = page.GetOptionalContents().Count > 0,
            Observations = observations,
            Provenance = provenance,
            Diagnostics = diagnostics
        });
    }

    private static ObservationEntity PathObservation(
        ProviderPageContext context, string regionId, string frameId, PdfPath path, int pathIndex, int subpathIndex,
        int commandIndex, ObservationKind kind, IReadOnlyList<LocatedPoint2> geometry, string dash,
        params (string Key, string Value)[] extra)
    {
        var values = new List<(string Key, string Value)>
        {
            ("modality", "native_pdf"), ("provider", ProviderName), ("coordinate_unit", "pdf_user_space_point"),
            ("path_index", pathIndex.ToString(CultureInfo.InvariantCulture)),
            ("subpath_index", subpathIndex.ToString(CultureInfo.InvariantCulture)),
            ("command_index", commandIndex.ToString(CultureInfo.InvariantCulture)),
            ("line_width", path.LineWidth.ToString("R", CultureInfo.InvariantCulture)),
            ("dash_pattern", dash), ("is_stroked", path.IsStroked.ToString().ToLowerInvariant()),
            ("is_filled", path.IsFilled.ToString().ToLowerInvariant())
        };
        values.AddRange(extra);
        return new()
        {
            ObservationId = IngestionUtilities.StableId("obs-native-path", context.SourceSha256, context.PageNumber, pathIndex, subpathIndex, commandIndex, kind),
            SourceRegionId = regionId,
            Kind = kind,
            Geometry = geometry,
            EvidenceStatus = EvidenceStatus.Candidate,
            Confidence = 0.98,
            CandidateProperties = Properties(values.ToArray())
        };
    }

    private static LocatedPoint2 Point(string frameId, PdfPoint point) => new() { CoordinateFrameId = frameId, X = point.X, Y = point.Y };

    private static IReadOnlyList<LocatedPoint2> Rectangle(string frameId, double left, double bottom, double right, double top) =>
        [new() { CoordinateFrameId = frameId, X = left, Y = bottom }, new() { CoordinateFrameId = frameId, X = right, Y = bottom }, new() { CoordinateFrameId = frameId, X = right, Y = top }, new() { CoordinateFrameId = frameId, X = left, Y = top }];

    private static IReadOnlyDictionary<string, string> Properties(params (string Key, string Value)[] values) =>
        new SortedDictionary<string, string>(values.ToDictionary(value => value.Key, value => value.Value, StringComparer.Ordinal), StringComparer.Ordinal);

    private static string FusionBox(double left, double bottom, double right, double top, double pageHeight, int dpi)
    {
        var scale = dpi / 72d;
        return string.Join(",", new[] { left * scale, (pageHeight - top) * scale, right * scale, (pageHeight - bottom) * scale }
            .Select(value => value.ToString("R", CultureInfo.InvariantCulture)));
    }
}
