using System.Globalization;
using System.IO;
using System.Text.Json;
using CadModeling.Drawing.Contracts;
using CadModeling.Drawing.Providers.Abstractions;

namespace CadModeling.Drawing.Ingestion;

public sealed class EngineeringDrawingIngestionService : IEngineeringDrawingIngestionService
{
    public const string ServiceName = "cad_ingest_engineering_drawing";
    public const string ServiceVersion = "1.0.0";
    private readonly IPdfNativeObservationProvider nativePdf;
    private readonly IPdfRasterizationProvider pdfRasterizer;
    private readonly IRasterInputProvider rasterInput;
    private readonly IRasterPreprocessor preprocessor;
    private readonly ILayoutObservationProvider layout;
    private readonly ITextObservationProvider text;
    private readonly IPrimitiveObservationProvider primitives;
    private readonly IObservationFusionService fusion;

    public EngineeringDrawingIngestionService(
        IPdfNativeObservationProvider? nativePdf = null,
        IPdfRasterizationProvider? pdfRasterizer = null,
        IRasterInputProvider? rasterInput = null,
        IRasterPreprocessor? preprocessor = null,
        ILayoutObservationProvider? layout = null,
        ITextObservationProvider? text = null,
        IPrimitiveObservationProvider? primitives = null,
        IObservationFusionService? fusion = null)
    {
        this.nativePdf = nativePdf ?? new PdfPigNativeObservationProvider();
        this.pdfRasterizer = pdfRasterizer ?? new PdftoppmRasterizationProvider();
        this.rasterInput = rasterInput ?? new WpfRasterInputProvider();
        this.preprocessor = preprocessor ?? new EngineeringRasterPreprocessor();
        this.layout = layout ?? new PageLayoutObservationProvider();
        this.text = text ?? new LocalTextObservationProvider();
        this.primitives = primitives ?? new DeterministicPrimitiveObservationProvider();
        this.fusion = fusion ?? new ObservationFusionService();
    }

    public async Task<DrawingIngestionResponse> IngestAsync(DrawingIngestionRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            ValidateRequest(request);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(request.Limits.MaximumRuntimeSeconds));
            var token = timeout.Token;
            var inputPath = Path.GetFullPath(request.InputPath);
            var inputKind = DetectInputKind(inputPath);
            var sourceSha = IngestionUtilities.Sha256File(inputPath);
            var pageCount = inputKind == DrawingInputKind.Pdf
                ? await nativePdf.GetPageCountAsync(inputPath, token)
                : await rasterInput.GetPageCountAsync(inputPath, inputKind, token);
            if (pageCount <= 0 || pageCount > request.Limits.MaximumPageCount)
                throw new IngestionRejectedException("ING-PAGE-LIMIT", $"Page count {pageCount} is outside the permitted range 1..{request.Limits.MaximumPageCount}.");
            var pageNumbers = request.PageNumbers.Count == 0
                ? Enumerable.Range(1, pageCount).ToArray()
                : request.PageNumbers.Distinct().OrderBy(number => number).ToArray();
            if (pageNumbers.Any(number => number < 1 || number > pageCount))
                throw new IngestionRejectedException("ING-PAGE-RANGE", $"Requested page numbers must be within 1..{pageCount}.");
            if (inputKind != DrawingInputKind.Pdf)
                foreach (var number in pageNumbers)
                {
                    var dimensions = RasterBuffer.Dimensions(inputPath, inputKind == DrawingInputKind.Tiff ? number - 1 : 0);
                    EnsurePixels(dimensions.Width, dimensions.Height, request.Limits);
                }

            var configurationHash = ConfigurationHash(request);
            var runId = IngestionUtilities.StableId("drawing-ingestion", sourceSha, configurationHash);
            var outputDirectory = CreateVersionedOutputDirectory(request.ArtifactRoot, Path.GetFileNameWithoutExtension(inputPath), sourceSha);
            var sourceCopy = Path.Combine(outputDirectory, "source" + Path.GetExtension(inputPath).ToLowerInvariant());
            File.Copy(inputPath, sourceCopy, overwrite: false);
            var createdAt = File.GetLastWriteTimeUtc(inputPath);
            var results = new List<PageIngestionResult>();
            var sourcePages = new List<SourcePage>();
            var globalDiagnostics = new List<IngestionDiagnostic>();
            var representations = new List<SourceRepresentation>();

