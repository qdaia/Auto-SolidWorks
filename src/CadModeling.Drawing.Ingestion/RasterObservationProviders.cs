using System.Globalization;
using System.IO;
using CadModeling.Drawing.Contracts;
using CadModeling.Drawing.Providers.Abstractions;

namespace CadModeling.Drawing.Ingestion;

public sealed class PageLayoutObservationProvider : ILayoutObservationProvider
{
    public Task<ProviderObservationBatch> ObserveAsync(RasterPreprocessResult page, ProviderPageContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var raster = page.NormalizedPage;
        var regionId = $"page-{raster.PageNumber:D4}-raster-region";
        var region = new SourceRegion
        {
            RegionId = regionId,
            ArtifactId = raster.ArtifactId,
            PageNumber = raster.PageNumber,
            Polygon = Rectangle(raster.CoordinateFrameId, 0, 0, raster.PixelWidth - 1, raster.PixelHeight - 1),
            EvidenceStatus = EvidenceStatus.Candidate
        };
        var regions=new List<SourceRegion> { region };
        var views=new List<ViewRegionObservation>();
        foreach(var hint in context.ViewHints)
        {
            if(string.IsNullOrWhiteSpace(hint.Id) || !double.IsFinite(hint.Left+hint.Top+hint.Right+hint.Bottom) || hint.Left<0 || hint.Top<0 || hint.Right>1 || hint.Bottom>1 || hint.Right<=hint.Left || hint.Bottom<=hint.Top || hint.OcrRotationClockwise is not (0 or 90 or 180 or 270) || hint.OcrPageSegmentationMode is not (6 or 7 or 11 or 13))
                throw new ArgumentException("Drawing regions require unique IDs, ordered bounds in [0,1], and OCR rotation 0/90/180/270.");
            var id=$"page-{raster.PageNumber:D4}-view-{hint.Id}";
            if(regions.Any(r=>r.RegionId==id)) throw new ArgumentException("Drawing view IDs must be unique on each page.");
            regions.Add(region with { RegionId=id,Polygon=Rectangle(raster.CoordinateFrameId,hint.Left*raster.PixelWidth,hint.Top*raster.PixelHeight,hint.Right*raster.PixelWidth,hint.Bottom*raster.PixelHeight) });
            views.Add(new() { ViewRegionId=id,SourceRegionId=id,ViewTypeHint=hint.ViewType,AssignmentBasis="agent_image_interpretation" });
        }
        if(views.Count==0) views.Add(new() { ViewRegionId=$"page-{raster.PageNumber:D4}-layout-candidate",SourceRegionId=regionId });
        return Task.FromResult(new ProviderObservationBatch
        {
            PageNumber = raster.PageNumber,
            Modality = ObservationModality.RasterLayout,
            SourceRegions = regions,
            ViewRegions = views,
            Provenance = IngestionUtilities.Provenance("page-layout-candidate", "1.0.0", context.ProviderProfile, context.DeterministicSeed)
        });
    }

    internal static IReadOnlyList<LocatedPoint2> Rectangle(string frameId, double left, double top, double right, double bottom) =>
        [new() { CoordinateFrameId = frameId, X = left, Y = top }, new() { CoordinateFrameId = frameId, X = right, Y = top }, new() { CoordinateFrameId = frameId, X = right, Y = bottom }, new() { CoordinateFrameId = frameId, X = left, Y = bottom }];
}

public sealed class LocalTextObservationProvider : ITextObservationProvider
{
    private readonly LocalOcrProviderConfiguration configuration;

    public LocalTextObservationProvider(string? executable = null, string? tessdataDirectory = null, string? language = null)
    {
        configuration = LocalOcrProviderConfiguration.FromEnvironment(executable, tessdataDirectory, language);
    }

