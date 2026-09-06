using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace CadModeling.Drawing.Ingestion;

public sealed record LocalOcrProviderConfiguration
{
    public const string DefaultLanguage = "eng+chi_sim";

    public string? ExecutablePath { get; init; }
    public string? TessdataDirectory { get; init; }
    public string Language { get; init; } = DefaultLanguage;
    public int PageSegmentationMode { get; init; } = 11;
    public int OcrEngineMode { get; init; } = 1;
    public int TimeoutSeconds { get; init; } = 120;
    public string? ExpectedExecutableSha256 { get; init; }
    public string? ExpectedTsvConfigSha256 { get; init; }
    public IReadOnlyDictionary<string, string> ExpectedModelSha256 { get; init; } =
        new SortedDictionary<string, string>(StringComparer.Ordinal);

    public static LocalOcrProviderConfiguration FromEnvironment(
        string? executablePath = null,
        string? tessdataDirectory = null,
        string? language = null) => new()
    {
        ExecutablePath = executablePath ?? Environment.GetEnvironmentVariable("CAD_TESSERACT_PATH"),
        TessdataDirectory = tessdataDirectory ?? Environment.GetEnvironmentVariable("CAD_TESSDATA_DIR"),
        Language = language ?? Environment.GetEnvironmentVariable("CAD_TESSERACT_LANG") ?? DefaultLanguage,
        ExpectedExecutableSha256 = Environment.GetEnvironmentVariable("CAD_TESSERACT_SHA256"),
        ExpectedTsvConfigSha256 = Environment.GetEnvironmentVariable("CAD_TESSERACT_TSV_SHA256"),
        ExpectedModelSha256 = ParseExpectedModelHashes(Environment.GetEnvironmentVariable("CAD_TESSERACT_MODEL_SHA256"))
    };

    private static IReadOnlyDictionary<string, string> ParseExpectedModelHashes(string? value)
    {
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(value)) return result;
        foreach (var entry in value.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = entry.IndexOf('=');
            if (separator <= 0 || separator == entry.Length - 1) continue;
            result[entry[..separator].Trim()] = entry[(separator + 1)..].Trim();
        }
        return result;
    }
}