            foreach (var pageNumber in pageNumbers)
            {
                token.ThrowIfCancellationRequested();
                var pageDirectory = Path.Combine(outputDirectory, $"page-{pageNumber:D4}");
                Directory.CreateDirectory(pageDirectory);
                var context = new ProviderPageContext
                {
                    SourcePath = inputPath,
                    SourceSha256 = sourceSha,
                    InputKind = inputKind,
                    PageNumber = pageNumber,
                    RenderDpi = request.RenderDpi,
                    PageArtifactDirectory = pageDirectory,
                    ProviderProfile = request.ProviderProfile,
                    DeterministicSeed = request.DeterministicSeed
                    ,ViewHints = request.ViewHints.Where(h=>h.PageNumber==pageNumber).ToArray()
                };
                NativePdfPageObservation? native = null;
                if (inputKind == DrawingInputKind.Pdf)
                    native = await nativePdf.ObservePageAsync(context, token);
                var sourceRaster = inputKind == DrawingInputKind.Pdf
                    ? await pdfRasterizer.RenderPageAsync(context, token)
                    : await rasterInput.LoadPageAsync(context, token);
                EnsurePixels(sourceRaster.PixelWidth, sourceRaster.PixelHeight, request.Limits);
                var processed = await preprocessor.PreprocessAsync(sourceRaster, request.PreprocessProfile, context, token);
                var batches = new List<ProviderObservationBatch>
                {
                    await layout.ObserveAsync(processed, context, token),
                    await text.ObserveAsync(processed, context, token),
                    await primitives.ObserveAsync(processed, context, token)
                };
                if (native is not null)
                    batches.Insert(0, NativeBatch(native, sourceCopy, sourceSha));
                var fused = fusion.Fuse(pageNumber, batches);
                var pageDiagnostics = fused.Diagnostics.Concat(processed.Diagnostics).ToArray();
                globalDiagnostics.AddRange(pageDiagnostics);
                var representation = native is null || native.Observations.Count == 0
                    ? SourceRepresentation.Raster
                    : SourceRepresentation.Hybrid;
                representations.Add(representation);

                var artifacts = InitialArtifacts(sourceCopy, sourceSha, sourceRaster, processed.NormalizedPage, pageNumber);
                var ledgerPath = Path.Combine(pageDirectory, "preprocess-ledger.json");
                WriteJsonNew(ledgerPath, processed.Ledger);
                artifacts.Add(Artifact("preprocess-ledger", ArtifactKind.JsonDocument, ledgerPath, "application/json", pageNumber));
                var overlayPath = Path.Combine(pageDirectory, "observation-overlay.png");
                WriteOverlay(processed.NormalizedPage, fused.Observations, overlayPath);
                artifacts.Add(Artifact("observation-overlay", ArtifactKind.Preview, overlayPath, "image/png", pageNumber));

                var frames = CoordinateFrames(pageNumber, sourceCopy, sourceRaster, processed.NormalizedPage, native);
                var transforms = processed.Transforms.Concat(native is null ? [] : new[] { PdfToRasterTransform(pageNumber, native, sourceRaster) }).ToArray();
                var contractDiagnostics = pageDiagnostics.Select((diagnostic, index) => new ContractDiagnostic
                {
                    Id = $"page-{pageNumber:D4}-ingestion-diagnostic-{index:D4}",
                    Code = diagnostic.Code,
                    Severity = diagnostic.Severity,
                    Blocking = diagnostic.Blocking,
                    Message = diagnostic.Message,
                    DocumentId = $"drawing-observation-page-{pageNumber:D4}",
                    FieldPath = "observations"
                }).ToArray();
                var unresolved = pageDiagnostics.Where(item => item.Code.StartsWith("ING-OCR-", StringComparison.Ordinal)).Select((item, index) => new UnresolvedItem
                {
                    Id = $"page-{pageNumber:D4}-ocr-gap-{index:D2}",
                    Category = "unreadable_text",
                    Blocking = false,
                    Reason = item.Message,
                    MinimumQuestion = "Readable OCR text is unavailable; inspect the source image or configure local OCR.",
                    SuggestedEvidence = "Original drawing image, native PDF text or local Tesseract language data."
                }).ToArray();
                var observation = new DrawingObservationDocument
                {
                    DocumentId = $"drawing-observation-page-{pageNumber:D4}",
                    SourceSha256 = sourceSha,
                    ProducerName = ServiceName,
                    ProducerVersion = ServiceVersion,
                    ConfigurationHash = configurationHash,
                    CreatedAt = createdAt,
                    Status = fused.Observations.Any(item => item.EvidenceStatus == EvidenceStatus.Conflict) ? DocumentStatus.Conflict : DocumentStatus.Candidate,
                    EvidenceStatus = fused.Observations.Any(item => item.EvidenceStatus == EvidenceStatus.Conflict) ? EvidenceStatus.Conflict :
                        fused.Observations.Count == 0 ? EvidenceStatus.Unreadable : EvidenceStatus.Candidate,
                    FactStatus = FactStatus.Unknown,
                    Diagnostics = contractDiagnostics,
                    UnresolvedItems = unresolved,
                    ArtifactManifest = artifacts.ToArray(),
                    CoordinateFrames = frames,
                    Transforms = transforms,
                    SourceRegions = fused.SourceRegions,
                    ViewRegions = fused.ViewRegions,
                    Observations = fused.Observations,
                    DimensionObservations = EngineeringDimensionParser.Extract(fused.Observations)
                };
                var observationPath = Path.Combine(pageDirectory, "observation.json");
                WriteJsonNew(observationPath, observation);
                artifacts.Add(Artifact("observation-json", ArtifactKind.JsonDocument, observationPath, "application/json", pageNumber));
                var providers = batches.Select(batch => batch.Provenance).Append(processed.Provenance)
                    .Concat(InputProviderProvenance(inputKind, request.ProviderProfile, request.DeterministicSeed))
                    .GroupBy(provider => $"{provider.ProviderName}|{provider.ConfigurationSha256}", StringComparer.Ordinal)
                    .Select(group => group.First()).OrderBy(provider => provider.ProviderName, StringComparer.Ordinal).ToArray();
                var artifactManifestPath = Path.Combine(pageDirectory, "artifact-manifest.json");
                WriteJsonNew(artifactManifestPath, new ArtifactManifestDocument
                {
                    SourceSha256 = sourceSha,
                    PageNumber = pageNumber,
                    Artifacts = artifacts,
                    Providers = providers
                });
                EnforceArtifactLimit(outputDirectory, request.Limits.MaximumArtifactBytes);
                sourcePages.Add(new()
                {
                    PageId = $"page-{pageNumber:D4}",
                    PageNumber = pageNumber,
                    ArtifactId = sourceRaster.ArtifactId,
                    Sha256 = sourceRaster.Sha256,
                    PixelWidth = sourceRaster.PixelWidth,
                    PixelHeight = sourceRaster.PixelHeight,
                    PhysicalWidth = native is null ? sourceRaster.PixelWidth / sourceRaster.Dpi * 25.4 : native.WidthPoints / 72d * 25.4,
                    PhysicalHeight = native is null ? sourceRaster.PixelHeight / sourceRaster.Dpi * 25.4 : native.HeightPoints / 72d * 25.4,
                    PhysicalUnit = MeasurementUnit.Millimeter,
                    Dpi = sourceRaster.Dpi,
                    Representation = representation
                });
                results.Add(new()
                {
                    PageNumber = pageNumber,
                    Observation = observation,
                    ObservationPath = observationPath,
                    PreprocessLedgerPath = ledgerPath,
                    ArtifactManifestPath = artifactManifestPath,
                    OverlayPath = overlayPath
                });
            }

