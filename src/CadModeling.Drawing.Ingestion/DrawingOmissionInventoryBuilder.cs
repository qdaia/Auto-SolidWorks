using System.Globalization;
using System.IO;
using System.Text.Json;
using CadModeling.Drawing.Contracts;
using CadModeling.Drawing.Providers.Abstractions;
using CadModeling.Ir;

namespace CadModeling.Drawing.Ingestion;

/// <summary>Independent source accounting: native objects, detector candidates, overlapping raw-image tiles and residual ink.</summary>
public sealed class DrawingOmissionInventoryBuilder
{
    public const int TileSize = 1400;
    public const int TileOverlap = 280;
    public const int MaximumCandidatesPerPage = 10000;
    public const int MaximumCandidatesTotal = 50000;
    private readonly List<DrawingOmissionCandidate> candidates = [];
    private readonly List<DrawingReviewRegion> regions = [];
    private readonly List<DrawingOmissionPage> pages = [];
    private bool complete = true;

    public void AddPage(DrawingObservationDocument observation, string observationPath,
        RasterPreprocessResult processed, string pageDirectory, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var pageNumber = processed.NormalizedPage.PageNumber;
        var rawPath = Path.Combine(pageDirectory, "ocr-source.png");
        if (!File.Exists(rawPath)) rawPath = processed.NormalizedPage.ImagePath;
        var raw = RasterBuffer.Load(rawPath);
        var frame = processed.NormalizedPage.CoordinateFrameId;
        var width = raw.Width; var height = raw.Height;
        var limitations = new List<string>();
        var mapped = new List<(ObservationEntity Observation, (double X, double Y)[] Points)>();
        foreach (var item in observation.Observations)
        {
            token.ThrowIfCancellationRequested();
            if (item.Geometry.Count == 0) { limitations.Add("Observation without source location: " + item.ObservationId); complete = false; continue; }
            var points = new List<(double, double)>();
            foreach (var point in item.Geometry)
            {
                if (!TryTransform(point, frame, observation.Transforms, out var transformed)) break;
                points.Add(transformed);
            }
            if (points.Count != item.Geometry.Count || points.Any(p => !double.IsFinite(p.Item1 + p.Item2)))
            { limitations.Add("Unmapped coordinate frame: " + item.ObservationId); complete = false; continue; }
            mapped.Add((item, points.ToArray()));
        }

        var dimensions = observation.DimensionObservations.GroupBy(d => d.SourceObservationId).ToDictionary(g => g.Key, g => g.First());
        var local = new List<DrawingOmissionCandidate>();
        // Group native path commands by source subpath, preserving all primitive IDs. No grouping by planned feature.
        var groups = mapped.GroupBy(m => m.Observation.Kind == ObservationKind.Text ? m.Observation.ObservationId :
            m.Observation.CandidateProperties.GetValueOrDefault("modality") == "native_pdf"
                ? "native-path-" + m.Observation.CandidateProperties.GetValueOrDefault("path_index") + "-" + m.Observation.CandidateProperties.GetValueOrDefault("subpath_index")
                : m.Observation.ObservationId);
        foreach (var group in groups)
        {
            token.ThrowIfCancellationRequested();
            var first = group.First().Observation;
            var points = group.SelectMany(m => m.Points).ToArray();
            var box = Bounds(points, width, height);
            if (box is null) { limitations.Add("Observation outside rendered page: " + first.ObservationId); complete = false; continue; }
            dimensions.TryGetValue(first.ObservationId, out var dimension);
            local.Add(new()
            {
                Id = IngestionUtilities.StableId("candidate", observation.SourceSha256, pageNumber, group.Key),
                PageNumber = pageNumber, Kind = first.Kind == ObservationKind.Text ? DrawingCandidateKind.Annotation : DrawingCandidateKind.Geometry,
                Bounds = box, Literal = first.RawLiteral, ObservationIds = group.Select(m => m.Observation.ObservationId).ToArray(),
                Provider = string.Join("+", group.Select(m => m.Observation.CandidateProperties.GetValueOrDefault("provider", "unknown")).Distinct().Order()),
                GeometryHint = string.Join("+", group.Select(m => Hint(m.Observation)).Distinct().Order()),
                Conflict = group.Any(m => m.Observation.EvidenceStatus == EvidenceStatus.Conflict),
                DimensionKind = dimension?.DimensionKind, NumericValue = dimension?.CandidateNumericValue,
                Multiplicity = dimension?.Count, SecondaryValue = dimension?.SecondaryValue
            });
        }

        local.AddRange(DrawingAnnotationAssembler.Assemble(local));
        // A separate foreground channel retains ink not near any recognized text or actual primitive stroke.
        // Geometry bounding rectangles are NEVER filled: that would hide an omitted hole inside an outer contour.
        var ink = raw.ToBinaryOtsu(out _);
        var explained = new byte[ink.Length];
        foreach (var mappedItem in mapped)
        {
            token.ThrowIfCancellationRequested();
            MarkObservation(explained, width, height, mappedItem.Observation, mappedItem.Points);
        }
        var reviewDir = Path.Combine(pageDirectory, "omission-review");
        Directory.CreateDirectory(reviewDir);
        var localRegions = new List<DrawingReviewRegion>();
        var overviewId = $"page-{pageNumber:D4}-overview";
        localRegions.Add(new()
        {
            Id = overviewId, PageNumber = pageNumber, Bounds = new(0, 0, 1, 1), Purpose = "overview",
            ImagePath = rawPath, ImageSha256 = IngestionUtilities.Sha256File(rawPath), PixelWidth = width, PixelHeight = height,
            InkPixels = ink.LongCount(p => p != 0), ResidualInkPixels = Enumerable.Range(0, ink.Length).LongCount(i => ink[i] != 0 && explained[i] == 0)
        });
        var tileNumber = 0;
        foreach (var top in TileStarts(height))
        foreach (var left in TileStarts(width))
        {
            token.ThrowIfCancellationRequested();
            var tw = Math.Min(TileSize, width-left); var th = Math.Min(TileSize, height-top);
            long dark = 0; long residual = 0;
            for (var y = top; y < top+th; y++)
            for (var x = left; x < left+tw; x++)
                if (ink[y*width+x] != 0) { dark++; if (explained[y*width+x] == 0) residual++; }
            if (dark == 0) continue; // Blank tiles have no foreground; full-page inspection remains mandatory.
            tileNumber++;
            var id = $"page-{pageNumber:D4}-detail-{tileNumber:D3}";
            var tilePath = Path.Combine(reviewDir, id + ".png");
            raw.Crop(left, top, tw, th).SavePng(tilePath);
            var box = new DrawingReviewBox(left/(double)width, top/(double)height, (left+tw)/(double)width, (top+th)/(double)height);
            localRegions.Add(new() { Id = id, PageNumber = pageNumber, Bounds = box, ImagePath = tilePath, ImageSha256 = IngestionUtilities.Sha256File(tilePath),
                PixelWidth = tw, PixelHeight = th, InkPixels = dark, ResidualInkPixels = residual });
            // Residual candidates are coarse alerts, not missing-feature assertions. No foreground is removed from raw crops.
            if (residual >= 12)
                local.Add(new() { Id = $"page-{pageNumber:D4}-residual-{tileNumber:D3}", PageNumber = pageNumber,
                    Kind = DrawingCandidateKind.ResidualInk, Bounds = box, Provider = "independent-raster-ink", GeometryHint = "unexplained_foreground_requires_visual_review",
                    Literal = $"{residual} foreground pixels were not accounted for by text boxes or primitive strokes; inspect original detail." });
        }
        var candidateBudget = Math.Min(MaximumCandidatesPerPage, Math.Max(0, MaximumCandidatesTotal-candidates.Count));
        if (local.Count > candidateBudget)
        {
            limitations.Add($"Candidate budget {candidateBudget} exceeded (page limit {MaximumCandidatesPerPage}, document limit {MaximumCandidatesTotal}); extraction cannot support a complete review.");
            complete = false;
            local = local.Take(candidateBudget).ToList();
        }
        if (localRegions[0].InkPixels == 0) limitations.Add("Blank raster page: inspect source overview and document its role.");
        if (observation.Diagnostics.Any(d => d.Code.StartsWith("ING-OCR-", StringComparison.Ordinal)))
            limitations.Add("OCR was unavailable or unreliable. Candidate recall is unknown; raw image review is required.");
        var retainedIds = local.Select(c=>c.Id).ToHashSet(StringComparer.Ordinal);
        for (var i = 0; i < local.Count; i++)
        {
            var candidate = local[i];
            var related = candidate.Kind == DrawingCandidateKind.Annotation
                ? local.Where(c => c.Id != candidate.Id && c.Kind == DrawingCandidateKind.Geometry && Distance(c.Bounds, candidate.Bounds) < .04)
                    .OrderBy(c => Distance(c.Bounds, candidate.Bounds)).Take(6).Select(c => c.Id).ToArray()
                : Array.Empty<string>();
            local[i] = candidate with { ReviewRegionIds = localRegions.Where(r => r.Purpose == "detail" && Intersects(r.Bounds, candidate.Bounds)).Select(r => r.Id).DefaultIfEmpty(overviewId).ToArray(),
                RelatedCandidateIds = candidate.RelatedCandidateIds.Concat(related).Where(retainedIds.Contains).Distinct().ToArray() };
        }
        var overlayPath = Path.Combine(reviewDir, "candidate-overview.png");
        var overlay = new RasterBuffer(width, height, (byte[])raw.Bgra.Clone());
        foreach (var candidate in local)
        {
            var b = candidate.Bounds;
            var color = candidate.Conflict ? (B: (byte)0, G: (byte)0, R: (byte)220) : candidate.Kind switch
            {
                DrawingCandidateKind.Annotation => (B: (byte)30, G: (byte)155, R: (byte)20),
                DrawingCandidateKind.ResidualInk => (B: (byte)0, G: (byte)140, R: (byte)240),
                _ => (B: (byte)210, G: (byte)90, R: (byte)20)
            };
            overlay.DrawRectangle((int)(b.Left*width), (int)(b.Top*height), (int)(b.Right*width), (int)(b.Bottom*height), color.B, color.G, color.R);
        }
        overlay.SavePng(overlayPath);
        pages.Add(new() { PageNumber = pageNumber, Ingested = true, ObservationPath = observationPath,
            ObservationSha256 = IngestionUtilities.Sha256File(observationPath), OverviewPath = overlayPath,
            PixelWidth = width, PixelHeight = height, Limitations = limitations });
        candidates.AddRange(local); regions.AddRange(localRegions);
    }