public sealed record LocalOcrConfigurationCheck
{
    public bool Ready { get; init; }
    public string Code { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public string? ExecutablePath { get; init; }
    public string? TessdataDirectory { get; init; }
    public string ProviderVersion { get; init; } = "unknown";
    public string? BinarySha256 { get; init; }
    public string? TsvConfigSha256 { get; init; }
    public string? CombinedModelSha256 { get; init; }
    public IReadOnlyDictionary<string, string> ModelSha256 { get; init; } =
        new SortedDictionary<string, string>(StringComparer.Ordinal);
    public string ConfigurationFingerprint { get; init; } = string.Empty;
}

public static partial class LocalOcrConfigurationChecker
{
    public static LocalOcrConfigurationCheck Check(LocalOcrProviderConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var fingerprint = Fingerprint(configuration);
        if (string.IsNullOrWhiteSpace(configuration.ExecutablePath) || string.IsNullOrWhiteSpace(configuration.TessdataDirectory))
            return Failure("ING-OCR-MODEL-NOT-CONFIGURED",
                "No reviewed local Tesseract executable and tessdata directory were configured.", fingerprint);
        if (!Path.IsPathFullyQualified(configuration.ExecutablePath))
            return Failure("ING-OCR-EXECUTABLE-PATH-UNSAFE", "The local OCR executable path must be absolute.", fingerprint);
        if (!Path.IsPathFullyQualified(configuration.TessdataDirectory))
            return Failure("ING-OCR-TESSDATA-PATH-UNSAFE", "The local OCR tessdata path must be absolute.", fingerprint);

        var executable = Path.GetFullPath(configuration.ExecutablePath);
        var tessdata = Path.GetFullPath(configuration.TessdataDirectory);
        if (!File.Exists(executable))
            return Failure("ING-OCR-EXECUTABLE-MISSING", $"Configured local OCR executable does not exist: {executable}", fingerprint,
                executable, tessdata);
        if (!Directory.Exists(tessdata))
            return Failure("ING-OCR-TESSDATA-MISSING", $"Configured tessdata directory does not exist: {tessdata}", fingerprint,
                executable, tessdata);
        if (configuration.TimeoutSeconds is < 1 or > 600)
            return Failure("ING-OCR-TIMEOUT-CONFIG", "Local OCR timeout must be within 1..600 seconds.", fingerprint, executable, tessdata);
        if (configuration.PageSegmentationMode is < 0 or > 13 || configuration.OcrEngineMode is < 0 or > 3)
            return Failure("ING-OCR-MODE-CONFIG", "Tesseract PSM/OEM configuration is outside the supported numeric range.", fingerprint,
                executable, tessdata);

        var languages = Languages(configuration.Language);
        if (languages.Length == 0)
            return Failure("ING-OCR-LANGUAGE-CONFIG", "At least one local Tesseract language must be configured.", fingerprint,
                executable, tessdata);

        var tsvConfigPath = Path.Combine(tessdata, "configs", "tsv");
        if (!File.Exists(tsvConfigPath) || new FileInfo(tsvConfigPath).Length == 0)
            return Failure("ING-OCR-TSV-CONFIG-MISSING",
                $"Required reviewed Tesseract TSV config is missing: {tsvConfigPath}", fingerprint, executable, tessdata);
        var tsvConfigSha = IngestionUtilities.Sha256File(tsvConfigPath);
        if (!string.IsNullOrWhiteSpace(configuration.ExpectedTsvConfigSha256) &&
            !HashEquals(configuration.ExpectedTsvConfigSha256, tsvConfigSha))
            return Failure("ING-OCR-TSV-CONFIG-HASH-MISMATCH",
                "Tesseract TSV config hash does not match the reviewed deployment pin.", fingerprint,
                executable, tessdata, tsvConfigSha: tsvConfigSha);

        var modelLanguages = languages
            .Concat(languages.SelectMany(ModelDependencies))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(item => item, StringComparer.Ordinal)
            .ToArray();
        var modelHashes = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var language in modelLanguages)
        {
            if (!LanguageName().IsMatch(language))
                return Failure("ING-OCR-LANGUAGE-CONFIG", $"Unsafe Tesseract language token '{language}'.", fingerprint, executable, tessdata);
            var modelPath = Path.Combine(tessdata, language + ".traineddata");
            if (!File.Exists(modelPath) || new FileInfo(modelPath).Length < 1024)
                return Failure("ING-OCR-MODEL-MISSING", $"Required reviewed OCR model is missing or truncated: {modelPath}", fingerprint,
                    executable, tessdata);
            var actual = IngestionUtilities.Sha256File(modelPath);
            modelHashes[language] = actual;
            if (configuration.ExpectedModelSha256.TryGetValue(language, out var expected) && !HashEquals(expected, actual))
                return Failure("ING-OCR-MODEL-HASH-MISMATCH", $"OCR model hash mismatch for '{language}'.", fingerprint,
                    executable, tessdata, IngestionUtilities.Sha256File(executable), modelHashes);
        }

        var binarySha = IngestionUtilities.Sha256File(executable);
        if (!string.IsNullOrWhiteSpace(configuration.ExpectedExecutableSha256) &&
            !HashEquals(configuration.ExpectedExecutableSha256, binarySha))
            return Failure("ING-OCR-EXECUTABLE-HASH-MISMATCH", "Local OCR executable hash does not match the reviewed deployment pin.", fingerprint,
                executable, tessdata, binarySha, modelHashes);