            var sourceRepresentation = representations.All(value => value == SourceRepresentation.Raster)
                ? SourceRepresentation.Raster : SourceRepresentation.Hybrid;
            var sourceManifest = new DrawingSourceManifest
            {
                DocumentId = "drawing-source-manifest",
                SourceSha256 = sourceSha,
                ProducerName = ServiceName,
                ProducerVersion = ServiceVersion,
                ConfigurationHash = configurationHash,
                CreatedAt = createdAt,
                Status = DocumentStatus.Valid,
                EvidenceStatus = EvidenceStatus.Confirmed,
                FactStatus = FactStatus.Stated,
                SourcePath = inputPath,
                FileName = Path.GetFileName(inputPath),
                MediaType = MediaType(inputKind),
                Representation = sourceRepresentation,
                Pages = sourcePages
            };
            WriteJsonNew(Path.Combine(outputDirectory, "source-manifest.json"), sourceManifest);
            EnforceArtifactLimit(outputDirectory, request.Limits.MaximumArtifactBytes);
            return new()
            {
                Outcome = globalDiagnostics.Any(item => item.Severity == ContractDiagnosticSeverity.Warning)
                    ? IngestionOutcome.SucceededWithWarnings : IngestionOutcome.Succeeded,
                RunId = runId,
                OutputDirectory = outputDirectory,
                SourceManifest = sourceManifest,
                Pages = results,
                Diagnostics = globalDiagnostics
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new()
            {
                Outcome = IngestionOutcome.Rejected,
                Diagnostics = [new()
                {
                    Code = "ING-RUNTIME-LIMIT",
                    Severity = ContractDiagnosticSeverity.Error,
                    Blocking = true,
                    Message = $"Drawing ingestion exceeded the {request.Limits.MaximumRuntimeSeconds} second runtime limit."
                }]
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var rejected = exception as IngestionRejectedException;
            return new()
            {
                Outcome = IngestionOutcome.Rejected,
                Diagnostics = [new()
                {
                    Code = rejected?.Code ?? "ING-INPUT-REJECTED",
                    Severity = ContractDiagnosticSeverity.Error,
                    Blocking = true,
                    Message = exception.Message
                }]
            };
        }
    }

