using System.ComponentModel;
using System.IO;
using System.Diagnostics;
using System.Text.Json;
using ModelContextProtocol.Server;
using CadModeling.Ir;

public sealed record GordonCurve
{
    public string Id { get; init; } = "";
    public double[][] PointsMm { get; init; } = [];
}

public sealed record GordonSurfaceDraft
{
    public GordonCurve[] Profiles { get; init; } = [];
    public GordonCurve[] Guides { get; init; } = [];
    public double ToleranceMm { get; init; } = 0.01;
    public int SamplesPerCurve { get; init; } = 41;
}

[McpServerToolType]
public sealed class GordonSurfaceTools
{
    private static string ConfigPath => Environment.GetEnvironmentVariable("CAD_SURFACE_RUNTIME_CONFIG")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AutoSolidWorks", "dependencies", "surface-runtime.json");

    public static object Capability() => new
    {
        tool = "cad_build_gordon_surface", backend = "FreeCAD/OpenCascade + 上游 CurvesWB/TiGL",
        configuration_present = File.Exists(ConfigPath), configuration_path = ConfigPath,
        inputs = "两组开放曲线，每组 2..16 条模型空间毫米插值点曲线。",
        outputs = new[] { "step", "brep", "FCStd", "svg", "json" },
        checks = new[] { "unique_pair_intersections", "boundary_coverage", "shape_validity", "sampled_curve_deviation", "step_reopen", "sampled_surface_regularity" },
        solidworks_native_feature = false, g1_g2_certification = false, automatic_shape_reconstruction = false
    };

    [McpServerTool(Name = "cad_build_gordon_surface", ReadOnly = false, Destructive = false,
        Idempotent = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("通过 FreeCAD/OpenCascade 和固定版本的上游 CurvesWB/TiGL 在本机构建 Gordon NURBS 曲面。输入两组模型空间毫米插值点曲线；每条轮廓与每条导向必须恰交一次，外侧曲线必须覆盖端点。向新的绝对目录写入 STEP、BREP、FCStd 可编辑输入曲线、SVG 预览和 JSON 检查。此工具不访问网络或启动 SolidWorks。检查采样偏差及 STEP 读回，不认证 G1/G2、任意形状重建或原生 SolidWorks 特征可编辑性；需要已配置的本地 FreeCAD Python 运行环境。")]
    public static async Task<JsonElement> Build(GordonSurfaceDraft draft, string outputDirectory, CancellationToken cancellationToken = default)
    {
        Validate(draft, outputDirectory);
        using var config = JsonDocument.Parse(await File.ReadAllTextAsync(ConfigPath, cancellationToken));
        var python = config.RootElement.GetProperty("python_executable").GetString()!;
        if (!Path.IsPathFullyQualified(python) || !File.Exists(python))
            throw new InvalidOperationException("已配置的 FreeCAD Python 可执行文件不存在。");
        var worker = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "surface", "gordon_worker.py"));
        if (!File.Exists(worker)) throw new FileNotFoundException("随包 Gordon 工作进程脚本缺失。", worker);
        Directory.CreateDirectory(Path.GetDirectoryName(outputDirectory)!);
        // The worker exclusively creates the output directory before writing anything.
        var start = new ProcessStartInfo(python) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(python)! };
        start.ArgumentList.Add(worker);
        start.ArgumentList.Add(outputDirectory);
        start.Environment["PYTHONNOUSERSITE"] = "1";
        using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动本地曲面工作进程。");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        try
        {
            await process.StandardInput.WriteAsync(JsonSerializer.Serialize(draft, ModelingIrJson.Options).AsMemory(), timeout.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            var output = await stdout;
            var error = await stderr;
            var receipt = Path.Combine(outputDirectory, "report.json");
            if (!File.Exists(receipt)) throw new InvalidOperationException($"曲面工作进程失败（退出码={process.ExitCode}）：{error[..Math.Min(error.Length, 2000)]}{output[..Math.Min(output.Length, 2000)]}");
            using var report = JsonDocument.Parse(await File.ReadAllTextAsync(receipt, cancellationToken));
            if (process.ExitCode != 0 && report.RootElement.GetProperty("success").GetBoolean())
                throw new InvalidOperationException("曲面工作进程退出失败，但回执报告成功；结果不可信。");
            return report.RootElement.Clone();
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); }
        }
    }

    private static void Validate(GordonSurfaceDraft draft, string output)
    {
        if (!Path.IsPathFullyQualified(output) || Directory.Exists(output) || File.Exists(output))
            throw new ArgumentException("outputDirectory 必须是尚未使用的绝对目录。");
        if (!double.IsFinite(draft.ToleranceMm) || draft.ToleranceMm is < 0.0001 or > 0.1 || draft.SamplesPerCurve is < 11 or > 201)
            throw new ArgumentException("tolerance_mm 必须在 0.0001..0.1 范围内；samples_per_curve 必须在 11..201 范围内。");
        if (draft.Profiles.Length is < 2 or > 16 || draft.Guides.Length is < 2 or > 16)
            throw new ArgumentException("每组曲线必须包含 2..16 条曲线。");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var curve in draft.Profiles.Concat(draft.Guides))
        {
            if (string.IsNullOrWhiteSpace(curve.Id) || curve.Id.Length > 128 || !ids.Add(curve.Id))
                throw new ArgumentException("曲线 ID 必须非空且唯一，长度不得超过 128 个字符。");
            if (curve.PointsMm.Length is < 2 or > 128 || curve.PointsMm.Any(p => p.Length != 3 || p.Any(v => !double.IsFinite(v) || Math.Abs(v) > 1000000)))
                throw new ArgumentException("每条曲线需要 2..128 个有限的 XYZ 插值点，单位为毫米。");
        }
    }
}
