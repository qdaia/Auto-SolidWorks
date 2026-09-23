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
builder.Services.AddSingleton<ArtifactCacheStore>();
builder.Services.AddSingleton<IModelingExecutor>(_ => new AutoStartingNamedPipeModelingExecutor(new()
{
    PipeName = Environment.GetEnvironmentVariable("CAD_SOLIDWORKS_PIPE") ?? "auto-solidworks-modeling",
    ExecutablePath = Environment.GetEnvironmentVariable("CAD_SOLIDWORKS_EXECUTOR"),
    LogDirectory = Environment.GetEnvironmentVariable("CAD_EXECUTOR_LOG_DIR")
}));
builder.Services.AddSingleton<ModelingWorkflow>();
builder.Services.AddSingleton<Stage4SavedModelAcceptanceWorkflow>();
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
    [McpServerTool(Name="cad_review_drawing_coverage",ReadOnly=true,Destructive=false,Idempotent=true,OpenWorld=false,UseStructuredContent=true)]
    [Description("Read the source-derived omission inventory and raw review crops, or check an agent's drawing_context against it. Returns unaccounted annotations/geometry/residual ink, missing region reviews, count mismatches and declared cross-view conflicts. Does not start SolidWorks. Passing means declared review accounting, not proof of complete drawing understanding. Use offset/limit to page candidates, regions and issues independently by their returned totals; pageNumber optionally narrows the page.")]
    public static object ReviewDrawingCoverage(string inventoryPath, DrawingPlanContext? drawingContext=null,
        int? pageNumber=null, int offset=0, int limit=50, bool onlyUnresolved=true)
    {
        if(offset<0 || limit is <1 or >200) throw new ArgumentException("offset must be nonnegative and limit within 1..200.");
        inventoryPath=AbsolutePath(inventoryPath);
        var inventory=DrawingOmissionValidation.Load(inventoryPath);
        if(pageNumber is { } page && (page<1 || page>inventory.TotalPages)) throw new ArgumentException("pageNumber is outside the source page range.");
        DrawingOmissionResult? review=null;
        if(drawingContext is not null)
        {
            if(drawingContext.OmissionReview is { } supplied && !string.Equals(AbsolutePath(supplied.InventoryPath),inventoryPath,StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("drawingContext refers to a different omission inventory.");
            review=DrawingOmissionValidation.Evaluate(drawingContext);
        }
        var issues=review?.Issues.Where(i=>pageNumber is null || i.PageNumber is null || i.PageNumber==pageNumber).ToArray()??[];
        var unresolvedCandidates=issues.Where(i=>i.CandidateId is not null).Select(i=>i.CandidateId!).ToHashSet(StringComparer.Ordinal);
        var unresolvedRegions=issues.Where(i=>i.RegionId is not null).Select(i=>i.RegionId!).ToHashSet(StringComparer.Ordinal);
        // Global integrity failures must not make the UI hide all candidates as if they were accounted for.
        var canFilter=review is not null && !issues.Any(i=>i.CandidateId is null && i.RegionId is null);
        var candidates=inventory.Candidates.Where(c=>pageNumber is null || c.PageNumber==pageNumber)
            .Where(c=>!onlyUnresolved || !canFilter || unresolvedCandidates.Contains(c.Id)).ToArray();
        var regions=inventory.Regions.Where(r=>pageNumber is null || r.PageNumber==pageNumber)
            .Where(r=>!onlyUnresolved || !canFilter || unresolvedRegions.Contains(r.Id)).ToArray();
        return new {
            inventory_path=inventoryPath,inventory_sha256=DrawingPlanValidation.FileHash(inventoryPath),
            status=review?.Status??"requires_visual_review",full_drawing_equivalence=false,
            scope="Candidate accounting and declared review only. Inspect original images; detection and review declarations can both miss features.",
            source_path=inventory.SourcePath,total_pages=inventory.TotalPages,complete_extraction=inventory.CompleteExtraction,
            limitations=inventory.Limitations,pages=inventory.Pages.Where(p=>pageNumber is null||p.PageNumber==pageNumber).ToArray(),
            total_candidates=candidates.Length,total_regions=regions.Length,total_issues=issues.Length,
            accounted_candidates=review?.AccountedCandidates,reviewed_regions=review?.ReviewedRegions,
            offset,limit,candidates=candidates.Skip(offset).Take(limit).ToArray(),regions=regions.Skip(offset).Take(limit).ToArray(),issues=issues.Skip(offset).Take(limit).ToArray(),
            next_offset=offset+limit<Math.Max(candidates.Length,Math.Max(regions.Length,issues.Length))?(int?)(offset+limit):null
        };
    }
    [McpServerTool(Name="cad_export_drawing",ReadOnly=false,Destructive=false,Idempotent=false,OpenWorld=false,UseStructuredContent=true)]
    [Description("Export an existing native part to a first-angle A3 engineering drawing and PDF with front/top/left/isometric views and imported native dimensions. Source file is preserved. Outputs must not already exist. Returns dimension placement gaps; a model-derived drawing is not an independent drawing benchmark or a complete manufacturing definition.")]
    public static Task<DrawingExportResult> ExportDrawing(IModelingExecutor executor,string inputPath,string nativePath,string pdfPath,string? templatePath=null,CancellationToken cancellationToken=default) =>
        executor.ExportDrawingAsync(new(inputPath,nativePath,pdfPath,templatePath),cancellationToken);
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
    [Description("Read a local model's features, native dimensions, geometry and optional face/edge queries. Optional verification checks source-declared cylindrical walls, count, axis locations, depths, bounding boxes, native dimensions and source-derived local trimmed-surface/clearance samples. Opens closed native files read-only; imports STEP/STP through an isolated working copy. May start SolidWorks.")]
    public static Task<ModelInspection> InspectModel(IModelingExecutor executor,string inputPath,
        EntityQuery[]? queries=null,ModelVerificationSpec? verification=null,CancellationToken cancellationToken=default,
        GeometryRef[]? geometryReferences=null,MeasurementQuery[]? measurements=null,string? documentRevision=null,
        string? sourceRevisionId=null,ConnectivityInspectionQuery[]? connectivityQueries=null,NativeEditabilityProbeSpec[]? editabilityProbes=null)
    {
        var errors=ModelVerification.Validate((verification??new()) with { Bindings=[] },null).ToArray();
        // Standalone inspection reuses expected measurements; source binding was checked during compilation.
        if(errors.Length>0)
            throw new ArgumentException(string.Join(" ",errors.Select(e=>e.Message)));
        return executor.InspectAsync(new(Path.GetFullPath(inputPath),queries,verification,geometryReferences,measurements,
            documentRevision,sourceRevisionId,connectivityQueries,editabilityProbes),cancellationToken);
    }

    [McpServerTool(Name="cad_capture_projection",ReadOnly=true,Destructive=false,Idempotent=true,OpenWorld=false,UseStructuredContent=true)]
    [Description("Capture T09 visible/hidden projection primitives from a saved native part in a temporary drawing. Requires a frozen source SHA and view frame. A captured snapshot is evidence for source comparison, not drawing-equivalence acceptance.")]
    public static Task<ProjectionCaptureResult> CaptureProjection(IModelingExecutor executor,ProjectionCaptureRequest request,CancellationToken cancellationToken=default)=>
        executor.CaptureProjectionAsync(request,cancellationToken);

    [McpServerTool(Name="cad_capture_section",ReadOnly=true,Destructive=false,Idempotent=true,OpenWorld=false,UseStructuredContent=true)]
    [Description("Capture T10 bounded section contours from a saved native part using a frozen section specification. Unsupported or incomplete captures cannot certify section equivalence.")]
    public static Task<SectionCaptureResult> CaptureSection(IModelingExecutor executor,SectionCaptureRequest request,CancellationToken cancellationToken=default)=>
        executor.CaptureSectionAsync(request,cancellationToken);

    [McpServerTool(Name = "cad_get_capabilities", ReadOnly = true, Destructive = false,
        Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("List local part/assembly modeling operations, native feature kinds, units, formats, workflow and current limits.")]
    public static object Capabilities() => new
    {
        server_version = typeof(ModelingTools).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
        mode = "modeling_only",
        default_unit = "mm",
        input_formats = new[] { "text", "pdf", "png", "jpg", "jpeg", "tif", "tiff" },
        output_formats = new[] { "sldprt", "sldasm", "step", "stp", "stl", "slddrw", "pdf" },
        drawing_export = new { projection="FirstAngle",sheet="A3",source="native_part",views=new[]{"Front","Top","Left","Isometric"},dimension_placement_coverage=true,complete_manufacturing_definition=false },
        schema_version=ModelingIrSchema.CurrentVersion,
        operations = ModelingCapabilityCatalog.Current.Operations,
        native_feature_kinds=Enum.GetNames<NativeFeatureKind>(),
        sketch_primitive_kinds=Enum.GetNames<GenericPrimitiveKind>(),
        assembly_mate_kinds=Enum.GetNames<AssemblyMateKind>(),
        source_requirements = new { primary_interpreter="agent_vision",ocr="auxiliary_candidates",drawing_feature_coverage=true,critical_dimension_binding=true,compiled_binding_integrity=true },
        drawing_omission = new { independent_source_inventory=true,raw_overlapping_tiles=true,residual_ink_candidates=true,
            complete_plan_requires_review=true,compile_and_build_recheck=true,declared_cross_view_checks=true,
            full_drawing_equivalence=false,automatic_semantic_recall_certification=false,review_tool="cad_review_drawing_coverage" },
        measured_verification = new[] { "cylinder_diameter_axis_position_axial_extent_count", "interior_vs_exterior_cylinder", "native_dimensions", "model_bounding_box", "saved_native_readback", "trimmed_plane_cylinder_cone_samples", "outward_normal_and_cone_half_angle", "boundary_clearance_samples", "unique_sampled_face_area" },
        local_geometry = new { maximum_sample_points=512, surface_kinds=new[]{"Plane","Cylinder","Cone"}, optional_unique_face_area=true, scope="Declared finite samples and touched-face area; no exact topology, material/void classification or thread certification." },
        local_recovery = new { enabled_by_default=true,mode="verified_prefix_and_suffix_replay",checkpoint_file_hash=true,source_requirements_preserved=true },
        natural_language_examples = ModelingCapabilityCatalog.Current.NaturalLanguageExamples.Skip(1).ToArray(),
        workflow = "Read the drawing if supplied, create a typed model plan, then build and save it.",
        limitations = ModelingCapabilityCatalog.Current.Limitations
    };

    [McpServerTool(Name = "cad_read_drawing", ReadOnly = false, Destructive = false,
        Idempotent = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Read a local engineering image or PDF into page images, text, primitive observations and an independent omission inventory with overlapping raw review crops. Use cad_review_drawing_coverage to inspect candidates and unexplained ink before creating a complete drawing plan. Does not start SolidWorks.")]
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
            drawing_review_status = draft?.DrawingContext is null ? "not_applicable" : !compilation.Success ? "incomplete" :
                draft.DrawingContext.OmissionReview is null ? "partial_unreviewed" : "declared_review_complete",
            full_drawing_equivalence = false,
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

    [McpServerTool(Name="cad_verify_revolved_family",ReadOnly=true,Destructive=false,Idempotent=true,OpenWorld=false,UseStructuredContent=true)]
    [Description("Verify a T17 finite revolved-family profile against an actual saved/reopened native model. The inspection binding independently maps every source step fact to required native diameter/axial-length dimensions; probe requests cannot define the required set. T08/T09 inputs must be producer-owned reports.")]
    public static Task<Stage4SavedModelAcceptance<RevolvedFamilyObservation>> VerifyRevolvedFamily(
        Stage4SavedModelAcceptanceWorkflow workflow,string nativePath,RevolvedFamilyProfile profile,RevolvedFamilyInspectionBinding binding,Stage4EvidenceBundle evidence,CancellationToken cancellationToken=default)=>
        workflow.VerifyRevolvedAsync(AbsolutePath(nativePath),profile,binding,evidence,cancellationToken);

    [McpServerTool(Name="cad_verify_hole_group",ReadOnly=true,Destructive=false,Idempotent=true,OpenWorld=false,UseStructuredContent=true)]
    [Description("Verify a T18 finite hole group against an actual saved/reopened native model, matching every measured instance and native pattern. Supported analytic countersinks require complete trimmed cone boundaries; cosmetic threads require native designation, size, depth/mode and entrance binding. Missing or ambiguous acquisition remains unverifiable. Requires per-hole T08 and frozen T09 evidence; does not certify physical helical threads.")]
    public static Task<Stage4SavedModelAcceptance<HoleGroupObservation>> VerifyHoleGroup(
        Stage4SavedModelAcceptanceWorkflow workflow,string nativePath,HoleGroupProfile profile,HoleGroupInspectionBinding binding,Stage4EvidenceBundle evidence,CancellationToken cancellationToken=default)=>
        workflow.VerifyHoleGroupAsync(AbsolutePath(nativePath),profile,binding,evidence,cancellationToken);

    [McpServerTool(Name="cad_verify_edge_treatment",ReadOnly=true,Destructive=false,Idempotent=true,OpenWorld=false,UseStructuredContent=true)]
    [Description("Verify a T19 profile arc/solid fillet/chamfer against an actual saved/reopened native model. Reads named native dimensions and T06 geometry references; repaired candidates must provide the full T14 attempt/execution/coverage/check bundle plus T13 diff, which is recomputed by the final gate.")]
    public static Task<Stage4SavedModelAcceptance<EdgeTreatmentObservation>> VerifyEdgeTreatment(
        Stage4SavedModelAcceptanceWorkflow workflow,string nativePath,EdgeTreatmentIntent intent,EdgeTreatmentInspectionBinding binding,Stage4RepairEvidenceBundle? repair=null,CancellationToken cancellationToken=default)=>
        workflow.VerifyEdgeTreatmentAsync(AbsolutePath(nativePath),intent,binding,repair,cancellationToken);

    private static string AbsolutePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("Use an absolute output path.");
        return Path.GetFullPath(path);
    }
}