    private static ProviderObservationBatch NativeBatch(NativePdfPageObservation native, string sourceCopy, string sourceSha)
    {
        var frameId = $"page-{native.PageNumber:D4}-pdf-user-space";
        var regionId = $"page-{native.PageNumber:D4}-native-region";
        return new()
        {
            PageNumber = native.PageNumber,
            Modality = ObservationModality.NativePdf,
            SourceRegions = [new()
            {
                RegionId = regionId,
                ArtifactId = "source-copy",
                PageNumber = native.PageNumber,
                Polygon = [new() { CoordinateFrameId = frameId, X = 0, Y = 0 }, new() { CoordinateFrameId = frameId, X = native.WidthPoints, Y = 0 },
                    new() { CoordinateFrameId = frameId, X = native.WidthPoints, Y = native.HeightPoints }, new() { CoordinateFrameId = frameId, X = 0, Y = native.HeightPoints }],
                EvidenceStatus = EvidenceStatus.Candidate
            }],
            Observations = native.Observations,
            Provenance = native.Provenance,
            Diagnostics = native.Diagnostics
        };
    }

    private static List<ArtifactRecord> InitialArtifacts(string sourceCopy, string sourceSha, RasterPage sourceRaster, RasterPage normalized, int pageNumber) =>
        [new() { ArtifactId = "source-copy", Kind = ArtifactKind.SourceFile, Uri = sourceCopy, MediaType = MediaType(DetectInputKind(sourceCopy)), Sha256 = sourceSha },
         Artifact(sourceRaster.ArtifactId, ArtifactKind.SourcePage, sourceRaster.ImagePath, sourceRaster.MediaType, pageNumber),
         Artifact(normalized.ArtifactId, ArtifactKind.NormalizedPage, normalized.ImagePath, normalized.MediaType, pageNumber)];

    private static IReadOnlyList<CoordinateFrame> CoordinateFrames(int pageNumber, string sourceCopy, RasterPage source, RasterPage normalized, NativePdfPageObservation? native)
    {
        var frames = new List<CoordinateFrame>
        {
            new() { FrameId = source.CoordinateFrameId, Space = CoordinateSpace.SourcePixel, Unit = MeasurementUnit.Pixel, ArtifactId = source.ArtifactId, OriginDescription = "Top-left pixel center; x right, y down.", AxisLabels = ["x", "y"] },
            new() { FrameId = normalized.CoordinateFrameId, Space = CoordinateSpace.NormalizedPixel, Unit = MeasurementUnit.Pixel, ArtifactId = normalized.ArtifactId, OriginDescription = "Top-left pixel center after recorded preprocessing; x right, y down.", AxisLabels = ["x", "y"] }
        };
        if (native is not null)
            frames.Add(new() { FrameId = $"page-{pageNumber:D4}-pdf-user-space", Space = CoordinateSpace.SheetSpace, Unit = MeasurementUnit.PdfPoint,
                ArtifactId = "source-copy", OriginDescription = "Native PDF user space, preserved numerically in PDF points; bottom-left origin, x right, y up.", AxisLabels = ["x_pdf_point", "y_pdf_point"] });
        return frames;
    }