    public (string Path, string Sha256, int CandidateCount, int RegionCount) Save(string sourcePath, string sourceSha, int totalPages, string outputDirectory)
    {
        var inventory = new DrawingOmissionInventory
        {
            SourcePath = Path.GetFullPath(sourcePath), SourceSha256 = sourceSha, TotalPages = totalPages, CompleteExtraction = complete,
            Pages = Enumerable.Range(1, totalPages).Select(n => pages.FirstOrDefault(p => p.PageNumber == n) ?? new DrawingOmissionPage { PageNumber = n, Ingested = false }).ToArray(),
            Regions = regions, Candidates = candidates,
            Limitations = ["Detected candidates are not a complete feature inventory. Region review is an agent declaration, not proof of visual attention.",
                "Residual ink is heuristic localization, not semantic segmentation or exact line coverage.",
                "No automatic cross-view geometry proof, hidden-line equivalence, topology or GD&T certification."]
        };
        var path = Path.Combine(outputDirectory, "omission-inventory.json");
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            JsonSerializer.Serialize(stream, inventory, ModelingIrJson.Options);
        return (path, IngestionUtilities.Sha256File(path), candidates.Count, regions.Count);
    }

    public static IReadOnlyList<int> TileStarts(int length)
    {
        if (length < 1) throw new ArgumentOutOfRangeException(nameof(length));
        if (length <= TileSize) return [0];
        var result = new List<int>();
        for (var p = 0; p < length-TileSize; p += TileSize-TileOverlap) result.Add(p);
        result.Add(length-TileSize);
        return result.Distinct().Order().ToArray();
    }

