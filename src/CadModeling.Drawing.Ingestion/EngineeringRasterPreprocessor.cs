using System.Globalization;
using System.IO;
using CadModeling.Drawing.Contracts;
using CadModeling.Drawing.Providers.Abstractions;

namespace CadModeling.Drawing.Ingestion;

public sealed class EngineeringRasterPreprocessor : IRasterPreprocessor
{
    public const string ProviderName = "engineering-raster-preprocessor";
    public const string ProviderVersion = "1.0.0";

    public Task<RasterPreprocessResult> PreprocessAsync(RasterPage page, string profile, ProviderPageContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = RasterBuffer.Load(page.ImagePath);
        var normalized = source;
        var rotate = profile.Contains("auto-orient", StringComparison.OrdinalIgnoreCase) && source.Height > source.Width;
        if (rotate) normalized = source.RotateClockwise();
        // OCR retains anti-aliased glyphs; binary geometry detection uses its own image.
        normalized.SavePng(Path.Combine(context.PageArtifactDirectory,"ocr-source.png"));
        var binary = normalized.ToBinaryOtsu(out var threshold);
        Denoise(binary, normalized.Width, normalized.Height);
        var darkRatio = binary.Count(value => value != 0) / (double)binary.Length;
        var bw = ToBlackAndWhite(binary, normalized.Width, normalized.Height);
        var outputPath = Path.Combine(context.PageArtifactDirectory, "normalized-page.png");
        bw.SavePng(outputPath);
        var normalizedArtifactId = $"page-{page.PageNumber:D4}-normalized-raster";
        var normalizedFrameId = $"page-{page.PageNumber:D4}-normalized-pixel";
        var forward = rotate ? ClockwiseMatrix(source.Height) : IngestionUtilities.IdentityMatrix();
        var inverse = rotate ? CounterClockwiseMatrix(source.Height) : IngestionUtilities.IdentityMatrix();
        var parameters = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["rotation_degrees_clockwise"] = rotate ? "90" : "0",
            ["deskew_degrees"] = "0",
            ["crop"] = "none",
            ["perspective"] = "none",
            ["threshold_method"] = "otsu",
            ["threshold_value"] = threshold.ToString(CultureInfo.InvariantCulture),
            ["denoise"] = "isolated_pixel_8_neighbour"
        };
        var provenance = IngestionUtilities.Provenance(ProviderName, ProviderVersion,
            $"{profile}|{string.Join(";", parameters.Select(item => $"{item.Key}={item.Value}"))}", context.DeterministicSeed);
        var steps = new List<PreprocessStep>
        {
            Step(1, "decode", page.ArtifactId, page.ArtifactId, IngestionUtilities.IdentityMatrix(), IngestionUtilities.IdentityMatrix(), ("decoder", "windows_imaging_component")),
            Step(2, "rotate_and_deskew", page.ArtifactId, normalizedArtifactId, forward, inverse,
                ("rotation_degrees_clockwise", rotate ? "90" : "0"), ("deskew_degrees", "0")),
            Step(3, "crop_and_perspective", normalizedArtifactId, normalizedArtifactId, IngestionUtilities.IdentityMatrix(), IngestionUtilities.IdentityMatrix(),
                ("crop", "none"), ("perspective", "none")),
            Step(4, "grayscale_denoise_threshold", normalizedArtifactId, normalizedArtifactId, IngestionUtilities.IdentityMatrix(), IngestionUtilities.IdentityMatrix(),
                ("threshold_method", "otsu"), ("threshold_value", threshold.ToString(CultureInfo.InvariantCulture)), ("denoise", "isolated_pixel_8_neighbour"))
        };
        var transform = new TransformRecord
        {
            TransformId = IngestionUtilities.StableId("transform", context.SourceSha256, page.PageNumber, profile),
            FromFrameId = page.CoordinateFrameId,
            ToFrameId = normalizedFrameId,
            ForwardMatrix = forward,
            InverseMatrix = inverse,
            InputUnit = MeasurementUnit.Pixel,
            OutputUnit = MeasurementUnit.Pixel,
            Parameters = new SortedDictionary<string, double>(StringComparer.Ordinal)
            {
                ["rotation_degrees_clockwise"] = rotate ? 90 : 0,
                ["deskew_degrees"] = 0,
                ["threshold"] = threshold
            },
            ErrorEstimate = 0,
            ErrorUnit = MeasurementUnit.Pixel,
            Source = ProviderName,
            BeforeArtifactId = page.ArtifactId,
            AfterArtifactId = normalizedArtifactId
        };
        return Task.FromResult(new RasterPreprocessResult
        {
            SourcePage = page,
            NormalizedPage = new()
            {
                PageNumber = page.PageNumber,
                ImagePath = outputPath,
                PixelWidth = normalized.Width,
                PixelHeight = normalized.Height,
                Dpi = page.Dpi,
                ArtifactId = normalizedArtifactId,
                CoordinateFrameId = normalizedFrameId,
                Sha256 = IngestionUtilities.Sha256File(outputPath)
            },
            Ledger = new() { Profile = profile, PageNumber = page.PageNumber, Steps = steps, Provenance = provenance },
            Transforms = [transform],
            Provenance = provenance,
            Diagnostics = darkRatio < 0.0001
                ? [new() { Code = "ING-BLANK-PAGE", Severity = ContractDiagnosticSeverity.Warning,
                    Message = "The rendered page contains too little foreground information and is retained as unreadable.",
                    PageNumber = page.PageNumber, ProviderName = ProviderName }]
                : []
        });
    }

    private static PreprocessStep Step(int sequence, string operation, string before, string after,
        IReadOnlyList<IReadOnlyList<double>> forward, IReadOnlyList<IReadOnlyList<double>> inverse,
        params (string Key, string Value)[] parameters) => new()
    {
        Sequence = sequence,
        Operation = operation,
        BeforeArtifactId = before,
        AfterArtifactId = after,
        ForwardMatrix = forward,
        InverseMatrix = inverse,
        Parameters = new SortedDictionary<string, string>(parameters.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal), StringComparer.Ordinal)
    };

    private static IReadOnlyList<IReadOnlyList<double>> ClockwiseMatrix(int sourceHeight) =>
        [new[] { 0d, -1d, sourceHeight - 1d }, new[] { 1d, 0d, 0d }, new[] { 0d, 0d, 1d }];

    private static IReadOnlyList<IReadOnlyList<double>> CounterClockwiseMatrix(int sourceHeight) =>
        [new[] { 0d, 1d, 0d }, new[] { -1d, 0d, sourceHeight - 1d }, new[] { 0d, 0d, 1d }];

    private static void Denoise(byte[] binary, int width, int height)
    {
        var remove = new List<int>();
        for (var y = 1; y < height - 1; y++)
        for (var x = 1; x < width - 1; x++)
        {
            var index = y * width + x;
            if (binary[index] == 0) continue;
            var neighbours = 0;
            for (var dy = -1; dy <= 1; dy++)
            for (var dx = -1; dx <= 1; dx++)
                if ((dx != 0 || dy != 0) && binary[(y + dy) * width + x + dx] != 0) neighbours++;
            if (neighbours == 0) remove.Add(index);
        }
        foreach (var index in remove) binary[index] = 0;
    }

    private static RasterBuffer ToBlackAndWhite(byte[] binary, int width, int height)
    {
        var bgra = new byte[binary.Length * 4];
        for (var i = 0; i < binary.Length; i++)
        {
            var value = binary[i] == 0 ? (byte)255 : (byte)0;
            bgra[i * 4] = value; bgra[i * 4 + 1] = value; bgra[i * 4 + 2] = value; bgra[i * 4 + 3] = 255;
        }
        return new(width, height, bgra);
    }
}
