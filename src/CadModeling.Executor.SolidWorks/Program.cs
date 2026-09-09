using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CadModeling.Core;
using CadModeling.Ir;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

return await ExecutorProgram.RunAsync(args);

[SupportedOSPlatform("windows")]
internal static class ExecutorProgram
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("The SolidWorks COM executor requires Windows.");
            return 2;
        }

        var executor = new SolidWorksComExecutor();
        var command = args.FirstOrDefault()?.ToLowerInvariant() ?? "help";
        switch (command)
        {
            case "serve":
                var pipeName = Option(args, "--pipe") ?? "cad-modeling-solidworks";
                var parentPid = int.TryParse(Option(args, "--parent-pid"), out var parsedParentPid)
                    ? parsedParentPid
                    : (int?)null;
                Console.Error.WriteLine($"SolidWorks executor listening on named pipe '{pipeName}'. COM calls are serialized.");
                using (var shutdown = new CancellationTokenSource())
                {
                    var parentMonitor = parentPid is null
                        ? Task.CompletedTask
                        : MonitorParentAsync(parentPid.Value, shutdown);
                    try
                    {
                        await new ExecutorPipeServer(pipeName, executor).RunAsync(shutdown.Token);
                    }
                    catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
                    await parentMonitor;
                }
                return 0;
            case "health":
                Console.WriteLine(JsonSerializer.Serialize(await executor.HealthAsync(), ModelingIrJson.Options));
                return 0;
            case "execute":
                var irPath = Option(args, "--ir");
                if (irPath is null || !File.Exists(irPath))
                {
                    Console.Error.WriteLine("Usage: execute --ir <absolute-plan.json> [--dry-run]");
                    return 2;
                }
                var plan = ModelingIrJson.Deserialize(await File.ReadAllTextAsync(irPath));
                var result = await executor.ExecuteAsync(plan, args.Contains("--dry-run", StringComparer.OrdinalIgnoreCase));
                Console.WriteLine(JsonSerializer.Serialize(result, ModelingIrJson.Options));
                return result.Success ? 0 : 1;
            default:
                Console.WriteLine("CadModeling.Executor.SolidWorks\n  serve [--pipe NAME]\n  health\n  execute --ir PLAN.json [--dry-run]");
                return 0;
        }
    }

    private static async Task MonitorParentAsync(int parentPid, CancellationTokenSource shutdown)
    {
        try
        {
            using var parent = Process.GetProcessById(parentPid);
            await parent.WaitForExitAsync(shutdown.Token);
            shutdown.Cancel();
        }
        catch (ArgumentException)
        {
            shutdown.Cancel();
        }
        catch (OperationCanceledException) { }
    }

    private static string? Option(IReadOnlyList<string> args, string name)
    {
        for (var i = 0; i < args.Count - 1; i++)
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        return null;
    }
}

internal sealed class ExecutorPipeServer(string pipeName, IModelingExecutor executor)
{
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await using var pipe = new NamedPipeServerStream(
                pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            await pipe.WaitForConnectionAsync(cancellationToken);
            using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            ExecutorServiceResponse response;
            try
            {
                var line = await reader.ReadLineAsync(cancellationToken);
                var request = line is null
                    ? null
                    : JsonSerializer.Deserialize<ExecutorServiceRequest>(line, ModelingIrJson.Options);
                response = request?.Action.ToLowerInvariant() switch
                {
                    "health" => new(Health: await executor.HealthAsync(cancellationToken)),
                    "inspect" when request.Inspection is not null => new(Inspection: await executor.InspectAsync(request.Inspection, cancellationToken)),
                    "drawing" when request.Drawing is not null => new(Drawing: await executor.ExportDrawingAsync(request.Drawing, cancellationToken)),
                    "assembly" when request.Assembly is not null => new(Assembly: await executor.BuildAssemblyAsync(request.Assembly,cancellationToken)),
                    "execute" when request.Plan is not null =>
                        new(Execution: await executor.ExecuteAsync(request.Plan, request.DryRun, cancellationToken)),
                    _ => new(Error: "Unknown action or missing Modeling IR plan.")
                };
            }
            catch (Exception ex)
            {
                response = new(Error: $"Executor request failed: {ex.Message}");
            }
            await writer.WriteLineAsync(JsonSerializer.Serialize(response, ModelingIrJson.Options));
        }
    }
}

[SupportedOSPlatform("windows")]
internal sealed partial class SolidWorksComExecutor : IModelingExecutor, IDisposable
{
    private readonly SemaphoreSlim _serialGate = new(1, 1);
    private readonly StaWorker _sta = new();

    public async Task<ExecutorHealth> HealthAsync(CancellationToken cancellationToken = default)
    {
        await _serialGate.WaitAsync(cancellationToken);
        try { return await _sta.InvokeAsync(CheckHealth, cancellationToken); }
        finally { _serialGate.Release(); }
    }

    public async Task<ExecutionResult> ExecuteAsync(ModelingPlan plan, bool dryRun, CancellationToken cancellationToken = default)
    {
        await _serialGate.WaitAsync(cancellationToken);
        try
        {
            var validation = new ModelingIrValidator().Validate(plan, forExecution: true);
            if (!validation.IsValid)
                return new(false, "rejected", "IR validation failed before COM execution.",
                    validation.Diagnostics.Select(x => new ExecutionEvidence(
                        "validation", $"{x.Code}: {x.Message}", false, Code: x.Code,
                        Category: ExecutionFailureCategory.Validation,
                        SuggestedAction: x.SuggestedAction)).ToArray(),
                    PlanFingerprint: ModelingPlanIdentity.Fingerprint(plan));
            if (dryRun)
                return new(true, "dry_run", "IR validated; SolidWorks was not mutated.",
                    plan.Operations.Select(x => new ExecutionEvidence("operation", $"Would execute {x.Id}: {x.Name}.", true)).ToArray(),
                    plan.Output.NativePath, PlanFingerprint: ModelingPlanIdentity.Fingerprint(plan));
            return await _sta.InvokeAsync(() => ExecuteOnSta(plan), cancellationToken);
        }
        finally { _serialGate.Release(); }
    }

    private static ExecutorHealth CheckHealth()
    {
        try
        {
            using var startupDialog = SolidWorksStartupDialogSuppressor.Start();
            var type = Type.GetTypeFromProgID("SldWorks.Application", throwOnError: false);
            if (type is null) return new(false, "solidworks-com", "SldWorks.Application is not registered.");
            var app = (SldWorks?)Activator.CreateInstance(type)
                      ?? throw new InvalidOperationException("COM activation returned null.");
            try
            {
                var suppression = startupDialog.WaitForResult(TimeSpan.FromSeconds(5));
                string revision = app.RevisionNumber() ?? "unknown";
                var active = app.IActiveDoc2;
                string? title = active?.GetTitle();
                var message = suppression.DialogHidden
                    ? suppression.NotificationAudioSilenced
                        ? "SolidWorks COM connection succeeded; the known false .NET Framework startup dialog and its notification sound were suppressed."
                        : "SolidWorks COM connection succeeded; the known false .NET Framework startup dialog was hidden."
                    : "SolidWorks COM connection succeeded.";
                return new(true, "solidworks-com", message, revision, title);
            }
            finally { ReleaseCom(app); }
        }
        catch (Exception ex)
        {
            return new(false, "solidworks-com", $"SolidWorks COM connection failed: {ex.Message}");
        }
    }

