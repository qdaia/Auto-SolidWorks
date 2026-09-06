using System.Globalization;
using System.Diagnostics;
using System.IO;
using CadModeling.Drawing.Providers.Abstractions;

namespace CadModeling.Drawing.Ingestion;

public sealed class WpfRasterInputProvider : IRasterInputProvider
{
    public const string ProviderName = "windows-imaging-raster";
    public const string ProviderVersion = "1.0.0";

    public Task<int> GetPageCountAsync(string imagePath, DrawingInputKind inputKind, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(inputKind == DrawingInputKind.Tiff ? RasterBuffer.FrameCount(imagePath) : 1);
    }

    public Task<RasterPage> LoadPageAsync(ProviderPageContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var frameIndex = context.InputKind == DrawingInputKind.Tiff ? context.PageNumber - 1 : 0;
        var buffer = RasterBuffer.Load(context.SourcePath, frameIndex);
        var path = Path.Combine(context.PageArtifactDirectory, "source-page.png");
        buffer.SavePng(path);
        return Task.FromResult(new RasterPage
        {
            PageNumber = context.PageNumber,
            ImagePath = path,
            PixelWidth = buffer.Width,
            PixelHeight = buffer.Height,
            Dpi = context.RenderDpi,
            ArtifactId = $"page-{context.PageNumber:D4}-source-raster",
            CoordinateFrameId = $"page-{context.PageNumber:D4}-source-pixel",
            Sha256 = IngestionUtilities.Sha256File(path)
        });
    }

    public ProviderProvenance GetProvenance(string profile, int seed) =>
        IngestionUtilities.Provenance(ProviderName, ProviderVersion, profile, seed);
}

public sealed class PdftoppmRasterizationProvider : IPdfRasterizationProvider
{
    public const string ProviderName = "poppler-pdftoppm";
    private readonly string? configuredExecutablePath;

    public PdftoppmRasterizationProvider(string? executablePath = null)
    {
        configuredExecutablePath = executablePath;
    }

    public async Task<RasterPage> RenderPageAsync(ProviderPageContext context, CancellationToken cancellationToken = default)
    {
        var executablePath = ResolveExecutable(configuredExecutablePath);
        var prefix = Path.Combine(context.PageArtifactDirectory, "rendered-page");
        var arguments = new[]
        {
            "-f", context.PageNumber.ToString(CultureInfo.InvariantCulture),
            "-l", context.PageNumber.ToString(CultureInfo.InvariantCulture),
            "-singlefile", "-r", context.RenderDpi.ToString(CultureInfo.InvariantCulture),
            "-png", context.SourcePath, prefix
        };
        var result = await IngestionUtilities.RunProcessAsync(executablePath, arguments, 120, cancellationToken);
        var path = prefix + ".png";
        if (result.ExitCode != 0 || !File.Exists(path))
            throw new InvalidDataException($"PDF rasterization failed with exit code {result.ExitCode}: {result.StandardError.Trim()}");
        var buffer = RasterBuffer.Load(path);
        return new()
        {
            PageNumber = context.PageNumber,
            ImagePath = path,
            PixelWidth = buffer.Width,
            PixelHeight = buffer.Height,
            Dpi = context.RenderDpi,
            ArtifactId = $"page-{context.PageNumber:D4}-rendered-raster",
            CoordinateFrameId = $"page-{context.PageNumber:D4}-rendered-pixel",
            Sha256 = IngestionUtilities.Sha256File(path)
        };
    }

    public ProviderProvenance GetProvenance(string profile, int seed) =>
        CreateProvenance(ResolveExecutable(configuredExecutablePath), profile, seed);

    private static ProviderProvenance CreateProvenance(string executablePath, string profile, int seed) =>
        IngestionUtilities.Provenance(ProviderName, IngestionUtilities.ExecutableVersion(executablePath, "-v"),
            $"{profile}|{executablePath}|{IngestionUtilities.Sha256File(executablePath)}", seed,
            binarySha: IngestionUtilities.Sha256File(executablePath));

    private static string ResolveExecutable(string? configured)
    {
        var candidates = new List<string?>
        {
            configured,
            Environment.GetEnvironmentVariable("CAD_PDFTOPPM_PATH"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "codex-runtimes", "codex-primary-runtime", "dependencies", "native", "poppler", "Library", "bin", "pdftoppm.exe"),
            Path.Combine(AppContext.BaseDirectory, "providers", "poppler", "pdftoppm.exe")
        };
        var pathValue = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        candidates.AddRange(pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).Select(folder => Path.Combine(folder, "pdftoppm.exe")));
        var resolved = candidates.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path));
        return resolved is null
            ? throw new FileNotFoundException("No reviewed pdftoppm provider was found. Set CAD_PDFTOPPM_PATH to an absolute executable path.")
            : Path.GetFullPath(resolved);
    }
}