    public LocalTextObservationProvider(LocalOcrProviderConfiguration configuration) =>
        this.configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));

    public LocalOcrConfigurationCheck CheckConfiguration() => LocalOcrConfigurationChecker.Check(configuration);

    public async Task<ProviderObservationBatch> ObserveAsync(RasterPreprocessResult page, ProviderPageContext context, CancellationToken cancellationToken = default)
    {
        var check = CheckConfiguration();
        if (!check.Ready)
            return ObserveUnreadableGlyphCandidates(page, context, cancellationToken, check.Code, check.Message,
                IngestionUtilities.Provenance("tesseract-local", check.ProviderVersion, check.ConfigurationFingerprint,
                    context.DeterministicSeed, check.CombinedModelSha256, check.BinarySha256));
        try
        {
            return await ObserveWithTesseractAsync(page, context, check, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ObserveUnreadableGlyphCandidates(page, context, cancellationToken, "ING-OCR-TIMEOUT",
                $"Local OCR exceeded the configured {configuration.TimeoutSeconds} second provider timeout and was terminated.",
                Provenance(check, context));
        }
        catch (InvalidDataException exception)
        {
            var code = exception.Message.StartsWith("ING-OCR-MODEL-INVALID:", StringComparison.Ordinal)
                ? "ING-OCR-MODEL-INVALID"
                : exception.Message.StartsWith("ING-OCR-PROCESS-FAILED:", StringComparison.Ordinal)
                    ? "ING-OCR-PROCESS-FAILED"
                    : "ING-OCR-PROVIDER-FAILED";
            return ObserveUnreadableGlyphCandidates(page, context, cancellationToken, code,
                $"Local OCR failed closed: {exception.Message}", Provenance(check, context));
        }
        catch (IOException exception)
        {
            return ObserveUnreadableGlyphCandidates(page, context, cancellationToken, "ING-OCR-PROVIDER-FAILED",
                $"Local OCR failed closed: {exception.Message}", Provenance(check, context));
        }
    }

    private async Task<ProviderObservationBatch> ObserveWithTesseractAsync(
        RasterPreprocessResult page,
        ProviderPageContext context,
        LocalOcrConfigurationCheck check,
        CancellationToken cancellationToken)
    {
        var input=Path.Combine(context.PageArtifactDirectory,"ocr-source.png");
        if(!File.Exists(input)) input=page.NormalizedPage.ImagePath;
        var raster=RasterBuffer.Load(input);
        var regions=new List<DrawingViewRegionHint> { new() { Id="page" } };
        regions.AddRange(context.ViewHints);
        if(regions.Count>17) throw new ArgumentException("At most 16 OCR view/annotation regions are supported per page.");
        var observations=new List<ObservationEntity>();
        for(var tileIndex=0;tileIndex<regions.Count;tileIndex++)
        {
            var region=regions[tileIndex];
            var left=(int)Math.Floor(region.Left*raster.Width);var top=(int)Math.Floor(region.Top*raster.Height);
            var width=(int)Math.Ceiling(region.Right*raster.Width)-left;var height=(int)Math.Ceiling(region.Bottom*raster.Height)-top;
            var tile=raster.Crop(left,top,width,height);
            for(var rotation=0;rotation<region.OcrRotationClockwise;rotation+=90) tile=tile.RotateClockwise();
            var scale=Math.Clamp(1800d/Math.Max(tile.Width,tile.Height),1,4);
            tile=tile.Scale(scale);
            var tilePath=Path.Combine(context.PageArtifactDirectory,$"ocr-tile-{tileIndex:D2}.png");tile.SavePng(tilePath);
            var args=new List<string> { tilePath,"stdout","--tessdata-dir",check.TessdataDirectory!,"-l",configuration.Language,
                "--oem",configuration.OcrEngineMode.ToString(CultureInfo.InvariantCulture),"--psm",(tileIndex==0?configuration.PageSegmentationMode:region.OcrPageSegmentationMode).ToString(CultureInfo.InvariantCulture),
                "-c","preserve_interword_spaces=1","tsv" };
            var result=await IngestionUtilities.RunProcessAsync(check.ExecutablePath!,args,configuration.TimeoutSeconds,cancellationToken);
            if(result.ExitCode!=0) throw new InvalidDataException($"ING-OCR-PROCESS-FAILED: exit={result.ExitCode}; {result.StandardError.Trim()}");
            LocatedPoint2 Back(double x,double y)
            {
                x/=scale;y/=scale;
                (x,y)=region.OcrRotationClockwise switch { 90=>(y,height-x),180=>(width-x,height-y),270=>(width-y,x),_=>(x,y) };
                return new() { CoordinateFrameId=page.NormalizedPage.CoordinateFrameId,X=left+x,Y=top+y };
            }
            foreach(var word in TesseractTsvParser.Parse(result.StandardOutput))
            {
                var corners=new[] {Back(word.Left,word.Top),Back(word.Left+word.Width,word.Top),Back(word.Left+word.Width,word.Top+word.Height),Back(word.Left,word.Top+word.Height)};
                observations.Add(new() {
                    ObservationId=IngestionUtilities.StableId("obs-ocr-text",context.SourceSha256,context.PageNumber,tileIndex,word.Left,word.Top,word.Text),
                    SourceRegionId=tileIndex==0?$"page-{context.PageNumber:D4}-raster-region":$"page-{context.PageNumber:D4}-view-{region.Id}",
                    Kind=ObservationKind.Text,RawLiteral=word.Text,Geometry=corners,EvidenceStatus=EvidenceStatus.Candidate,
                    Confidence=Math.Clamp(word.ConfidencePercent/100d,0,.95),
                    CandidateProperties=Props(("modality","raster_ocr"),("provider","tesseract-local"),("language",configuration.Language),
                        ("ocr_rotation_clockwise",region.OcrRotationClockwise.ToString(CultureInfo.InvariantCulture)),
                        ("fusion_bbox",Box((int)corners.Min(p=>p.X),(int)corners.Min(p=>p.Y),(int)corners.Max(p=>p.X),(int)corners.Max(p=>p.Y))),
                        ("engineering_dimension","unbound_candidate_only"))
                });
            }
        }
        var diagnostics=new List<IngestionDiagnostic>();
        if(observations.Count==0) diagnostics.Add(new() { Code="ING-OCR-NO-TEXT",Severity=ContractDiagnosticSeverity.Warning,Message="Local OCR returned no readable text candidates.",PageNumber=context.PageNumber,ProviderName="tesseract-local" });
        else if(observations.Average(o=>o.Confidence)<.65 || Math.Max(raster.Width,raster.Height)<1000)
            diagnostics.Add(new() { Code="ING-OCR-LOW-RELIABILITY",Severity=ContractDiagnosticSeverity.Warning,Message="Low-resolution or low-confidence OCR: inspect the source image before using numeric candidates. Cropping/upscaling does not recover missing detail.",PageNumber=context.PageNumber,ProviderName="tesseract-local" });
        return new() { PageNumber=context.PageNumber,Modality=ObservationModality.RasterText,Observations=observations,Provenance=Provenance(check,context),Diagnostics=diagnostics };
    }

    private static ProviderObservationBatch ObserveUnreadableGlyphCandidates(
        RasterPreprocessResult page,
        ProviderPageContext context,
        CancellationToken cancellationToken,
        string diagnosticCode,
        string diagnosticMessage,
        ProviderProvenance provenance)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var buffer = RasterBuffer.Load(page.NormalizedPage.ImagePath);
        var binary = buffer.ToBinaryOtsu(out _);
        var components = BinaryComponents.Find(binary, buffer.Width, buffer.Height, 4)
            .Where(c => c.Width >= 2 && c.Height >= 4 && c.Width <= buffer.Width / 8 && c.Height <= buffer.Height / 5)
            .ToArray();
        var groups = new List<List<PixelComponent>>();
        foreach (var component in components)
        {
            var group = groups.FirstOrDefault(candidate =>
                Math.Abs(candidate.Average(item => (item.Top + item.Bottom) / 2d) - (component.Top + component.Bottom) / 2d) <= Math.Max(4, component.Height) &&
                component.Left - candidate.Max(item => item.Right) <= Math.Max(20, component.Height * 3));
            if (group is null) groups.Add([component]); else group.Add(component);
        }
        var regionId = $"page-{context.PageNumber:D4}-raster-region";
        var observations = groups.Where(group => group.Count >= 2).Take(250).Select((group, index) => new ObservationEntity
        {
            ObservationId = IngestionUtilities.StableId("obs-unreadable-text", context.SourceSha256, context.PageNumber, index,
                group.Min(c => c.Left), group.Min(c => c.Top), group.Max(c => c.Right), group.Max(c => c.Bottom)),
            SourceRegionId = regionId,
            Kind = ObservationKind.Text,
            RawLiteral = string.Empty,
            Geometry = PageLayoutObservationProvider.Rectangle(page.NormalizedPage.CoordinateFrameId,
                group.Min(c => c.Left), group.Min(c => c.Top), group.Max(c => c.Right), group.Max(c => c.Bottom)),
            EvidenceStatus = EvidenceStatus.Unreadable,
            Confidence = 0.1,
            CandidateProperties = Props(("modality", "raster_component_cluster"), ("provider", "conservative-glyph-cluster"),
                ("fusion_bbox", Box(group.Min(c => c.Left), group.Min(c => c.Top), group.Max(c => c.Right), group.Max(c => c.Bottom))),
                ("reason", "no_reviewed_local_ocr_model"), ("engineering_dimension", "not_available"))
        }).ToList();
        if (observations.Count == 0)
            observations.Add(new()
            {
                ObservationId = IngestionUtilities.StableId("obs-unreadable-text-page", context.SourceSha256, context.PageNumber),
                SourceRegionId = regionId,
                Kind = ObservationKind.Text,
                RawLiteral = string.Empty,
                Geometry = PageLayoutObservationProvider.Rectangle(page.NormalizedPage.CoordinateFrameId, 0, 0,
                    page.NormalizedPage.PixelWidth - 1, page.NormalizedPage.PixelHeight - 1),
                EvidenceStatus = EvidenceStatus.Unreadable,
                Confidence = 0.05,
                CandidateProperties = Props(("modality", "raster_component_cluster"), ("provider", "conservative-glyph-cluster"),
                    ("fusion_bbox", Box(0, 0, page.NormalizedPage.PixelWidth - 1, page.NormalizedPage.PixelHeight - 1)),
                    ("reason", "no_reviewed_local_ocr_model_and_no_stable_glyph_cluster"), ("engineering_dimension", "not_available"))
            });
        return new()
        {
            PageNumber = context.PageNumber,
            Modality = ObservationModality.RasterText,
            Observations = observations,
            Provenance = provenance,
            Diagnostics = [new()
            {
                Code = diagnosticCode,
                Severity = ContractDiagnosticSeverity.Warning,
                Message = diagnosticMessage + " Glyph clusters are retained as unreadable low-confidence text candidates; no numeric value is inferred.",
                PageNumber = context.PageNumber,
                ProviderName = provenance.ProviderName
            }]
        };
    }

    private static ProviderProvenance Provenance(LocalOcrConfigurationCheck check, ProviderPageContext context) =>
        IngestionUtilities.Provenance("tesseract-local", check.ProviderVersion,
            $"{context.ProviderProfile}|{check.ConfigurationFingerprint}", context.DeterministicSeed,
            check.CombinedModelSha256, check.BinarySha256);
    private static IReadOnlyDictionary<string, string> Props(params (string Key, string Value)[] values) =>
        new SortedDictionary<string, string>(values.ToDictionary(v => v.Key, v => v.Value, StringComparer.Ordinal), StringComparer.Ordinal);
    private static string Box(double left, double top, double right, double bottom) =>
        string.Join(",", new[] { left, top, right, bottom }.Select(value => value.ToString("R", CultureInfo.InvariantCulture)));
}

