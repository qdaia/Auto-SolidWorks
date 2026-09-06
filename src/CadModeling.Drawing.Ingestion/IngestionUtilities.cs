using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CadModeling.Drawing.Providers.Abstractions;

namespace CadModeling.Drawing.Ingestion;

internal static class IngestionUtilities
{
    public static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    public static string Sha256Text(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    public static string StableId(string prefix, params object?[] values)
    {
        var payload = string.Join("|", values.Select(value => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty));
        return $"{prefix}-{Sha256Text(payload)[..16]}";
    }

    public static IReadOnlyList<IReadOnlyList<double>> IdentityMatrix() =>
        [new[] { 1d, 0d, 0d }, new[] { 0d, 1d, 0d }, new[] { 0d, 0d, 1d }];

    public static ProviderProvenance Provenance(
        string name,
        string version,
        string configuration,
        int seed,
        string? modelSha = null,
        string? binarySha = null) => new()
    {
        ProviderName = name,
        ProviderVersion = version,
        ConfigurationSha256 = Sha256Text(configuration),
        DeterministicSeed = seed,
        BinarySha256 = binarySha,
        ModelSha256 = modelSha,
        Hardware = $"{Environment.OSVersion.Platform};{Environment.OSVersion.Version};{System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}"
    };

    public static string ExecutableVersion(string executable, params string[] arguments)
    {
        var info = FileVersionInfo.GetVersionInfo(executable);
        var embedded = info.FileVersion ?? info.ProductVersion;
        if (!string.IsNullOrWhiteSpace(embedded)) return embedded;
        try
        {
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start);
            if (process is null) return "unknown";
            if (!process.WaitForExit(5000))
            {
                process.Kill(entireProcessTree: true);
                return "unknown";
            }
            var text = process.StandardOutput.ReadToEnd() + "\n" + process.StandardError.ReadToEnd();
            return text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault() ?? "unknown";
        }
        catch
        {
            return "unknown";
        }
    }

    public static async Task<(int ExitCode, string StandardOutput, string StandardError)> RunProcessAsync(
        string executable, IEnumerable<string> arguments, int timeoutSeconds, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException($"Failed to start provider executable '{executable}'.");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw;
        }
        return (process.ExitCode, await stdout, await stderr);
    }
}

internal sealed class RasterBuffer
{
    public int Width { get; }
    public int Height { get; }
    public int Stride => Width * 4;
    public byte[] Bgra { get; }

    public RasterBuffer(int width, int height, byte[] bgra)
    {
        Width = width;
        Height = height;
        Bgra = bgra;
    }

    public static RasterBuffer Load(string path, int frameIndex = 0)
    {
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        if (frameIndex < 0 || frameIndex >= decoder.Frames.Count) throw new ArgumentOutOfRangeException(nameof(frameIndex));
        var converted = new FormatConvertedBitmap(decoder.Frames[frameIndex], PixelFormats.Bgra32, null, 0);
        var bytes = new byte[converted.PixelWidth * converted.PixelHeight * 4];
        converted.CopyPixels(bytes, converted.PixelWidth * 4, 0);
        return new(converted.PixelWidth, converted.PixelHeight, bytes);
    }

    public static int FrameCount(string path)
    {
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames.Count;
    }

    public static (int Width, int Height) Dimensions(string path, int frameIndex = 0)
    {
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        if (frameIndex < 0 || frameIndex >= decoder.Frames.Count) throw new ArgumentOutOfRangeException(nameof(frameIndex));
        return (decoder.Frames[frameIndex].PixelWidth, decoder.Frames[frameIndex].PixelHeight);
    }

    public byte[] ToBinaryOtsu(out int threshold)
    {
        var histogram = new long[256];
        var gray = new byte[Width * Height];
        for (var i = 0; i < gray.Length; i++)
        {
            var offset = i * 4;
            var value = (byte)Math.Clamp((int)Math.Round(Bgra[offset + 2] * 0.299 + Bgra[offset + 1] * 0.587 + Bgra[offset] * 0.114), 0, 255);
            gray[i] = value;
            histogram[value]++;
        }
        threshold = Otsu(histogram, gray.Length);
        var binary = new byte[gray.Length];
        for (var i = 0; i < gray.Length; i++) binary[i] = gray[i] <= threshold ? (byte)1 : (byte)0;
        return binary;
    }

    public RasterBuffer RotateClockwise()
    {
        var output = new byte[Bgra.Length];
        var newWidth = Height;
        for (var y = 0; y < Height; y++)
        for (var x = 0; x < Width; x++)
        {
            var nx = Height - 1 - y;
            var ny = x;
            Buffer.BlockCopy(Bgra, (y * Width + x) * 4, output, (ny * newWidth + nx) * 4, 4);
        }
        return new(Height, Width, output);
    }