    private static TransformRecord PdfToRasterTransform(int pageNumber, NativePdfPageObservation native, RasterPage raster)
    {
        var scale = raster.Dpi / 72d;
        return new()
        {
            TransformId = $"page-{pageNumber:D4}-pdf-to-rendered-pixel",
            FromFrameId = $"page-{pageNumber:D4}-pdf-user-space",
            ToFrameId = raster.CoordinateFrameId,
            ForwardMatrix = [new[] { scale, 0d, 0d }, new[] { 0d, -scale, native.HeightPoints * scale }, new[] { 0d, 0d, 1d }],
            InverseMatrix = [new[] { 1d / scale, 0d, 0d }, new[] { 0d, -1d / scale, native.HeightPoints }, new[] { 0d, 0d, 1d }],
            InputUnit = MeasurementUnit.PdfPoint,
            OutputUnit = MeasurementUnit.Pixel,
            Parameters = new SortedDictionary<string, double>(StringComparer.Ordinal) { ["pdf_points_per_inch"] = 72, ["render_dpi"] = raster.Dpi },
            ErrorEstimate = 1,
            ErrorUnit = MeasurementUnit.Pixel,
            Source = "pdf-render-coordinate-contract",
            BeforeArtifactId = "source-copy",
            AfterArtifactId = raster.ArtifactId
        };
    }

    private static void WriteOverlay(RasterPage page, IReadOnlyList<ObservationEntity> observations, string path)
    {
        var buffer = RasterBuffer.Load(page.ImagePath);
        foreach (var observation in observations.Take(3000))
        {
            var points = observation.Geometry.Where(point => point.CoordinateFrameId == page.CoordinateFrameId).ToArray();
            if (points.Length == 0 && observation.CandidateProperties.TryGetValue("fusion_bbox", out var boxText))
            {
                var values = boxText.Split(',').Select(value => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : double.NaN).ToArray();
                if (values.Length == 4 && values.All(double.IsFinite))
                    points = [new() { CoordinateFrameId = page.CoordinateFrameId, X = values[0], Y = values[1] }, new() { CoordinateFrameId = page.CoordinateFrameId, X = values[2], Y = values[3] }];
            }
            if (points.Length == 0) continue;
            var left = (int)Math.Floor(points.Min(point => point.X)); var right = (int)Math.Ceiling(points.Max(point => point.X));
            var top = (int)Math.Floor(points.Min(point => point.Y)); var bottom = (int)Math.Ceiling(points.Max(point => point.Y));
            if (observation.EvidenceStatus == EvidenceStatus.Conflict) buffer.DrawRectangle(left, top, right, bottom, 0, 0, 255);
            else if (observation.Kind == ObservationKind.Text) buffer.DrawRectangle(left, top, right, bottom, 0, 160, 0);
            else buffer.DrawRectangle(left, top, right, bottom, 255, 100, 0);
        }
        buffer.SavePng(path);
    }

    private static ArtifactRecord Artifact(string id, ArtifactKind kind, string path, string mediaType, int? pageNumber) => new()
    {
        ArtifactId = id,
        Kind = kind,
        Uri = Path.GetFullPath(path),
        MediaType = mediaType,
        Sha256 = IngestionUtilities.Sha256File(path),
        PageNumber = pageNumber
    };

