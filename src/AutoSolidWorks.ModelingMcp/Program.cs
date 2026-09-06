using System.ComponentModel;
using System.IO;
using System.Reflection;
using CadModeling.Core;
using CadModeling.Ir;
using CadModeling.Drawing.Contracts;
using CadModeling.Drawing.Ingestion;
using CadModeling.Drawing.Providers.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Services.AddSingleton<RuleBasedTextCompiler>();
builder.Services.AddSingleton<GenericPlanCompiler>();
builder.Services.AddSingleton<ModelingIrValidator>();
builder.Services.AddSingleton<IModelingExecutor>(_ => new AutoStartingNamedPipeModelingExecutor(new()
{
    PipeName = Environment.GetEnvironmentVariable("CAD_SOLIDWORKS_PIPE") ?? "auto-solidworks-modeling",
    ExecutablePath = Environment.GetEnvironmentVariable("CAD_SOLIDWORKS_EXECUTOR"),
    LogDirectory = Environment.GetEnvironmentVariable("CAD_EXECUTOR_LOG_DIR")
}));
builder.Services.AddSingleton<ModelingWorkflow>();
builder.Services.AddSingleton<IPdfNativeObservationProvider, PdfPigNativeObservationProvider>();
builder.Services.AddSingleton<IPdfRasterizationProvider, PdftoppmRasterizationProvider>();
builder.Services.AddSingleton<IRasterInputProvider, WpfRasterInputProvider>();
builder.Services.AddSingleton<IRasterPreprocessor, EngineeringRasterPreprocessor>();
builder.Services.AddSingleton<ILayoutObservationProvider, PageLayoutObservationProvider>();
builder.Services.AddSingleton<ITextObservationProvider, LocalTextObservationProvider>();
builder.Services.AddSingleton<IPrimitiveObservationProvider, DeterministicPrimitiveObservationProvider>();
builder.Services.AddSingleton<IObservationFusionService, ObservationFusionService>();
builder.Services.AddSingleton<IEngineeringDrawingIngestionService, EngineeringDrawingIngestionService>();
builder.Services.AddMcpServer().WithStdioServerTransport().WithTools<ModelingTools>(ModelingIrJson.Options);
await builder.Build().RunAsync();