    public static bool TryTransform(LocatedPoint2 point, string targetFrame, IReadOnlyList<TransformRecord> transforms, out (double X, double Y) result)
    {
        result = (point.X, point.Y);
        var queue = new Queue<(string Frame, double X, double Y)>(); queue.Enqueue((point.CoordinateFrameId, point.X, point.Y));
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (queue.TryDequeue(out var current))
        {
            if (!visited.Add(current.Frame)) continue;
            if (current.Frame == targetFrame) { result = (current.X, current.Y); return true; }
            foreach (var t in transforms)
            {
                var forward = t.FromFrameId == current.Frame;
                if (!forward && t.ToFrameId != current.Frame) continue;
                var m = forward ? t.ForwardMatrix : t.InverseMatrix;
                if (m.Count != 3 || m.Any(row => row.Count != 3 || row.Any(v => !double.IsFinite(v)))) continue;
                var w = m[2][0]*current.X + m[2][1]*current.Y + m[2][2];
                if (Math.Abs(w) < 1e-12) continue;
                queue.Enqueue((forward ? t.ToFrameId : t.FromFrameId,
                    (m[0][0]*current.X+m[0][1]*current.Y+m[0][2])/w, (m[1][0]*current.X+m[1][1]*current.Y+m[1][2])/w));
            }
        }
        return false;
    }