        var combinedModelSha = IngestionUtilities.Sha256Text(string.Join("|", modelHashes.Select(item => $"{item.Key}:{item.Value}")));
        return new()
        {
            Ready = true,
            Code = "ING-OCR-READY",
            Message = "Reviewed local Tesseract configuration is ready for offline OCR.",
            ExecutablePath = executable,
            TessdataDirectory = tessdata,
            ProviderVersion = IngestionUtilities.ExecutableVersion(executable, "--version"),
            BinarySha256 = binarySha,
            TsvConfigSha256 = tsvConfigSha,
            CombinedModelSha256 = combinedModelSha,
            ModelSha256 = modelHashes,
            ConfigurationFingerprint = fingerprint
        };
    }

    public static string Fingerprint(LocalOcrProviderConfiguration configuration)
    {
        var expectedModels = string.Join("|", configuration.ExpectedModelSha256.OrderBy(item => item.Key, StringComparer.Ordinal)
            .Select(item => $"{item.Key}:{item.Value.ToLowerInvariant()}"));
        return string.Join("|",
            "provider=tesseract-local-v1",
            $"language={configuration.Language}",
            $"psm={configuration.PageSegmentationMode}",
            $"oem={configuration.OcrEngineMode}",
            $"timeout={configuration.TimeoutSeconds}",
            $"expected_binary={configuration.ExpectedExecutableSha256?.ToLowerInvariant() ?? string.Empty}",
            $"expected_tsv_config={configuration.ExpectedTsvConfigSha256?.ToLowerInvariant() ?? string.Empty}",
            $"expected_models={expectedModels}");
    }

    private static string[] Languages(string language) => language.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        .Distinct(StringComparer.Ordinal).OrderBy(item => item, StringComparer.Ordinal).ToArray();
    private static IReadOnlyList<string> ModelDependencies(string language) => language switch
    {
        "chi_sim" => ["chi_sim_vert"],
        _ => []
    };
    private static bool HashEquals(string left, string right) =>
        string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase) && right.Length == 64 && right.All(Uri.IsHexDigit);

    private static LocalOcrConfigurationCheck Failure(
        string code,
        string message,
        string fingerprint,
        string? executable = null,
        string? tessdata = null,
        string? binarySha = null,
        IReadOnlyDictionary<string, string>? modelSha = null,
        string? tsvConfigSha = null) => new()
    {
        Ready = false,
        Code = code,
        Message = message,
        ExecutablePath = executable,
        TessdataDirectory = tessdata,
        BinarySha256 = binarySha,
        TsvConfigSha256 = tsvConfigSha,
        ModelSha256 = modelSha ?? new SortedDictionary<string, string>(StringComparer.Ordinal),
        ConfigurationFingerprint = fingerprint
    };

    [GeneratedRegex("^[A-Za-z0-9_]+$", RegexOptions.CultureInvariant)]
    private static partial Regex LanguageName();
}

public sealed record TesseractTsvWord
{
    public int Left { get; init; }
    public int Top { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public double ConfidencePercent { get; init; }
    public string Text { get; init; } = string.Empty;
}

public static class TesseractTsvParser
{
    public static IReadOnlyList<TesseractTsvWord> Parse(string tsv)
    {
        if (string.IsNullOrEmpty(tsv)) return [];
        var words = new List<TesseractTsvWord>();
        foreach (var line in tsv.Split('\n').Skip(1))
        {
            var fields = line.TrimEnd('\r').Split('\t');
            if (fields.Length < 12 || fields[0] != "5" || string.IsNullOrWhiteSpace(fields[11])) continue;
            if (!int.TryParse(fields[6], out var left) || !int.TryParse(fields[7], out var top) ||
                !int.TryParse(fields[8], out var width) || !int.TryParse(fields[9], out var height)) continue;
            _ = double.TryParse(fields[10], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var confidence);
            words.Add(new()
            {
                Left = left,
                Top = top,
                Width = width,
                Height = height,
                ConfidencePercent = confidence,
                Text = fields[11]
            });
        }
        return words;
    }
}