[McpServerToolType]
public sealed class ModelingTools
{
    [McpServerTool(Name="cad_list_weldment_profiles",ReadOnly=true,Destructive=false,Idempotent=true,OpenWorld=false,UseStructuredContent=true)]
    [Description("Find local native SLDLFP weldment profiles. Default search is ProgramData/SOLIDWORKS. Use cad_inspect_model on a returned profile to read available configurations before creating a structural member.")]
    public static object WeldmentProfiles(string? directory=null)
    {
        var root=directory is null ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"SOLIDWORKS") : AbsolutePath(directory);
        var profiles=Directory.Exists(root)?Directory.EnumerateFiles(root,"*.sldlfp",new EnumerationOptions { RecurseSubdirectories=true,IgnoreInaccessible=true,AttributesToSkip=FileAttributes.ReparsePoint })
            .OrderBy(p=>p,StringComparer.OrdinalIgnoreCase).Take(1000).Select(p=>new { path=p,name=Path.GetFileNameWithoutExtension(p),library=Path.GetRelativePath(root,Path.GetDirectoryName(p)!) }).ToArray():[];
        return new { directory=root,profiles,maximum_results=1000 };
    }
    [McpServerTool(Name="cad_build_assembly",ReadOnly=false,Destructive=false,Idempotent=false,OpenWorld=false,UseStructuredContent=true)]
    [Description("Create a local native SolidWorks assembly from existing components, explicit mm/Euler-degree transforms, geometric mates and interference checks. Preserves source components and verifies the saved assembly by reopening it.")]
    public static Task<AssemblyResult> BuildAssembly(IModelingExecutor executor,AssemblyPlan plan,CancellationToken cancellationToken=default)
    {
        AssemblyPlanValidation.Validate(plan);
        return executor.BuildAssemblyAsync(plan,cancellationToken);
    }
    [McpServerTool(Name = "cad_inspect_model", ReadOnly = true, Destructive = false,
        Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Read a local model's features, native dimensions, geometry and optional face/edge queries. Opens closed native files read-only; imports STEP/STP through an isolated working copy for geometry checks without saving source files. May start SolidWorks.")]
    public static Task<ModelInspection> InspectModel(IModelingExecutor executor,string inputPath,
        EntityQuery[]? queries=null,CancellationToken cancellationToken=default) =>
        executor.InspectAsync(new(Path.GetFullPath(inputPath),queries),cancellationToken);

    [McpServerTool(Name = "cad_get_capabilities", ReadOnly = true, Destructive = false,
        Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("List local part/assembly modeling operations, native feature kinds, units, formats, workflow and current limits.")]
    public static object Capabilities() => new
    {
        server_version = typeof(ModelingTools).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
        mode = "modeling_only",
        default_unit = "mm",
        input_formats = new[] { "text", "pdf", "png", "jpg", "jpeg", "tif", "tiff" },
        output_formats = new[] { "sldprt", "sldasm", "step", "stp", "stl" },
        schema_version=ModelingIrSchema.CurrentVersion,
        operations = ModelingCapabilityCatalog.Current.Operations,
        native_feature_kinds=Enum.GetNames<NativeFeatureKind>(),
        sketch_primitive_kinds=Enum.GetNames<GenericPrimitiveKind>(),
        assembly_mate_kinds=Enum.GetNames<AssemblyMateKind>(),
        natural_language_examples = ModelingCapabilityCatalog.Current.NaturalLanguageExamples.Skip(1).ToArray(),
        workflow = "Read the drawing if supplied, create a typed model plan, then build and save it.",
        limitations = ModelingCapabilityCatalog.Current.Limitations
    };

    [McpServerTool(Name = "cad_read_drawing", ReadOnly = false, Destructive = false,
        Idempotent = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Read a local engineering image or PDF into page images, text and primitive observations for modeling. Does not start SolidWorks.")]
    public static Task<DrawingIngestionResponse> ReadDrawing(
        IEngineeringDrawingIngestionService service,
        [Description("Absolute path of the image or PDF.")] string inputPath,
        [Description("Directory for extracted page images. Defaults to LocalAppData/AutoSolidWorks/drawings.")] string? artifactRoot = null,
        int[]? pageNumbers = null, int renderDpi = 300,
        [Description("Optional agent-interpreted view or annotation regions in page-normalized [0,1] coordinates. Set OCR rotation for sideways dimension text. Region labels describe geometry; they are not inferred from OCR.")] DrawingViewRegionHint[]? viewHints = null,
        CancellationToken cancellationToken = default) => service.IngestAsync(new()
        {
            InputPath = Path.GetFullPath(inputPath),
            ArtifactRoot = Path.GetFullPath(artifactRoot ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AutoSolidWorks", "drawings")),
            PageNumbers = pageNumbers ?? [],
            ViewHints = viewHints ?? [],
            RenderDpi = renderDpi,
            PreprocessProfile = "engineering-default-v1",
            ProviderProfile = "offline-default-v1"
        }, cancellationToken);

    [McpServerTool(Name = "cad_create_model_plan", ReadOnly = true, Destructive = false,
        Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Compile a typed geometry draft, or a supported simple text description, into an executable model plan. Dimensions default to mm. Supply exactly one of draft or text. Returns ir_json for cad_build_model.")]
    public static object CreatePlan(
        GenericPlanCompiler compiler, ModelingWorkflow workflow,
        [Description("Absolute .SLDPRT output path.")] string nativeOutputPath,
        GenericModelDraft? draft = null, string? text = null,
        [Description("Optional absolute STEP/STP/STL output paths.")] string[]? exportPaths = null,
        bool overwriteAllowed = false)
    {
        if ((draft is null) == string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Supply exactly one of draft or text.");
        var output = AbsolutePath(nativeOutputPath);
        var exports = exportPaths?.Select(AbsolutePath).ToArray();
        var compilation = draft is not null
            ? compiler.Compile(draft, output, exports, overwriteAllowed)
            : workflow.Compile(text!, output);
        if (draft is null && compilation.Plan is not null)
        {
            compilation = compilation with { Plan = compilation.Plan with {
                Output = compilation.Plan.Output with {
                    ExportPaths = exports ?? [], OverwriteAllowed = overwriteAllowed
                }
            }};
        }
        return new {
            success = compilation.Success,
            diagnostics = compilation.Diagnostics,
            plan = compilation.Plan,
            ir_json = compilation.Plan is null ? null : ModelingIrJson.Serialize(compilation.Plan)
        };
    }

    [McpServerTool(Name = "cad_build_model", ReadOnly = false, Destructive = false,
        Idempotent = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Build the compiled plan in SolidWorks and save its outputs. Runs directly by default. dryRun is an optional geometry-plan check, with no SolidWorks connection.")]
    public static Task<ExecutionResult> BuildModel(ModelingWorkflow workflow, ModelingIrValidator validator,
        [Description("The ir_json returned by cad_create_model_plan.")] string irJson,
        bool dryRun = false, CancellationToken cancellationToken = default)
    {
        var plan = ModelingIrJson.Deserialize(irJson);
        if (!dryRun) return workflow.ExecuteAsync(plan, false, cancellationToken);
        var validation = validator.Validate(plan, forExecution: true);
        return Task.FromResult(new ExecutionResult(validation.IsValid,
            validation.IsValid ? "dry_run_passed" : "invalid_model",
            validation.IsValid ? $"{plan.Operations.Count} modeling operations checked; no SolidWorks document created." : "Correct the invalid model parameters.",
            validation.Diagnostics.Select(d => new ExecutionEvidence("model_parameters", d.Message,
                d.Severity != DiagnosticSeverity.Error, Code: d.Code, SuggestedAction: d.SuggestedAction)).ToArray()));
    }

    [McpServerTool(Name = "cad_executor_health", ReadOnly = false, Destructive = false,
        Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Start/check the local SolidWorks executor and connection. Use only for modeling or troubleshooting; may start SolidWorks.")]
    public static Task<ExecutorHealth> Health(ModelingWorkflow workflow, CancellationToken cancellationToken) =>
        workflow.HealthAsync(cancellationToken);

    private static string AbsolutePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("Use an absolute output path.");
        return Path.GetFullPath(path);
    }
}