    private static DrawingReviewBox? Bounds((double X, double Y)[] points, int width, int height)
    {
        var left = Math.Clamp(points.Min(p => p.X)-1, 0, width); var top = Math.Clamp(points.Min(p => p.Y)-1, 0, height);
        var right = Math.Clamp(points.Max(p => p.X)+1, 0, width); var bottom = Math.Clamp(points.Max(p => p.Y)+1, 0, height);
        return right > left && bottom > top ? new(left/width, top/height, right/width, bottom/height) : null;
    }
    private static string Hint(ObservationEntity item) => item.CandidateProperties.GetValueOrDefault("dash_pattern", "solid") != "solid"
        ? "dashed_line_candidate" : item.Kind.ToString();
    private static bool Intersects(DrawingReviewBox a, DrawingReviewBox b) => a.Left < b.Right && b.Left < a.Right && a.Top < b.Bottom && b.Top < a.Bottom;
    private static double Distance(DrawingReviewBox a, DrawingReviewBox b) => Math.Sqrt(Math.Pow((a.Left+a.Right-b.Left-b.Right)/2, 2)+Math.Pow((a.Top+a.Bottom-b.Top-b.Bottom)/2, 2));

    private static void MarkObservation(byte[] mask, int width, int height, ObservationEntity item, (double X, double Y)[] points)
    {
        if (item.Kind == ObservationKind.Text)
        {
            if (string.IsNullOrWhiteSpace(item.RawLiteral)) return;
            var b = Bounds(points, width, height); if (b is null) return;
            for (var y = (int)(b.Top*height); y < Math.Min(height, (int)Math.Ceiling(b.Bottom*height)); y++)
                Array.Fill(mask, (byte)1, y*width+(int)(b.Left*width), Math.Min(width, (int)Math.Ceiling(b.Right*width))-(int)(b.Left*width));
            return;
        }
        // Cover only traced centerlines/curves with a small display tolerance. False candidates cannot erase entire interiors.
        if (item.Kind == ObservationKind.Circle)
        {
            var b = Bounds(points, width, height); if (b is null) return;
            var cx=(b.Left+b.Right)*width/2; var cy=(b.Top+b.Bottom)*height/2;
            var rx=(b.Right-b.Left)*width/2; var ry=(b.Bottom-b.Top)*height/2;
            var steps=Math.Clamp((int)Math.Ceiling(Math.PI*2*Math.Max(rx,ry)), 24, 20000);
            for(var i=0;i<steps;i++) Dot(mask,width,height,cx+rx*Math.Cos(i*Math.PI*2/steps),cy+ry*Math.Sin(i*Math.PI*2/steps));
        }
        else if (item.Kind == ObservationKind.Arc && points.Length is 3 or 4)
        {
            var span = points.Max(p=>p.X)-points.Min(p=>p.X)+points.Max(p=>p.Y)-points.Min(p=>p.Y);
            var steps=Math.Clamp((int)Math.Ceiling(span*2), 16, 20000);
            for(var i=0;i<=steps;i++)
            {
                var t=i/(double)steps; var u=1-t;
                if(points.Length==4) Dot(mask,width,height,u*u*u*points[0].X+3*u*u*t*points[1].X+3*u*t*t*points[2].X+t*t*t*points[3].X,
                    u*u*u*points[0].Y+3*u*u*t*points[1].Y+3*u*t*t*points[2].Y+t*t*t*points[3].Y);
                else Dot(mask,width,height,u*u*points[0].X+2*u*t*points[1].X+t*t*points[2].X,u*u*points[0].Y+2*u*t*points[1].Y+t*t*points[2].Y);
            }
        }
        else for (var i=1;i<points.Length;i++)
        {
            var a=points[i-1];var b=points[i];
            var steps=Math.Clamp((int)Math.Ceiling(Math.Max(Math.Abs(b.X-a.X),Math.Abs(b.Y-a.Y))),1,20000);
            for(var s=0;s<=steps;s++) Dot(mask,width,height,a.X+(b.X-a.X)*s/steps,a.Y+(b.Y-a.Y)*s/steps);
        }
    }
    private static void Dot(byte[] mask,int width,int height,double x,double y)
    {
        if(x < -3 || y < -3 || x > width+3 || y > height+3) return;
        var ix=(int)Math.Round(x);var iy=(int)Math.Round(y);
        for(var dy=-2;dy<=2;dy++) for(var dx=-2;dx<=2;dx++)
            if(ix+dx>=0&&ix+dx<width&&iy+dy>=0&&iy+dy<height) mask[(iy+dy)*width+ix+dx]=1;
    }
}