    public RasterBuffer Scale(double factor)
    {
        var bitmap=BitmapSource.Create(Width,Height,96,96,PixelFormats.Bgra32,null,Bgra,Stride);
        var scaled=new TransformedBitmap(bitmap,new System.Windows.Media.ScaleTransform(factor,factor));
        var bytes=new byte[scaled.PixelWidth*scaled.PixelHeight*4];
        scaled.CopyPixels(bytes,scaled.PixelWidth*4,0);
        return new(scaled.PixelWidth,scaled.PixelHeight,bytes);
    }
    public RasterBuffer Crop(int left,int top,int width,int height)
    {
        if(left<0||top<0||width<1||height<1||left+width>Width||top+height>Height) throw new ArgumentException("Crop lies outside the raster.");
        var bytes=new byte[width*height*4];
        for(var y=0;y<height;y++) Buffer.BlockCopy(Bgra,((top+y)*Width+left)*4,bytes,y*width*4,width*4);
        return new(width,height,bytes);
    }

    public void SavePng(string path)
    {
        var bitmap = BitmapSource.Create(Width, Height, 96, 96, PixelFormats.Bgra32, null, Bgra, Stride);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        encoder.Save(stream);
    }

    public void DrawRectangle(int left, int top, int right, int bottom, byte b, byte g, byte r)
    {
        left = Math.Clamp(left, 0, Width - 1); right = Math.Clamp(right, 0, Width - 1);
        top = Math.Clamp(top, 0, Height - 1); bottom = Math.Clamp(bottom, 0, Height - 1);
        for (var x = left; x <= right; x++) { Set(x, top, b, g, r); Set(x, bottom, b, g, r); }
        for (var y = top; y <= bottom; y++) { Set(left, y, b, g, r); Set(right, y, b, g, r); }
    }

    private void Set(int x, int y, byte b, byte g, byte r)
    {
        var index = (y * Width + x) * 4;
        Bgra[index] = b; Bgra[index + 1] = g; Bgra[index + 2] = r; Bgra[index + 3] = 255;
    }

    private static int Otsu(long[] histogram, int total)
    {
        long sum = 0;
        for (var i = 0; i < histogram.Length; i++) sum += i * histogram[i];
        long backgroundWeight = 0;
        double backgroundSum = 0;
        double maximum = -1;
        var threshold = 127;
        for (var i = 0; i < histogram.Length; i++)
        {
            backgroundWeight += histogram[i];
            if (backgroundWeight == 0) continue;
            var foregroundWeight = total - backgroundWeight;
            if (foregroundWeight == 0) break;
            backgroundSum += i * histogram[i];
            var backgroundMean = backgroundSum / backgroundWeight;
            var foregroundMean = (sum - backgroundSum) / foregroundWeight;
            var variance = backgroundWeight * (double)foregroundWeight * Math.Pow(backgroundMean - foregroundMean, 2);
            if (variance <= maximum) continue;
            maximum = variance;
            threshold = i;
        }
        return threshold;
    }
}

internal sealed record PixelComponent(int Left, int Top, int Right, int Bottom, int Count, int Quadrants)
{
    public int Width => Right - Left + 1;
    public int Height => Bottom - Top + 1;
}

internal static class BinaryComponents
{
    public static IReadOnlyList<PixelComponent> Find(byte[] binary, int width, int height, int minimumPixels = 3)
    {
        var seen = new bool[binary.Length];
        var result = new List<PixelComponent>();
        var queue = new Queue<int>();
        for (var start = 0; start < binary.Length; start++)
        {
            if (binary[start] == 0 || seen[start]) continue;
            seen[start] = true;
            queue.Enqueue(start);
            var left = start % width; var right = left; var top = start / width; var bottom = top; var count = 0;
            var points = new List<(int X, int Y)>();
            while (queue.Count > 0)
            {
                var index = queue.Dequeue(); var x = index % width; var y = index / width;
                count++; points.Add((x, y)); left = Math.Min(left, x); right = Math.Max(right, x); top = Math.Min(top, y); bottom = Math.Max(bottom, y);
                for (var dy = -1; dy <= 1; dy++)
                for (var dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0) continue;
                    var nx = x + dx; var ny = y + dy;
                    if (nx < 0 || ny < 0 || nx >= width || ny >= height) continue;
                    var next = ny * width + nx;
                    if (binary[next] == 0 || seen[next]) continue;
                    seen[next] = true; queue.Enqueue(next);
                }
            }
            if (count < minimumPixels) continue;
            var cx = (left + right) / 2d; var cy = (top + bottom) / 2d; var quadrants = 0;
            foreach (var (x, y) in points)
            {
                if (x <= cx && y <= cy) quadrants |= 1;
                if (x > cx && y <= cy) quadrants |= 2;
                if (x <= cx && y > cy) quadrants |= 4;
                if (x > cx && y > cy) quadrants |= 8;
            }
            result.Add(new(left, top, right, bottom, count, quadrants));
        }
        return result.OrderBy(c => c.Top).ThenBy(c => c.Left).ToArray();
    }
}