    private static ExecutionResult ExecuteOnSta(ModelingPlan plan)
    {
        var evidence = new List<ExecutionEvidence>();
        GeometrySnapshot? geometry = null;
        IReadOnlyList<StableFeatureReference>? featureReferences = null;
        var planFingerprint = ModelingPlanIdentity.Fingerprint(plan);
        SldWorks? app = null;
        IModelDoc2? model = null;
        bool? originalInputDimension = null;
        string? activeOperationId = null;
        string? checkpointPath = null;
        ModelingCheckpointManifest? checkpoint = null;
        ModelVerificationResult? verification = null;
        try
        {
            if(plan.Recovery.ResumeManifestPath is { } resumePath)
            {
                checkpoint=ModelingRecovery.ReadAndValidate(plan,resumePath);
                checkpointPath=resumePath;
            }
            using var startupDialog = SolidWorksStartupDialogSuppressor.Start();
            var type = Type.GetTypeFromProgID("SldWorks.Application", throwOnError: true)
                       ?? throw new InvalidOperationException("SldWorks.Application is not registered.");
            app = (SldWorks?)Activator.CreateInstance(type) ?? throw new InvalidOperationException("COM activation returned null.");
            var suppression = startupDialog.WaitForResult(TimeSpan.FromSeconds(5));
            if (suppression.DialogHidden)
                evidence.Add(Pass("startup_dialog",
                    suppression.NotificationAudioSilenced
                        ? "Suppressed the known false .NET Framework startup dialog and its notification sound without acknowledging the dialog."
                        : "Hid the known false .NET Framework startup dialog without acknowledging it.",
                    ("notification_audio", suppression.NotificationAudioSilenced ? "silenced-and-restored" : "not-confirmed")));
            app.Visible = true;
            var targetName=Path.GetFileName(plan.Output.NativePath);
            foreach(var open in (app.GetDocuments() as object[]??[]).Cast<IModelDoc2>())
                if(Path.GetFileName(open.GetPathName()).Equals(targetName,StringComparison.OrdinalIgnoreCase)||open.GetTitle().Equals(targetName,StringComparison.OrdinalIgnoreCase))
                    throw new CadExecutionException("OUTPUT_DOCUMENT_OPEN",ExecutionFailureCategory.Output,false,
                        "A document with the requested output filename is already open in SolidWorks.","Use a unique output filename so the existing document remains untouched.");
            originalInputDimension=app.GetUserPreferenceToggle((int)swUserPreferenceToggle_e.swInputDimValOnCreate);
            app.SetUserPreferenceToggle((int)swUserPreferenceToggle_e.swInputDimValOnCreate,false);
            evidence.Add(Pass("connect", "Connected to SolidWorks COM.", ("revision", app.RevisionNumber() ?? "unknown")));

            if ((checkpoint?.NativePath??plan.SourceModelPath) is { } sourceModel)
            {
                var target = Path.GetFullPath(plan.Output.NativePath!);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(sourceModel, target, plan.Output.OverwriteAllowed);
                int openErrors=0,openWarnings=0;
                model=(IModelDoc2?)app.OpenDoc6(target,(int)swDocumentTypes_e.swDocPART,(int)swOpenDocOptions_e.swOpenDocOptions_Silent,"",ref openErrors,ref openWarnings);
                if(model is null) throw new IOException($"Cannot open model copy (errors={openErrors}).");
                if(!Path.GetFullPath(model.GetPathName()).Equals(target,StringComparison.OrdinalIgnoreCase))
                { model=null; throw new IOException("SolidWorks returned another open document instead of the edit copy. Use a unique output filename."); }
                evidence.Add(Pass("document", "Opened a separate model copy for editing.", ("source",sourceModel),("output",target)));
            }
            else
            {
                var template = FindPartTemplate(app);
                model = (IModelDoc2?)app.NewDocument(template, 0, 0d, 0d);
                if (model is null) throw new InvalidOperationException("NewDocument returned null. Check the configured part template.");
                evidence.Add(Pass("document", "Created a new part document.", ("template", template)));
            }

            var operationObjects = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            var completed=checkpoint?.CompletedOperationCount??0;
            if(checkpoint is not null)
            {
                RestoreCheckpointFeatures(model,checkpoint,operationObjects);
                evidence.Add(Pass("checkpoint_resume","Reused the saved prefix and restored its actual feature references.",("completed_operations",completed.ToString(CultureInfo.InvariantCulture)),("manifest",checkpointPath!)));
            }
            var checkpointAfter=ModelingRecovery.CheckpointAfter(plan);
            var mathUtility = (IMathUtility)app.GetMathUtility();
            foreach (var operation in plan.Operations.Skip(completed))
            {
                activeOperationId=operation.Id;
                try
                {
                    switch (operation)
                    {
                        case NativeFeatureOperation native:
                            operationObjects[native.Id] = ExecuteNativeFeature(model, native, operationObjects, mathUtility);
                            evidence.Add(Pass("feature", $"Created {native.Options.Kind} '{native.Name}'.", ("id", native.Id)));
                            break;
                        case ProfileSketchOperation sketch:
                            operationObjects[sketch.Id] = ExecuteSketch(model, sketch, mathUtility, operationObjects);
                            evidence.Add(Pass("sketch", $"Created profile sketch '{sketch.Name}'.", ("id", sketch.Id)));
                            break;
                        case ExtrudeBossOperation extrude:
                            var feature = ExecuteBossExtrude(model, extrude, operationObjects);
                            operationObjects[extrude.Id] = feature;
                            evidence.Add(Pass("feature", $"Created boss extrude '{extrude.Name}'.",
                                ("depth_mm", extrude.DepthMm.ToString(CultureInfo.InvariantCulture)),
                                ("end_condition", extrude.EndCondition.ToString())));
                            break;
                        case ExtrudeCutOperation cut:
                            var cutFeature = ExecuteCutExtrude(model, cut, operationObjects);
                            operationObjects[cut.Id] = cutFeature;
                            evidence.Add(Pass("feature", $"Created cut extrude '{cut.Name}'.",
                                ("depth_mm", cut.DepthMm.ToString(CultureInfo.InvariantCulture)),
                                ("end_condition", cut.EndCondition.ToString())));
                            break;
                        default:
                            throw new NotSupportedException($"Unsupported operation type {operation.GetType().Name}.");
                    }
                }
                catch (Exception ex) when (ex is not CadExecutionException)
                {
                    throw WrapOperationFailure(operation, ex);
                }
                completed++;
                if(checkpointAfter.Contains(operation.Id))
                {
                    try
                    {
                        (checkpointPath,checkpoint)=SaveCheckpoint(app,model,plan,completed,operationObjects);
                        evidence.Add(Pass("checkpoint_saved","Saved and reopened a reusable prefix model.",("operation_id",operation.Id),("manifest",checkpointPath)));
                    }
                    catch(Exception checkpointError)
                    {
                        // Optional checkpoint failure must not fabricate a recoverable state or destroy a previous checkpoint.
                        evidence.Add(new("checkpoint_unavailable",checkpointError.Message,true,Code:"CHECKPOINT_UNAVAILABLE",SuggestedAction:"Use the last valid checkpoint if available; otherwise rebuild."));
                    }
                }
            }

            activeOperationId=null;

            var rebuildOk = Convert.ToBoolean(model.ForceRebuild3(false));
            if (!rebuildOk) throw new InvalidOperationException("ForceRebuild3 reported failure.");
            evidence.Add(Pass("rebuild", "SolidWorks rebuild completed without a reported error."));

            featureReferences = CaptureFeatureReferences(model, plan, operationObjects);
            evidence.Add(Pass("stable_references", "Mapped IR operation ids to SolidWorks feature identities and persistent references.",
                ("count", featureReferences.Count.ToString(CultureInfo.InvariantCulture)),
                ("persistent_count", featureReferences.Count(x => x.PersistentReferenceBase64 is not null).ToString(CultureInfo.InvariantCulture))));
            VerifyExpectedFeatures(model, plan.Acceptance, evidence);
            geometry = MeasureGeometry(model);
            evidence.Add(Pass("geometry_snapshot", "Measured SolidWorks geometry and mass properties.",
                ("solid_body_count", geometry.SolidBodyCount.ToString(CultureInfo.InvariantCulture)),
                ("face_count", geometry.FaceCount.ToString(CultureInfo.InvariantCulture)),
                ("edge_count", geometry.EdgeCount.ToString(CultureInfo.InvariantCulture)),
                ("volume_mm3", geometry.VolumeMm3.ToString("0.###", CultureInfo.InvariantCulture)),
                ("surface_area_mm2", geometry.SurfaceAreaMm2.ToString("0.###", CultureInfo.InvariantCulture)),
                ("bounding_box_mm", FormatBounds(geometry.BoundingBoxMm))));
            VerifyGeometryQuality(geometry, plan.Acceptance, evidence);
            if(ModelVerification.HasChecks(plan.Verification))
            {
                verification=VerifySourceRequirements(model,plan.Verification,geometry,out _);
                foreach(var check in verification.Checks)
                {
                    var measurements=check.Measurements is null?new Dictionary<string,string>():new Dictionary<string,string>(check.Measurements);
                    measurements["check_id"]=check.Id;
                    var affected=plan.DrawingContext?.Features.Where(f=>f.VerificationCheckIds.Contains(check.Id)).SelectMany(f=>f.OperationIds).Distinct().ToArray()??[];
                    measurements["affected_operation_ids"]=string.Join(",",affected);
                    evidence.Add(new("source_requirement",check.Message,check.Passed,measurements,check.Passed?null:"SOURCE_REQUIREMENT_MISMATCH",ExecutionFailureCategory.GeometryQuality,
                        !check.Passed,"Inspect measured feature geometry against the source requirement; do not weaken the expectation.",affected.Length==1?affected[0]:null));
                }
                if(!verification.Passed) throw new CadExecutionException("SOURCE_REQUIREMENT_MISMATCH",ExecutionFailureCategory.GeometryQuality,true,
                    "The actual model does not satisfy the declared source requirements.","Correct the offending feature and rebuild from a compatible checkpoint.");
            }
            var output = Path.GetFullPath(plan.Output.NativePath!);
            EnforceAllowedOutputRoot(output);
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            var saveErrors = 0;
            var saveWarnings = 0;
            PrepareModelPresentation(model);
            var saveOk = model.Extension.SaveAs(output, 0, 1, null, ref saveErrors, ref saveWarnings);
            if (!saveOk || !File.Exists(output) || new FileInfo(output).Length == 0)
                throw new IOException($"IModelDocExtension.SaveAs failed (errors={saveErrors}, warnings={saveWarnings}).");

            foreach (var exportPath in plan.Output.ExportPaths)
            {
                var export = Path.GetFullPath(exportPath);
                EnforceAllowedOutputRoot(export);
                Directory.CreateDirectory(Path.GetDirectoryName(export)!);
                var exportErrors = 0;
                var exportWarnings = 0;
                var exportOk = model.Extension.SaveAs(export, 0, 1, null, ref exportErrors, ref exportWarnings);
                if (!exportOk || !File.Exists(export) || new FileInfo(export).Length == 0)
                    throw new IOException($"Export SaveAs failed for '{export}' (errors={exportErrors}, warnings={exportWarnings}).");
                evidence.Add(Pass("export", "Exported an additional CAD artifact.",
                    ("path", export), ("bytes", new FileInfo(export).Length.ToString(CultureInfo.InvariantCulture)),
                    ("warnings", exportWarnings.ToString(CultureInfo.InvariantCulture))));
            }

            var previewPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var view in new[]
                     {
                         (Name: "isometric", Id: (int)swStandardViews_e.swIsometricView),
                         (Name: "front", Id: (int)swStandardViews_e.swFrontView),
                         (Name: "top", Id: (int)swStandardViews_e.swTopView),
                         (Name: "right", Id: (int)swStandardViews_e.swRightView),
                         (Name: "left", Id: (int)swStandardViews_e.swLeftView)
                     })
            {
                var preview = Path.Combine(
                    Path.GetDirectoryName(output)!,
                    $"{Path.GetFileNameWithoutExtension(output)}.{view.Name}.bmp");
                model.ShowNamedView2("", view.Id);
                model.ViewZoomtofit2();
                model.GraphicsRedraw2();
                var previewOk = Convert.ToBoolean(model.SaveBMP(preview, 1280, 960));
                if (!previewOk || !File.Exists(preview) || new FileInfo(preview).Length == 0)
                    throw new IOException($"SolidWorks did not export a non-empty {view.Name} preview.");
                previewPaths[view.Name] = preview;
                evidence.Add(Pass("preview", $"Exported {view.Name} review preview.",
                    ("view", view.Name), ("path", preview),
                    ("bytes", new FileInfo(preview).Length.ToString(CultureInfo.InvariantCulture))));
            }

            PrepareModelPresentation(model);
            if (!model.Save3((int)swSaveAsOptions_e.swSaveAsOptions_Silent, ref saveErrors, ref saveWarnings) || saveErrors != 0)
                throw new IOException($"Could not save final model presentation (errors={saveErrors}, warnings={saveWarnings}).");
            evidence.Add(Pass("presentation", "Saved an isometric view with construction geometry hidden; no features were suppressed."));

            var artifactStem = Path.Combine(Path.GetDirectoryName(output)!, Path.GetFileNameWithoutExtension(output));
            var parametersPath = artifactStem + "_parameters.json";
            File.WriteAllText(parametersPath, ModelingIrJson.Serialize(plan));
            if (new FileInfo(parametersPath).Length == 0)
                throw new IOException("The parameter report was empty.");
            evidence.Add(Pass("parameters", "Saved the modeling parameters and assumptions.",
                ("path", parametersPath), ("bytes", new FileInfo(parametersPath).Length.ToString(CultureInfo.InvariantCulture))));

            var reviewPath = artifactStem + "_review_report.json";
            var expectedOutputs = new List<string> { output, parametersPath };
            expectedOutputs.AddRange(plan.Output.ExportPaths);
            expectedOutputs.AddRange(previewPaths.Values);
            var review = new
            {
                evaluation = new { status = "pass", message = "Rebuild, feature, geometry quality, export, and preview checks passed." },
                checks = new
                {
                    rebuild_without_errors = true,
                    expected_features_exist = true,
                    expected_solid_body_count = geometry.SolidBodyCount == plan.Acceptance.Geometry.ExpectedSolidBodyCount,
                    positive_volume = !plan.Acceptance.Geometry.RequirePositiveVolume || geometry.VolumeMm3 > 0,
                    valid_topology = !plan.Acceptance.Geometry.RequireValidTopology || geometry.FaceCount > 0 && geometry.EdgeCount > 0,
                    expected_outputs_exist = expectedOutputs.All(path => File.Exists(path)),
                    previews_created = previewPaths.Count == 5,
                    previews_not_blank = previewPaths.Values.All(path => new FileInfo(path).Length > 0)
                },
                model = new
                {
                    name = plan.Name,
                    features = plan.Acceptance.ExpectedFeatures,
                    expected_bounding_box_mm = plan.Acceptance.ExpectedBoundingBoxMm,
                    geometry,
                    stable_feature_references = featureReferences,
                    plan_fingerprint = planFingerprint,
                    assumptions = plan.Assumptions
                },
                source_requirement_verification = verification,
                artifacts = new { native = output, exports = plan.Output.ExportPaths, previews = previewPaths, parameters = parametersPath }
            };
            var createdTitle = model.GetTitle();
            app.CloseDoc(createdTitle);
            ReleaseCom(model);
            model = null;
            if(ModelVerification.HasChecks(plan.Verification))
            {
                var reopened=InspectOnSta(new(output,Verification:plan.Verification),app);
                verification=reopened.Verification;
                if(!reopened.Success || verification is not { Passed:true })
                    throw new CadExecutionException("SAVED_REQUIREMENT_MISMATCH",ExecutionFailureCategory.GeometryQuality,true,
                        "Saved model read-back did not pass the source requirements.","Inspect the saved result; do not deliver it as verified.");
                evidence.Add(Pass("saved_source_requirements","Reopened the final native file and repeated the declared source requirement measurements."));
            }
            File.WriteAllText(reviewPath, JsonSerializer.Serialize(review,
                new JsonSerializerOptions(ModelingIrJson.Options) { WriteIndented = true }));
            evidence.Add(Pass("review_report", "Wrote the postflight report after saved-model verification.",
                ("path", reviewPath), ("bytes", new FileInfo(reviewPath).Length.ToString(CultureInfo.InvariantCulture))));
            evidence.Add(Pass("save", "Saved native SolidWorks part and closed only the document created by this run.",
                ("path", output), ("bytes", new FileInfo(output).Length.ToString(CultureInfo.InvariantCulture)),
                ("sha256", Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(output)))),
                ("warnings", saveWarnings.ToString(CultureInfo.InvariantCulture))));

            return new(true, "completed", "SolidWorks created, rebuilt, measured and saved the model.",
                evidence, output, geometry, featureReferences, planFingerprint) {Verification=verification,Recovery=RecoveryState(plan,checkpointPath,checkpoint,null)};
        }
        catch (Exception ex)
        {
            var failure = ClassifyFailure(ex);
            if(failure.OperationId is null&&activeOperationId is not null) failure=failure with {OperationId=activeOperationId};
            evidence.Add(new("failed", failure.Message, false, failure.Data, failure.Code, failure.Category,
                failure.Retryable, failure.SuggestedAction, failure.OperationId));
            if (app is not null && model is not null)
            {
                try
                {
                    var failedTitle = model.GetTitle();
                    app.CloseDoc(failedTitle);
                    evidence.Add(Pass("cleanup", "Closed the unsaved document created by the failed run.", ("title", failedTitle)));
                }
                catch (COMException) { }
                ReleaseCom(model);
                model = null;
            }
            return new(false, "failed", "SolidWorks execution stopped at the first failed checkpoint.", evidence,
                null, geometry, featureReferences, planFingerprint) {Verification=verification,Recovery=RecoveryState(plan,checkpointPath,checkpoint,failure.OperationId)};
        }
        finally
        {
            if(app is not null && originalInputDimension is { } inputDimension)
                try { app.SetUserPreferenceToggle((int)swUserPreferenceToggle_e.swInputDimValOnCreate,inputDimension); } catch(COMException) { }
            ReleaseCom(model);
            ReleaseCom(app);
        }
    }

    private static object ExecuteSketch(
        IModelDoc2 model,
        ProfileSketchOperation operation,
        IMathUtility mathUtility,
        IReadOnlyDictionary<string, object> objects)
    {
        model.ClearSelection2(true);
        _activeFrame = operation.Frame;
        if (operation.PlaneId is { } planeId)
        {
            SelectFeature(model, ResolveFeature(model, objects, planeId), false, 0);
        }
        else if (operation.Frame is { } frame)
        {
            var planeFeature = CreateFramePlane(model, frame, operation.Name + "_Plane");
            SelectFeature(model, planeFeature, false, 0);
        }
        else if (operation.FaceAttachment is { } attachment)
        {
            if (!objects.ContainsKey(attachment.SupportOperationId))
                throw new InvalidOperationException(
                    $"Face-attached sketch support '{attachment.SupportOperationId}' has not been created.");
            var selectedFace = Convert.ToBoolean(model.Extension.SelectByID2(
                string.Empty, "FACE", Mm(attachment.PickXmm), Mm(attachment.PickYmm), Mm(attachment.PickZmm), false, 0, null, 0));
            if (!selectedFace)
                throw new InvalidOperationException(
                    $"Could not select the planar support face at ({attachment.PickXmm:R}, {attachment.PickYmm:R}, {attachment.PickZmm:R}) mm.");
        }
        else
        {
            var candidates = operation.Plane switch
            {
                ReferencePlane.Front => new[] { "Front Plane", "前视基准面" },
                ReferencePlane.Top => new[] { "Top Plane", "上视基准面" },
                ReferencePlane.Right => new[] { "Right Plane", "右视基准面" },
                _ => throw new ArgumentOutOfRangeException(nameof(operation.Plane))
            };
            var selectedPlane = candidates.Any(name => Convert.ToBoolean(
                model.Extension.SelectByID2(name, "PLANE", 0d, 0d, 0d, false, 0, null, 0)));
            if (!selectedPlane) throw new InvalidOperationException($"Could not select {operation.Plane} reference plane in this SolidWorks language.");
        }

        model.SketchManager.InsertSketch(true);
        var activeSketch = (ISketch?)model.IGetActiveSketch2()
                           ?? throw new InvalidOperationException("SolidWorks did not expose the active sketch after InsertSketch.");
        var sketchToModel = activeSketch.ModelToSketchTransform.IInverse();
        var primitiveSegments = new List<object[]>();
        var sketchManager = model.SketchManager;
        var previousAddToDb = sketchManager.AddToDB;
        var previousDisplayWhenAdded = sketchManager.DisplayWhenAdded;
        // Typed coordinates must not depend on zoom, window visibility, snapping or automatic relations.
        // Restore both preferences before applying the explicitly requested constraints and dimensions.
        try
        {
        sketchManager.AddToDB = true;
        sketchManager.DisplayWhenAdded = false;
        foreach (var primitive in operation.Primitives)
        {
            var before = (activeSketch.GetSketchSegments() as object[] ?? []).Cast<ISketchSegment>().ToArray();
            object? created = primitive switch
            {
                CenteredRectangleProfile rectangle => CreateRectangle(model, mathUtility, sketchToModel, operation.Plane, rectangle),
                ThreePointRectangleProfile rectangle => CreateThreePointRectangle(model, mathUtility, sketchToModel, activeSketch, operation.Plane, rectangle),
                CircleProfile circle => CreateCircle(model, mathUtility, sketchToModel, operation.Plane, circle),
                PolygonProfile polygon => CreatePolygon(model, mathUtility, sketchToModel, activeSketch, operation.Plane, polygon),
                CompositeCurveProfile composite => CreateCompositeCurve(model, mathUtility, sketchToModel, operation.Plane, composite),
                EllipseProfile ellipse => CreateEllipse(model, mathUtility, sketchToModel, operation.Plane, ellipse),
                OpenCurveProfile open => CreateOpenCurve(model, mathUtility, sketchToModel, operation.Plane, open),
                SplineProfile spline => CreateSpline(model, mathUtility, sketchToModel, operation.Plane, spline),
                SketchPointsProfile points => points.Points.Select(p=>
                {
                    var q=PointInSketch(mathUtility,sketchToModel,operation.Plane,p.Xmm,p.Ymm);
                    return (object)(model.SketchManager.CreatePoint(q.X,q.Y,q.Z) ?? throw new InvalidOperationException("Sketch point creation failed."));
                }).ToArray(),
                _ => throw new NotSupportedException($"Unsupported profile primitive {primitive.GetType().Name}.")
            };
            if (created is null) throw new InvalidOperationException($"SolidWorks failed to create {primitive.GetType().Name}.");
            primitiveSegments.Add(primitive is SketchPointsProfile ? (object[])created : (activeSketch.GetSketchSegments() as object[] ?? []).Cast<ISketchSegment>().Except(before).Cast<object>().ToArray());
        }
        }
        finally
        {
            sketchManager.DisplayWhenAdded = previousDisplayWhenAdded;
            sketchManager.AddToDB = previousAddToDb;
        }
        ApplySketchEditing(model, activeSketch, operation, primitiveSegments, mathUtility, sketchToModel);
        model.SketchManager.InsertSketch(true);
        var sketchFeature = model.IFeatureByPositionReverse(0)
                            ?? throw new InvalidOperationException("Could not obtain the newly created sketch feature.");
        sketchFeature.Name = operation.Name;
        return sketchFeature;
    }

    private static object CreateRectangle(
        IModelDoc2 model,
        IMathUtility mathUtility,
        MathTransform sketchToModel,
        ReferencePlane plane,
        CenteredRectangleProfile rectangle)
    {
        var center = PointInSketch(mathUtility, sketchToModel, plane, rectangle.CenterXmm, rectangle.CenterYmm);
        var corner = PointInSketch(mathUtility, sketchToModel, plane,
            rectangle.CenterXmm + rectangle.WidthMm / 2d,
            rectangle.CenterYmm + rectangle.HeightMm / 2d);
        return model.SketchManager.CreateCenterRectangle(
                   center.X, center.Y, center.Z, corner.X, corner.Y, corner.Z)
               ?? throw new InvalidOperationException("CreateCenterRectangle returned null.");
    }

    private static object CreateCircle(
        IModelDoc2 model,
        IMathUtility mathUtility,
        MathTransform sketchToModel,
        ReferencePlane plane,
        CircleProfile circle)
    {
        var center = PointInSketch(mathUtility, sketchToModel, plane, circle.CenterXmm, circle.CenterYmm);
        return model.SketchManager.CreateCircleByRadius(center.X, center.Y, center.Z, Mm(circle.DiameterMm) / 2d)
               ?? throw new InvalidOperationException("CreateCircleByRadius returned null.");
    }

    private static object CreateThreePointRectangle(
        IModelDoc2 model,
        IMathUtility mathUtility,
        MathTransform sketchToModel,
        ISketch activeSketch,
        ReferencePlane plane,
        ThreePointRectangleProfile rectangle)
    {
        var first = PointInSketch(mathUtility, sketchToModel, plane, rectangle.Corner1.Xmm, rectangle.Corner1.Ymm);
        var second = PointInSketch(mathUtility, sketchToModel, plane, rectangle.Corner2.Xmm, rectangle.Corner2.Ymm);
        var third = PointInSketch(mathUtility, sketchToModel, plane, rectangle.Corner3.Xmm, rectangle.Corner3.Ymm);
        var beforeCount = SketchSegmentCount(activeSketch);
        var created = model.SketchManager.Create3PointCornerRectangle(
            first.X, first.Y, first.Z,
            second.X, second.Y, second.Z,
            third.X, third.Y, third.Z);
        var afterCount = SketchSegmentCount(activeSketch);
        if (created is null && afterCount <= beforeCount)
            throw new InvalidOperationException(
                $"Create3PointCornerRectangle added no geometry; sketch segment count remained {beforeCount}.");
        return created ?? activeSketch;
    }

    private static object CreatePolygon(
        IModelDoc2 model,
        IMathUtility mathUtility,
        MathTransform sketchToModel,
        ISketch activeSketch,
        ReferencePlane plane,
        PolygonProfile polygon)
    {
        var sketchManager = model.SketchManager;
        var previousAddToDatabase = sketchManager.AddToDB;
        sketchManager.AddToDB = true;
        try
        {
            var sketchPoints = polygon.Points.Select(point =>
                PointInSketch(mathUtility, sketchToModel, plane, point.Xmm, point.Ymm)).ToArray();
            for (var index = 0; index < polygon.Points.Count; index++)
            {
                var start = sketchPoints[index];
                var end = sketchPoints[(index + 1) % sketchPoints.Length];
                // ISketchManager takes sketch coordinates. The legacy IModelDoc2
                // point/line overload can fault on an arbitrary reference plane.
                _ = sketchManager.CreateLine(start.X, start.Y, start.Z, end.X, end.Y, end.Z)
                    ?? throw new InvalidOperationException($"CreateLine did not add polygon segment {index}.");
            }
        }
        finally
        {
            sketchManager.AddToDB = previousAddToDatabase;
        }
        return activeSketch;
    }

    private static object CreateCompositeCurve(
        IModelDoc2 model,
        IMathUtility mathUtility,
        MathTransform sketchToModel,
        ReferencePlane plane,
        CompositeCurveProfile composite)
    {
        var sketchManager = model.SketchManager;
        var previousAddToDatabase = sketchManager.AddToDB;
        sketchManager.AddToDB = true;
        try
        {
            foreach (var curve in composite.Curves)
            {
                var start = PointInSketch(mathUtility, sketchToModel, plane, curve.Start.Xmm, curve.Start.Ymm);
                var end = PointInSketch(mathUtility, sketchToModel, plane, curve.End.Xmm, curve.End.Ymm);
                object? created = curve switch
                {
                    LineProfileCurve => sketchManager.CreateLine(start.X, start.Y, start.Z, end.X, end.Y, end.Z),
                    ThreePointArcProfileCurve arc => CreateThreePointArc(sketchManager, mathUtility, sketchToModel, plane, start, end, arc),
                    _ => throw new NotSupportedException($"Unsupported composite profile curve {curve.GetType().Name}.")
                };
                if (created is null)
                    throw new InvalidOperationException($"SolidWorks failed to create {curve.GetType().Name} in a composite curve profile.");
            }
        }
        finally
        {
            sketchManager.AddToDB = previousAddToDatabase;
        }
        return model.IGetActiveSketch2()
               ?? throw new InvalidOperationException("SolidWorks did not retain the active composite-curve sketch.");
    }

    private static object CreateThreePointArc(
        ISketchManager sketchManager,
        IMathUtility mathUtility,
        MathTransform sketchToModel,
        ReferencePlane plane,
        SketchPoint start,
        SketchPoint end,
        ThreePointArcProfileCurve arc)
    {
        var onArc = PointInSketch(mathUtility, sketchToModel, plane, arc.PointOnArc.Xmm, arc.PointOnArc.Ymm);
        return sketchManager.Create3PointArc(
                   start.X, start.Y, start.Z,
                   end.X, end.Y, end.Z,
                   onArc.X, onArc.Y, onArc.Z)
               ?? throw new InvalidOperationException("Create3PointArc returned null.");
    }

    private static int SketchSegmentCount(ISketch sketch)
    {
        var value = sketch.GetSketchSegments();
        return value is Array segments ? segments.Length : 0;
    }

    private static object ExecuteBossExtrude(IModelDoc2 model, ExtrudeBossOperation operation, IReadOnlyDictionary<string, object> objects)
    {
        model.ClearSelection2(true);
        var sketchFeature = (IFeature)objects[operation.SketchId];
        if (!Convert.ToBoolean(sketchFeature.Select2(false, 0)))
            throw new InvalidOperationException($"Could not select sketch '{operation.SketchId}' for extrusion.");
        SelectEndReference(model, operation.EndCondition, operation.EndReference, objects);
        var endCondition = EndCondition(operation.EndCondition);
        var startCondition = operation.StartOffsetMm > 0
            ? (int)swStartConditions_e.swStartOffset
            : (int)swStartConditions_e.swStartSketchPlane;
        var feature = (IFeature?)model.FeatureManager.FeatureExtrusion3(
            true, false, operation.ReverseDirection, endCondition, 0, Mm(operation.DepthMm), 0d,
            false, false, false, false, 0d, 0d, false, false, false, false,
            operation.Merge, false, true, startCondition, Mm(operation.StartOffsetMm), operation.ReverseStartOffset);
        if (feature is null) throw new InvalidOperationException("FeatureExtrusion3 returned null.");
        feature.Name = operation.Name;
        return feature;
    }

    private static object ExecuteCutExtrude(IModelDoc2 model, ExtrudeCutOperation operation, IReadOnlyDictionary<string, object> objects)
    {
        model.ClearSelection2(true);
        var sketchFeature = (IFeature)objects[operation.SketchId];
        if (!Convert.ToBoolean(sketchFeature.Select2(false, 0)))
            throw new InvalidOperationException($"Could not select sketch '{operation.SketchId}' for cut extrusion.");
        SelectEndReference(model, operation.EndCondition, operation.EndReference, objects);
        var endCondition = EndCondition(operation.EndCondition);
        var startCondition = operation.StartOffsetMm > 0
            ? (int)swStartConditions_e.swStartOffset
            : (int)swStartConditions_e.swStartSketchPlane;
        var feature = (IFeature?)model.FeatureManager.FeatureCut4(
            true, false, !operation.ReverseDirection, endCondition, 0, Mm(operation.DepthMm), 0d,
            false, false, false, false, 0d, 0d, false, false, false, false,
            false, false, true, false, false, false, startCondition, Mm(operation.StartOffsetMm), operation.ReverseStartOffset, true);
        if (feature is null) throw new InvalidOperationException("FeatureCut4 returned null.");
        feature.Name = operation.Name;
        return feature;
    }

    private static int EndCondition(ExtrudeEndCondition condition) => condition switch
    {
        ExtrudeEndCondition.Blind => (int)swEndConditions_e.swEndCondBlind,
        ExtrudeEndCondition.MidPlane => (int)swEndConditions_e.swEndCondMidPlane,
        ExtrudeEndCondition.UpToSurface => (int)swEndConditions_e.swEndCondUpToSurface,
        ExtrudeEndCondition.ThroughAll => (int)swEndConditions_e.swEndCondThroughAll,
        ExtrudeEndCondition.UpToNext => (int)swEndConditions_e.swEndCondUpToNext,
        _ => throw new NotSupportedException($"Unsupported extrude end condition {condition}.")
    };

    private static void SelectEndReference(
        IModelDoc2 model,
        ExtrudeEndCondition endCondition,
        PlanarFaceReference? reference,
        IReadOnlyDictionary<string, object> objects)
    {
        if (endCondition != ExtrudeEndCondition.UpToSurface) return;
        if (reference is null)
            throw new InvalidOperationException("UpToSurface extrusion requires an end reference.");
        if (!objects.TryGetValue(reference.SupportOperationId, out var supportObject) || supportObject is not IFeature)
            throw new InvalidOperationException(
                $"End-reference support '{reference.SupportOperationId}' has not been created.");
        var targetX = Mm(reference.PickXmm);
        var targetY = Mm(reference.PickYmm);
        var targetZ = Mm(reference.PickZmm);
        var part = (IPartDoc)model;
        // Resolve against the current solid because downstream cuts can split or replace faces first created
        // by the anchored support feature. The support id identifies the owning feature.
        var bodies = part.GetBodies2((int)swBodyType_e.swSolidBody, true) as Array;
        IFace2? nearestFace = null;
        var nearestDistanceSquared = double.PositiveInfinity;
        if (bodies is not null)
        {
            foreach (var face in bodies.Cast<object>().OfType<IBody2>()
                         .SelectMany(body => (body.GetFaces() as Array)?.Cast<object>().OfType<IFace2>() ?? []))
            {
                if (face.GetClosestPointOn(targetX, targetY, targetZ) is not double[] closest || closest.Length < 3)
                    continue;
                var dx = closest[0] - targetX;
                var dy = closest[1] - targetY;
                var dz = closest[2] - targetZ;
                var distanceSquared = (dx * dx) + (dy * dy) + (dz * dz);
                if (distanceSquared >= nearestDistanceSquared) continue;
                nearestFace = face;
                nearestDistanceSquared = distanceSquared;
            }
        }
        const double facePointToleranceMeters = 1e-6;
        if (nearestFace is null || nearestDistanceSquared > facePointToleranceMeters * facePointToleranceMeters)
        {
            var rawBox = part.GetPartBox(true) as double[];
            var box = rawBox is { Length: >= 6 }
                ? $"[{rawBox[0] * 1000d:R}, {rawBox[1] * 1000d:R}, {rawBox[2] * 1000d:R}]..[{rawBox[3] * 1000d:R}, {rawBox[4] * 1000d:R}, {rawBox[5] * 1000d:R}] mm"
                : "unavailable";
            throw new InvalidOperationException(
                $"Could not resolve the extrusion end face anchored by '{reference.SupportOperationId}' at ({reference.PickXmm:R}, {reference.PickYmm:R}, {reference.PickZmm:R}) mm; nearest face distance was {Math.Sqrt(nearestDistanceSquared) * 1000d:R} mm and the current solid box was {box}.");
        }
        var selectData = ((ISelectionMgr)model.SelectionManager).CreateSelectData();
        selectData.Mark = 1;
        var selected = ((IEntity)nearestFace).Select4(true, selectData);
        if (!selected)
            throw new InvalidOperationException(
                $"Could not select the extrusion end face at ({reference.PickXmm:R}, {reference.PickYmm:R}, {reference.PickZmm:R}) mm.");
    }

    private static SketchPoint PointInSketch(
        IMathUtility mathUtility,
        MathTransform sketchToModel,
        ReferencePlane plane,
        double uMm,
        double vMm)
    {
        if (_activeFrame is { } frame)
        {
            var (u, v, _) = FrameBasis(frame);
            var origin = frame.OriginMm;
            var point = (IMathPoint)mathUtility.CreatePoint(new[] {
                Mm(origin.X + u.X*uMm + v.X*vMm), Mm(origin.Y + u.Y*uMm + v.Y*vMm), Mm(origin.Z + u.Z*uMm + v.Z*vMm) });
            var local = (IMathPoint)point.MultiplyTransform(sketchToModel.IInverse());
            var data = (double[])local.ArrayData;
            // Creation APIs consume 2D sketch coordinates. Remove only floating-point plane residuals;
            // a genuinely inconsistent frame must fail rather than being projected onto another plane.
            if (Math.Abs(data[2]) > 1e-8)
                throw new InvalidOperationException($"Sketch frame point lies {data[2]:R} m off the active sketch plane.");
            return new(data[0], data[1], 0d);
        }
        var (sketchU, sketchV) = plane switch
        {
            ReferencePlane.Front => (uMm, vMm),
            ReferencePlane.Top => (uMm, -vMm),
            ReferencePlane.Right => (-uMm, vMm),
            _ => throw new ArgumentOutOfRangeException(nameof(plane))
        };
        // SketchManager creation APIs consume coordinates in the active sketch coordinate system.
        // Top-plane IR coordinates are (width, depth); Right-plane coordinates are (depth, height).
        return new(Mm(sketchU), Mm(sketchV), 0d);
    }

    private static GeometrySnapshot MeasureGeometry(IModelDoc2 model)
    {
        var part = (IPartDoc)model;
        var bodies = new List<IBody2>();
        var rawBodies = part.GetBodies2((int)swBodyType_e.swSolidBody, true);
        if (rawBodies is Array bodyArray)
            bodies.AddRange(bodyArray.Cast<object>().OfType<IBody2>());
        var solidBodyCount=bodies.Count;
        var surfaces=part.GetBodies2((int)swBodyType_e.swSheetBody,true) as object[] ?? [];
        bodies.AddRange(surfaces.Cast<IBody2>());
        if(bodies.Count==0) throw new InvalidOperationException("No solid or surface bodies exist in the rebuilt model.");

        try
        {
            // PartBox can include visible construction sketches/axes. Measure bodies only.
            var box = new[] { double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity,
                double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity };
            foreach (var body in bodies)
            for (var axis = 0; axis < 3; axis++)
            foreach (var sign in new[] { -1, 1 })
            {
                if (!body.GetExtremePoint(axis == 0 ? sign : 0, axis == 1 ? sign : 0, axis == 2 ? sign : 0,
                    out var x, out var y, out var z)) throw new InvalidOperationException("Cannot measure body extreme point.");
                var value = axis == 0 ? x : axis == 1 ? y : z;
                box[axis] = Math.Min(box[axis], value); box[axis + 3] = Math.Max(box[axis + 3], value);
            }
            if (box.Length < 6)
                throw new CadExecutionException("GEOMETRY_ENVELOPE_UNAVAILABLE", ExecutionFailureCategory.GeometryQuality,
                    false, "SolidWorks returned an incomplete part bounding box.",
                    "Rebuild the part and inspect whether a valid solid body exists.");

            IMassProperty2? massProperty = null;
            try
            {
                massProperty = solidBodyCount>0 ? model.Extension.CreateMassProperty2() as IMassProperty2 : null;
                if (massProperty is null && solidBodyCount>0)
                    throw new CadExecutionException("MASS_PROPERTIES_UNAVAILABLE", ExecutionFailureCategory.GeometryQuality,
                        true, "SolidWorks could not create a mass-property evaluator.",
                        "Rebuild the model and retry; verify that the document contains a solid body.");
                if(massProperty is not null)
                {
                    massProperty.UseSystemUnits=true;
                    massProperty.IncludeHiddenBodiesOrComponents=true;
                    massProperty.AccuracyLevel=(int)swMassPropertyAccuracyLevel_e.swMassPropertyAccuracyLevel_Higher;
                    if(!massProperty.Recalculate()) throw new InvalidOperationException("Mass properties could not be recalculated.");
                }
                var center = massProperty is not null?ToDoubles(massProperty.CenterOfMass, 3, "center of mass"):new double[3];
                var area=bodies.SelectMany(b=>(b.GetFaces() as object[] ?? []).Cast<IFace2>()).Sum(f=>f.GetArea());
                if(solidBodyCount==0)
                {
                    var sheetMass=bodies.Select(b=>(double[])b.GetMassProperties(1)).ToArray();
                    var totalArea=sheetMass.Sum(m=>m[3]);
                    if(totalArea<=0) throw new InvalidOperationException("Surface bodies have no measurable area.");
                    center=Enumerable.Range(0,3).Select(axis=>sheetMass.Sum(m=>m[axis]*m[3])/totalArea).ToArray();
                }
                return new(
                    solidBodyCount,
                    bodies.Sum(body => body.GetFaceCount()),
                    bodies.Sum(body => body.GetEdgeCount()),
                    (massProperty?.Volume??0) * 1_000_000_000d,
                    area * 1_000_000d,
                    new(center[0] * 1000d, center[1] * 1000d, center[2] * 1000d),
                    new(
                        Math.Abs(box[3] - box[0]) * 1000d,
                        Math.Abs(box[4] - box[1]) * 1000d,
                        Math.Abs(box[5] - box[2]) * 1000d)) { SurfaceBodyCount=surfaces.Length };
            }
            finally
            {
                ReleaseCom(massProperty);
            }
        }
        finally
        {
            foreach (var body in bodies) ReleaseCom(body);
        }
    }

    private static void VerifyGeometryQuality(GeometrySnapshot geometry, AcceptanceSpec acceptance, List<ExecutionEvidence> evidence)
    {
        var quality = acceptance.Geometry;
        if(quality.ExpectedCenterOfMassMm is { } expectedCenter)
        {
            var measured=geometry.CenterOfMassMm;
            var error=Math.Max(Math.Abs(measured.X-expectedCenter.X),Math.Max(Math.Abs(measured.Y-expectedCenter.Y),Math.Abs(measured.Z-expectedCenter.Z)));
            if(!double.IsFinite(error) || error>quality.CenterOfMassToleranceMm)
                throw new CadExecutionException("CENTER_OF_MASS_MISMATCH",ExecutionFailureCategory.GeometryQuality,true,
                    "Measured center of mass differs from the requested position.","Inspect body translations, rotations and material distribution.");
            evidence.Add(Pass("center_of_mass","Measured center of mass matches the requested position."));
        }
        if(quality.ExpectedSurfaceBodyCount is { } expectedSurfaces && geometry.SurfaceBodyCount!=expectedSurfaces)
            throw new InvalidOperationException($"Expected {expectedSurfaces} surface bodies, measured {geometry.SurfaceBodyCount}.");
        if (geometry.SolidBodyCount != quality.ExpectedSolidBodyCount)
            throw new CadExecutionException("SOLID_BODY_COUNT_MISMATCH", ExecutionFailureCategory.GeometryQuality,
                true,
                $"Expected {quality.ExpectedSolidBodyCount} solid body/bodies, measured {geometry.SolidBodyCount}.",
                "Inspect disconnected boss features, failed merges, or cuts that remove the complete body.",
                data: new Dictionary<string, string>
                {
                    ["expected"] = quality.ExpectedSolidBodyCount.ToString(CultureInfo.InvariantCulture),
                    ["measured"] = geometry.SolidBodyCount.ToString(CultureInfo.InvariantCulture)
                });
        if (quality.RequirePositiveVolume && (!double.IsFinite(geometry.VolumeMm3) || geometry.VolumeMm3 <= 0))
            throw new CadExecutionException("NON_POSITIVE_VOLUME", ExecutionFailureCategory.GeometryQuality,
                true, "The modeled part does not have a finite positive volume.",
                "Inspect profile closure, boss direction, cut depth, and solid merge settings.");
        if (quality.RequireValidTopology && (geometry.FaceCount <= 0 || geometry.EdgeCount <= 0))
            throw new CadExecutionException("INVALID_TOPOLOGY", ExecutionFailureCategory.GeometryQuality,
                true, "The modeled solid has no usable face/edge topology.",
                "Rebuild the feature chain and inspect the operation that first creates invalid geometry.");

        if (acceptance.ExpectedBoundingBoxMm is { } expectedBounds)
        {
            var measured = new[] { geometry.BoundingBoxMm.X, geometry.BoundingBoxMm.Y, geometry.BoundingBoxMm.Z };
            var expected = new[] { expectedBounds.X, expectedBounds.Y, expectedBounds.Z };
            var passed = measured.Zip(expected).All(pair =>
                Math.Abs(pair.First - pair.Second) <= acceptance.BoundingBoxToleranceMm);
            evidence.Add(new ExecutionEvidence(
                Stage: "bounding_box",
                Message: passed ? "Measured bounding box matches the IR acceptance contract." : "Measured bounding box differs from the IR acceptance contract.",
                Passed: passed,
                Data: new Dictionary<string, string>
                {
                    ["measured_mm"] = FormatBounds(geometry.BoundingBoxMm),
                    ["expected_mm"] = FormatBounds(expectedBounds),
                    ["tolerance_mm"] = acceptance.BoundingBoxToleranceMm.ToString(CultureInfo.InvariantCulture)
                },
                Code: passed ? null : "BOUNDING_BOX_MISMATCH",
                Category: passed ? null : ExecutionFailureCategory.GeometryQuality,
                Retryable: !passed,
                SuggestedAction: passed ? null : "Check sketch orientation, extrusion direction, dimensions, and feature offsets."));
            if (!passed)
                throw new CadExecutionException("BOUNDING_BOX_MISMATCH", ExecutionFailureCategory.GeometryQuality,
                    true, "Bounding-box acceptance check failed.",
                    "Check sketch orientation, extrusion direction, dimensions, and feature offsets.");
        }

        if (quality.ExpectedVolumeMm3 is { } expectedVolume)
        {
            var tolerance = expectedVolume * quality.VolumeTolerancePercent / 100d;
            var passed = Math.Abs(geometry.VolumeMm3 - expectedVolume) <= tolerance;
            evidence.Add(new ExecutionEvidence(
                Stage: "volume",
                Message: passed ? "Measured volume matches the IR acceptance contract." : "Measured volume differs from the IR acceptance contract.",
                Passed: passed,
                Data: new Dictionary<string, string>
                {
                    ["measured_mm3"] = geometry.VolumeMm3.ToString("0.###", CultureInfo.InvariantCulture),
                    ["expected_mm3"] = expectedVolume.ToString("0.###", CultureInfo.InvariantCulture),
                    ["tolerance_percent"] = quality.VolumeTolerancePercent.ToString(CultureInfo.InvariantCulture)
                },
                Code: passed ? null : "VOLUME_MISMATCH",
                Category: passed ? null : ExecutionFailureCategory.GeometryQuality,
                Retryable: !passed,
                SuggestedAction: passed ? null : "Inspect missing or extra cuts, holes, and boss features; then revise the Modeling IR."));
            if (!passed)
                throw new CadExecutionException("VOLUME_MISMATCH", ExecutionFailureCategory.GeometryQuality,
                    true, "Volume acceptance check failed.",
                    "Inspect missing or extra cuts, holes, and boss features; then revise the Modeling IR.");
        }

        evidence.Add(Pass("geometry_quality", "Body count, volume and topology checks passed."));
    }

    private static IReadOnlyList<StableFeatureReference> CaptureFeatureReferences(
        IModelDoc2 model,
        ModelingPlan plan,
        IReadOnlyDictionary<string, object> operationObjects)
    {
        var references = new List<StableFeatureReference>();
        foreach (var operation in plan.Operations)
        {
            if (!operationObjects.TryGetValue(operation.Id, out var value) || value is not IFeature feature) continue;
            string? persistentReference = null;
            try
            {
                var raw = model.Extension.GetPersistReference3(feature);
                var bytes = raw switch
                {
                    byte[] direct => direct,
                    Array array => array.Cast<object>().Select(Convert.ToByte).ToArray(),
                    _ => []
                };
                if (bytes.Length > 0) persistentReference = Convert.ToBase64String(bytes);
            }
            catch (COMException)
            {
                // The operation/name mapping remains useful if this SolidWorks build cannot persist the object.
            }

            references.Add(new(
                operation.Id,
                operation.Name,
                operation.GetType().Name,
                feature.Name,
                feature.GetID(),
                persistentReference,
                operation.DependsOn));
        }
        return references;
    }

    private static double[] ToDoubles(object raw, int minimumLength, string name)
    {
        if (raw is not Array array)
            throw new CadExecutionException("GEOMETRY_DATA_INVALID", ExecutionFailureCategory.GeometryQuality,
                false, $"SolidWorks returned invalid {name} data.", "Rebuild the model and retry.");
        var values = array.Cast<object>().Select(Convert.ToDouble).ToArray();
        if (values.Length < minimumLength)
            throw new CadExecutionException("GEOMETRY_DATA_INCOMPLETE", ExecutionFailureCategory.GeometryQuality,
                false, $"SolidWorks returned incomplete {name} data.", "Rebuild the model and retry.");
        return values;
    }

    private static string FormatBounds(BoundingBoxSpec bounds) => string.Join(" x ", new[]
    {
        bounds.X.ToString("0.###", CultureInfo.InvariantCulture),
        bounds.Y.ToString("0.###", CultureInfo.InvariantCulture),
        bounds.Z.ToString("0.###", CultureInfo.InvariantCulture)
    });

    private static void VerifyExpectedFeatures(IModelDoc2 model, AcceptanceSpec acceptance, List<ExecutionEvidence> evidence)
    {
        if (acceptance.ExpectedFeatures.Count == 0) return;
        var actual = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rawFeatures = model.FeatureManager.GetFeatures(false);
        if (rawFeatures is Array features)
        {
            foreach (var raw in features)
            {
                if (raw is not IFeature feature) continue;
                actual.Add(feature.Name);
                ReleaseCom(feature);
            }
        }
        var missing = acceptance.ExpectedFeatures.Where(expected => !actual.Contains(expected)).ToList();
        var passed = missing.Count == 0;
        evidence.Add(new("feature_tree",
            passed ? "Every feature named by the IR acceptance contract exists in the SolidWorks feature tree."
                   : "One or more expected SolidWorks features are missing.",
            passed,
            new Dictionary<string, string>
            {
                ["expected_count"] = acceptance.ExpectedFeatures.Count.ToString(CultureInfo.InvariantCulture),
                ["missing"] = missing.Count == 0 ? "<none>" : string.Join(", ", missing)
            }));
        if (!passed) throw new InvalidOperationException("Feature-tree acceptance check failed.");
    }

    private static string FindPartTemplate(ISldWorks app)
    {
        var configured = System.Environment.GetEnvironmentVariable("SOLIDWORKS_PART_TEMPLATE");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return Path.GetFullPath(configured);
        var defaultPart = app.GetUserPreferenceStringValue((int)swUserPreferenceStringValue_e.swDefaultTemplatePart);
        if (!string.IsNullOrWhiteSpace(defaultPart) && File.Exists(defaultPart)) return Path.GetFullPath(defaultPart);
        var defaultSetting = app.GetUserPreferenceStringValue((int)swUserPreferenceStringValue_e.swFileLocationsDocumentTemplates);
        foreach (var candidate in (defaultSetting ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var expanded = System.Environment.ExpandEnvironmentVariables(candidate.Trim('"'));
            if (File.Exists(expanded)) return expanded;
            if (Directory.Exists(expanded))
            {
                var template = Directory.EnumerateFiles(expanded, "*.prtdot").FirstOrDefault();
                if (template is not null) return template;
            }
        }
        var programDataRoot = Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.CommonApplicationData), "SOLIDWORKS");
        if (Directory.Exists(programDataRoot))
        {
            var standardTemplate = Directory.EnumerateFiles(programDataRoot, "*.prtdot", SearchOption.AllDirectories)
                .OrderBy(path => path.Contains($"{Path.DirectorySeparatorChar}MBD{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault();
            if (standardTemplate is not null) return standardTemplate;
        }
        throw new FileNotFoundException("No SolidWorks part template was found. Set SOLIDWORKS_PART_TEMPLATE to an absolute .prtdot path.");
    }

    private static void EnforceAllowedOutputRoot(string output)
    {
        var configuredRoot = System.Environment.GetEnvironmentVariable("CAD_ALLOWED_OUTPUT_ROOT");
        if (string.IsNullOrWhiteSpace(configuredRoot)) return;
        var root = Path.GetFullPath(configuredRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!output.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException($"Output is outside CAD_ALLOWED_OUTPUT_ROOT '{root}'.");
    }

    private static double Mm(double value) => value / 1000d;

    private readonly record struct SketchPoint(double X, double Y, double Z);

    private static CadExecutionException WrapOperationFailure(ModelingOperation operation, Exception exception)
    {
        var referenceFailure = exception.Message.Contains("select", StringComparison.OrdinalIgnoreCase) ||
                               exception.Message.Contains("reference", StringComparison.OrdinalIgnoreCase);
        return new(
            referenceFailure ? "REFERENCE_RESOLUTION_FAILED" : "FEATURE_CREATION_FAILED",
            referenceFailure ? ExecutionFailureCategory.ReferenceResolution : ExecutionFailureCategory.FeatureCreation,
            true,
            $"Operation '{operation.Id}' ({operation.Name}) failed: {exception.Message}",
            referenceFailure
                ? "Resolve the referenced sketch/plane/feature against the current model state, then retry revised IR."
                : "Inspect this operation's dimensions and dependencies, revise the Modeling IR, and retry from a clean document.",
            operation.Id,
            new Dictionary<string, string>
            {
                ["operation_type"] = operation.GetType().Name,
                ["exception"] = exception.GetType().Name,
                ["hresult"] = $"0x{exception.HResult:X8}",
                ["depends_on"] = string.Join(",",operation.DependsOn)
            },
            exception);
    }

    private static ExecutionFailure ClassifyFailure(Exception exception)
    {
        if (exception is CadExecutionException cad)
        {
            var data = cad.FailureData is null
                ? new Dictionary<string, string>()
                : new Dictionary<string, string>(cad.FailureData);
            data["exception"] = cad.InnerException?.GetType().Name ?? cad.GetType().Name;
            return new(cad.Code, cad.Category, cad.Retryable, cad.Message, cad.SuggestedAction,
                cad.OperationId, data);
        }

        return exception switch
        {
            FileNotFoundException => new("ENVIRONMENT_FILE_MISSING", ExecutionFailureCategory.Environment, false,
                exception.Message, "Verify the SolidWorks part template and configured filesystem paths.", null,
                FailureData(exception)),
            UnauthorizedAccessException => new("OUTPUT_NOT_AUTHORIZED", ExecutionFailureCategory.Output, false,
                exception.Message, "Choose an output path inside CAD_ALLOWED_OUTPUT_ROOT and confirm overwrite policy.", null,
                FailureData(exception)),
            IOException => new("OUTPUT_IO_FAILED", ExecutionFailureCategory.Output, true,
                exception.Message, "Check that the destination is writable and not locked, then retry with a versioned path.", null,
                FailureData(exception)),
            COMException => new("SOLIDWORKS_COM_FAILED", ExecutionFailureCategory.SolidWorksInterop, true,
                exception.Message, "Inspect SolidWorks state and rebuild errors, then retry from a clean document.", null,
                FailureData(exception)),
            InvalidComObjectException => new("SOLIDWORKS_OBJECT_LIFETIME_FAILED", ExecutionFailureCategory.SolidWorksInterop, true,
                exception.Message, "Retry from a clean document and inspect COM object release ordering in the executor.", null,
                FailureData(exception)),
            InvalidOperationException when exception.Message.Contains("ForceRebuild3", StringComparison.OrdinalIgnoreCase) =>
                new("REBUILD_FAILED", ExecutionFailureCategory.Rebuild, true, exception.Message,
                    "Inspect the last successful feature and revise the first failing operation in Modeling IR.", null,
                    FailureData(exception)),
            _ => new("EXECUTION_FAILED", ExecutionFailureCategory.Unknown, false, exception.Message,
                "Inspect the structured evidence and executor log before changing the Modeling IR.", null,
                FailureData(exception))
        };

        static IReadOnlyDictionary<string, string> FailureData(Exception ex) =>
            new Dictionary<string, string> { ["exception"] = ex.GetType().Name };
    }

    private static ExecutionEvidence Pass(string stage, string message, params (string Key, string Value)[] data) =>
        new(stage, message, true, data.Length == 0 ? null : data.ToDictionary(x => x.Key, x => x.Value));

    private sealed class CadExecutionException : Exception
    {
        public CadExecutionException(
            string code,
            ExecutionFailureCategory category,
            bool retryable,
            string message,
            string suggestedAction,
            string? operationId = null,
            IReadOnlyDictionary<string, string>? data = null,
            Exception? innerException = null) : base(message, innerException)
        {
            Code = code;
            Category = category;
            Retryable = retryable;
            SuggestedAction = suggestedAction;
            OperationId = operationId;
            FailureData = data;
        }

        public string Code { get; }
        public ExecutionFailureCategory Category { get; }
        public bool Retryable { get; }
        public string SuggestedAction { get; }
        public string? OperationId { get; }
        public IReadOnlyDictionary<string, string>? FailureData { get; }
    }

    private sealed record ExecutionFailure(
        string Code,
        ExecutionFailureCategory Category,
        bool Retryable,
        string Message,
        string SuggestedAction,
        string? OperationId,
        IReadOnlyDictionary<string, string> Data);

    private static void ReleaseCom(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
            Marshal.FinalReleaseComObject(value);
    }

    public void Dispose()
    {
        _serialGate.Dispose();
        _sta.Dispose();
    }
}

[SupportedOSPlatform("windows")]
internal sealed class SolidWorksStartupDialogSuppressor : IDisposable
{
    private const string ExactJournalMessage = "没能装入 Microsoft .NET Framework。";
    private const int SwHide = 0;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly ManualResetEventSlim _audioReady = new(false);
    private readonly Task<StartupDialogSuppressionResult> _watchTask;
    private volatile bool _audioSuppressionArmed;

    private SolidWorksStartupDialogSuppressor(bool watchNewProcess)
    {
        if (!watchNewProcess)
        {
            _audioReady.Set();
            _watchTask = Task.FromResult(new StartupDialogSuppressionResult(false, false));
            return;
        }
        var startedUtc = DateTime.UtcNow;
        _watchTask = Task.Run(() => Watch(startedUtc, _cancellation.Token));
        // Do not launch SolidWorks until the system-sounds session has been muted or audio setup has failed safely.
        _audioReady.Wait(TimeSpan.FromSeconds(2));
    }

    public static SolidWorksStartupDialogSuppressor Start()
    {
        var processes = Process.GetProcessesByName("SLDWORKS");
        try
        {
            return new(processes.Length == 0);
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }
    }

    public StartupDialogSuppressionResult WaitForResult(TimeSpan timeout)
    {
        try
        {
            if (_watchTask.Wait(timeout)) return _watchTask.Result;
            _cancellation.Cancel();
            return _watchTask.Wait(TimeSpan.FromSeconds(1))
                ? _watchTask.Result
                : new(false, _audioSuppressionArmed);
        }
        catch (AggregateException ex) when (ex.InnerExceptions.All(x => x is OperationCanceledException))
        {
            return new(false, _audioSuppressionArmed);
        }
    }

    private StartupDialogSuppressionResult Watch(DateTime startedUtc, CancellationToken cancellationToken)
    {
        var comInitialized = CoInitializeEx(IntPtr.Zero, 0) >= 0;
        SolidWorksStartupAudioSilencer? audioSilencer = null;
        try
        {
            try
            {
                audioSilencer = SolidWorksStartupAudioSilencer.Start(startedUtc);
                _audioSuppressionArmed = audioSilencer.IsArmed;
            }
            catch (COMException)
            {
                // Window suppression remains useful if Core Audio is unavailable.
            }
            finally
            {
                _audioReady.Set();
            }

            var deadline = startedUtc + TimeSpan.FromSeconds(12);
            while (DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
            {
                audioSilencer?.MuteMatchingSessions();
                if (AnyJournalContainsExactMessage() && TryDismissForNewSolidWorksProcess(startedUtc))
                {
                    // Keep the notification session muted until the short system-sound waveform has fully elapsed.
                    cancellationToken.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(400));
                    var audioWasArmed = _audioSuppressionArmed;
                    audioSilencer?.Dispose();
                    var audioWasRestored = audioSilencer?.RestoreSucceeded ?? false;
                    audioSilencer = null;
                    return new(true, audioWasArmed && audioWasRestored);
                }
                cancellationToken.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(25));
            }
            return new(false, _audioSuppressionArmed);
        }
        finally
        {
            _audioReady.Set();
            audioSilencer?.Dispose();
            if (comInitialized) CoUninitialize();
        }
    }

    private static bool AnyJournalContainsExactMessage()
    {
        try
        {
            var root = Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
                "SOLIDWORKS");
            if (!Directory.Exists(root)) return false;
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Directory.EnumerateFiles(root, "swxJRNL.swj", SearchOption.AllDirectories).Any(journal =>
                Encoding.GetEncoding(936).GetString(File.ReadAllBytes(journal))
                    .Contains(ExactJournalMessage, StringComparison.Ordinal));
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static bool TryDismissForNewSolidWorksProcess(DateTime startedUtc)
    {
        foreach (var process in Process.GetProcessesByName("SLDWORKS"))
        {
            using (process)
            {
                try
                {
                    if (process.StartTime.ToUniversalTime() < startedUtc - TimeSpan.FromSeconds(2)) continue;
                    if (TryDismissDialog((uint)process.Id)) return true;
                }
                catch (InvalidOperationException) { }
            }
        }
        return false;
    }

    private static bool TryDismissDialog(uint processId)
    {
        var dismissed = false;
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out var ownerProcessId);
            if (ownerProcessId != processId || !IsWindowVisible(window)) return true;
            if (!WindowText(window).Equals("SOLIDWORKS", StringComparison.OrdinalIgnoreCase)) return true;
            if (!WindowClass(window).Equals("#32770", StringComparison.Ordinal)) return true;

            if (!GetWindowRect(window, out var bounds)) return true;
            var width = bounds.Right - bounds.Left;
            var height = bounds.Bottom - bounds.Top;
            if (width is < 400 or > 520 || height is < 120 or > 220) return true;

            var buttons = new List<string>();
            var hasDirectUi = false;
            EnumChildWindows(window, (child, _) =>
            {
                var childClass = WindowClass(child);
                if (childClass.Equals("DirectUIHWND", StringComparison.Ordinal)) hasDirectUi = true;
                if (IsWindowVisible(child) && childClass.Equals("Button", StringComparison.Ordinal))
                    buttons.Add(WindowText(child));
                return true;
            }, IntPtr.Zero);
            if (!hasDirectUi || buttons.Count != 1 ||
                !(buttons[0].Equals("确定", StringComparison.Ordinal) ||
                  buttons[0].Equals("OK", StringComparison.OrdinalIgnoreCase))) return true;

            ShowWindow(window, SwHide);
            dismissed = true;
            return false;
        }, IntPtr.Zero);
        return dismissed;
    }

    private static string WindowText(IntPtr window)
    {
        var text = new StringBuilder(512);
        GetWindowText(window, text, text.Capacity);
        return text.ToString();
    }

    private static string WindowClass(IntPtr window)
    {
        var text = new StringBuilder(128);
        GetClassName(window, text, text.Capacity);
        return text.ToString();
    }

    public void Dispose()
    {
        _cancellation.Cancel();
        try { _watchTask.Wait(TimeSpan.FromSeconds(1)); }
        catch (AggregateException) { }
        _cancellation.Dispose();
        _audioReady.Dispose();
    }

    private delegate bool EnumWindowsCallback(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsCallback callback, IntPtr parameter);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, StringBuilder text, int maximumCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder className, int maximumCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out WindowBounds bounds);

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr reserved, uint coInit);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowBounds
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}

internal readonly record struct StartupDialogSuppressionResult(
    bool DialogHidden,
    bool NotificationAudioSilenced);

[SupportedOSPlatform("windows")]
internal sealed class StaWorker : IDisposable
{
    private readonly BlockingCollection<Action> _queue = [];
    private readonly Thread _thread;

    public StaWorker()
    {
        _thread = new Thread(() =>
        {
            foreach (var action in _queue.GetConsumingEnumerable()) action();
        })
        { IsBackground = true, Name = "SolidWorks-COM-STA" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public Task<T> InvokeAsync<T>(Func<T> function, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Add(() =>
        {
            if (cancellationToken.IsCancellationRequested)
            {
                completion.TrySetCanceled(cancellationToken);
                return;
            }
            try { completion.TrySetResult(function()); }
            catch (Exception ex) { completion.TrySetException(ex); }
        }, cancellationToken);
        return completion.Task;
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        _thread.Join(TimeSpan.FromSeconds(5));
        _queue.Dispose();
    }
}