public sealed class DeterministicPrimitiveObservationProvider : IPrimitiveObservationProvider
{
    public Task<ProviderObservationBatch> ObserveAsync(RasterPreprocessResult page, ProviderPageContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var raster = page.NormalizedPage;
        var buffer = RasterBuffer.Load(raster.ImagePath);
        var binary = buffer.ToBinaryOtsu(out _);
        var regionId = $"page-{context.PageNumber:D4}-raster-region";
        var observations = new List<ObservationEntity>();
        FindAxisLines(binary, buffer.Width, buffer.Height, horizontal: true, observations, context, raster.CoordinateFrameId, regionId);
        FindAxisLines(binary, buffer.Width, buffer.Height, horizontal: false, observations, context, raster.CoordinateFrameId, regionId);
        var components = BinaryComponents.Find(binary, buffer.Width, buffer.Height, 8);
        var componentIndex = 0;
        foreach (var component in components)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var aspect = component.Width / (double)Math.Max(1, component.Height);
            var density = component.Count / (double)(component.Width * component.Height);
            var radial = RadialScore(binary, buffer.Width, component);
            ObservationKind? kind = null;
            var primitive = string.Empty;
            var confidence = 0.0;
            if (component.Width >= 14 && component.Height >= 14 && aspect is >= 0.72 and <= 1.38 && radial >= 0.62)
            {
                kind = component.Quadrants == 15 ? ObservationKind.Circle : ObservationKind.Arc;
                primitive = kind == ObservationKind.Circle ? "circle_candidate" : "arc_candidate";
                confidence = Math.Min(0.88, 0.45 + radial * 0.4);
            }
            else if (component.Width is >= 3 and <= 80 && component.Height is >= 3 and <= 80 && aspect is >= 0.25 and <= 4 && density > 0.08)
            {
                kind = ObservationKind.Symbol;
                primitive = "connected_symbol_candidate";
                confidence = 0.3;
            }
            if (kind is null) { componentIndex++; continue; }
            observations.Add(new()
            {
                ObservationId = IngestionUtilities.StableId("obs-raster-component", context.SourceSha256, context.PageNumber, componentIndex, kind, component.Left, component.Top),
                SourceRegionId = regionId,
                Kind = kind.Value,
                Geometry = PageLayoutObservationProvider.Rectangle(raster.CoordinateFrameId, component.Left, component.Top, component.Right, component.Bottom),
                EvidenceStatus = EvidenceStatus.Candidate,
                Confidence = confidence,
                CandidateProperties = Props(("modality", "raster_primitive"), ("provider", "deterministic-primitive"),
                    ("primitive", primitive), ("pixel_density", density.ToString("R", CultureInfo.InvariantCulture)),
                    ("radial_score", radial.ToString("R", CultureInfo.InvariantCulture)), ("engineering_dimension", "not_inferred"))
            });
            componentIndex++;
        }
        return Task.FromResult(new ProviderObservationBatch
        {
            PageNumber = context.PageNumber,
            Modality = ObservationModality.RasterPrimitive,
            Observations = observations.OrderBy(item => item.ObservationId, StringComparer.Ordinal).ToArray(),
            Provenance = IngestionUtilities.Provenance("deterministic-primitive", "1.0.0", context.ProviderProfile, context.DeterministicSeed)
        });
    }

    private static void FindAxisLines(byte[] binary, int width, int height, bool horizontal,
        List<ObservationEntity> output, ProviderPageContext context, string frameId, string regionId)
    {
        var outer = horizontal ? height : width;
        var inner = horizontal ? width : height;
        var minimum = Math.Max(12, inner / 25);
        var accepted = new List<(int Fixed, int Start, int End)>();
        for (var fixedAxis = 0; fixedAxis < outer; fixedAxis++)
        {
            var runStart = -1;
            for (var moving = 0; moving <= inner; moving++)
            {
                var dark = moving < inner && binary[horizontal ? fixedAxis * width + moving : moving * width + fixedAxis] != 0;
                if (dark && runStart < 0) runStart = moving;
                if (dark || runStart < 0) continue;
                var end = moving - 1;
                if (end - runStart + 1 >= minimum && !accepted.Any(line => Math.Abs(line.Fixed - fixedAxis) <= 2 && Math.Abs(line.Start - runStart) <= 4 && Math.Abs(line.End - end) <= 4))
                    accepted.Add((fixedAxis, runStart, end));
                runStart = -1;
            }
            if (accepted.Count >= 500) break;
        }
        foreach (var (fixedAxis, start, end) in accepted)
        {
            var points = horizontal
                ? new[] { new LocatedPoint2 { CoordinateFrameId = frameId, X = start, Y = fixedAxis }, new LocatedPoint2 { CoordinateFrameId = frameId, X = end, Y = fixedAxis } }
                : new[] { new LocatedPoint2 { CoordinateFrameId = frameId, X = fixedAxis, Y = start }, new LocatedPoint2 { CoordinateFrameId = frameId, X = fixedAxis, Y = end } };
            output.Add(new()
            {
                ObservationId = IngestionUtilities.StableId("obs-raster-line", context.SourceSha256, context.PageNumber, horizontal, fixedAxis, start, end),
                SourceRegionId = regionId,
                Kind = ObservationKind.VisibleLine,
                Geometry = points,
                EvidenceStatus = EvidenceStatus.Candidate,
                Confidence = 0.72,
                CandidateProperties = Props(("modality", "raster_primitive"), ("provider", "deterministic-axis-run"),
                    ("primitive", "line_candidate"), ("orientation", horizontal ? "horizontal" : "vertical"),
                    ("engineering_dimension", "not_inferred"))
            });
        }
    }

    private static double RadialScore(byte[] binary, int width, PixelComponent component)
    {
        var cx = (component.Left + component.Right) / 2d;
        var cy = (component.Top + component.Bottom) / 2d;
        var rx = component.Width / 2d;
        var ry = component.Height / 2d;
        if (rx < 1 || ry < 1) return 0;
        var ring = 0; var dark = 0;
        for (var y = component.Top; y <= component.Bottom; y++)
        for (var x = component.Left; x <= component.Right; x++)
        {
            var normalized = Math.Sqrt(Math.Pow((x - cx) / rx, 2) + Math.Pow((y - cy) / ry, 2));
            if (normalized is < 0.72 or > 1.28) continue;
            ring++;
            if (binary[y * width + x] != 0) dark++;
        }
        return ring == 0 ? 0 : dark / (double)ring;
    }

    private static IReadOnlyDictionary<string, string> Props(params (string Key, string Value)[] values) =>
        new SortedDictionary<string, string>(values.ToDictionary(v => v.Key, v => v.Value, StringComparer.Ordinal), StringComparer.Ordinal);
}