    private static void WriteJsonNew<T>(string path, T value)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false));
        writer.Write(DrawingContractJson.Serialize(value));
    }

    private static void ValidateRequest(DrawingIngestionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.InputPath) || !File.Exists(request.InputPath))
            throw new IngestionRejectedException("ING-INPUT-MISSING", "Input_path must identify an existing local file.");
        if (new FileInfo(request.InputPath).Length > request.Limits.MaximumFileBytes)
            throw new IngestionRejectedException("ING-FILE-LIMIT", $"Input exceeds the {request.Limits.MaximumFileBytes} byte limit.");
        if (request.RenderDpi < request.Limits.MinimumDpi || request.RenderDpi > request.Limits.MaximumDpi)
            throw new IngestionRejectedException("ING-DPI-LIMIT", $"Render DPI must be within {request.Limits.MinimumDpi}..{request.Limits.MaximumDpi}.");
        if (string.IsNullOrWhiteSpace(request.ArtifactRoot))
            throw new IngestionRejectedException("ING-ARTIFACT-ROOT", "Artifact_root is required.");
        _ = Path.GetFullPath(request.ArtifactRoot);
    }

    private static DrawingInputKind DetectInputKind(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        using var stream = File.OpenRead(path);
        var header = new byte[8];
        var read = stream.Read(header, 0, header.Length);
        bool Starts(params byte[] bytes) => read >= bytes.Length && header.AsSpan(0, bytes.Length).SequenceEqual(bytes);
        var detected = Starts(0x25, 0x50, 0x44, 0x46, 0x2D) ? DrawingInputKind.Pdf :
            Starts(0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A) ? DrawingInputKind.Png :
            Starts(0xFF, 0xD8, 0xFF) ? DrawingInputKind.Jpeg :
            Starts(0x42, 0x4D) ? DrawingInputKind.Bmp :
            Starts(0x49, 0x49, 0x2A, 0x00) || Starts(0x4D, 0x4D, 0x00, 0x2A) ? DrawingInputKind.Tiff :
            throw new IngestionRejectedException("ING-MEDIA-SIGNATURE", "Unsupported or corrupt drawing media signature.");
        var allowedExtensions = detected switch
        {
            DrawingInputKind.Pdf => new[] { ".pdf" }, DrawingInputKind.Png => new[] { ".png" },
            DrawingInputKind.Jpeg => new[] { ".jpg", ".jpeg" }, DrawingInputKind.Bmp => new[] { ".bmp" },
            DrawingInputKind.Tiff => new[] { ".tif", ".tiff" }, _ => []
        };
        if (!allowedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            throw new IngestionRejectedException("ING-MEDIA-MISMATCH", $"File extension '{extension}' does not match detected media '{detected}'.");
        return detected;
    }

    private static string MediaType(DrawingInputKind kind) => kind switch
    {
        DrawingInputKind.Pdf => "application/pdf", DrawingInputKind.Png => "image/png", DrawingInputKind.Jpeg => "image/jpeg",
        DrawingInputKind.Bmp => "image/bmp", DrawingInputKind.Tiff => "image/tiff", _ => "application/octet-stream"
    };
    private IEnumerable<ProviderProvenance> InputProviderProvenance(DrawingInputKind kind, string profile, int seed)
    {
        if (kind == DrawingInputKind.Pdf && pdfRasterizer is PdftoppmRasterizationProvider pdf) yield return pdf.GetProvenance(profile, seed);
        if (kind != DrawingInputKind.Pdf && rasterInput is WpfRasterInputProvider raster) yield return raster.GetProvenance(profile, seed);
    }
    private static string ConfigurationHash(DrawingIngestionRequest request) => IngestionUtilities.Sha256Text(JsonSerializer.Serialize(new
    {
        request.PageNumbers, request.RenderDpi, request.PreprocessProfile, request.ProviderProfile, request.DeterministicSeed, request.Limits, request.ViewHints
    }, DrawingContractJson.Options));
    private static string CreateVersionedOutputDirectory(string root, string sourceName, string sha)
    {
        var family = Path.Combine(Path.GetFullPath(root), $"{Sanitize(sourceName)}-{sha[..12]}");
        Directory.CreateDirectory(family);
        for (var version = 1; version < 10000; version++)
        {
            var candidate = Path.Combine(family, $"run-{version:D4}");
            if (Directory.Exists(candidate)) continue;
            Directory.CreateDirectory(candidate);
            return candidate;
        }
        throw new IOException("No available versioned ingestion output directory remained.");
    }
    private static string Sanitize(string value) => string.Concat(value.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
    private static void EnsurePixels(int width, int height, DrawingIngestionLimits limits)
    {
        if (width <= 0 || height <= 0 || (long)width * height > limits.MaximumPixelsPerPage)
            throw new IngestionRejectedException("ING-PIXEL-LIMIT", $"Page dimensions {width}x{height} exceed the {limits.MaximumPixelsPerPage} pixel limit.");
    }
    private static void EnforceArtifactLimit(string directory, long maximumBytes)
    {
        var total = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length);
        if (total > maximumBytes) throw new IngestionRejectedException("ING-ARTIFACT-LIMIT", $"Generated artifacts exceed the {maximumBytes} byte limit.");
    }
}

internal sealed class IngestionRejectedException(string code, string message) : IOException(message)
{
    public string Code { get; } = code;
}
