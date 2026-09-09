using System.Text.Json;
using CadModeling.Core;
using CadModeling.Ir;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

internal sealed partial class SolidWorksComExecutor
{
    private static (string Path,ModelingCheckpointManifest Manifest) SaveCheckpoint(SldWorks app,IModelDoc2 model,ModelingPlan plan,
        int completed,IReadOnlyDictionary<string,object> objects)
    {
        if(model.SketchManager.ActiveSketch is not null || !model.ForceRebuild3(false))
            throw new InvalidOperationException("Cannot checkpoint an active sketch or a failed rebuild.");
        var geometry=MeasureGeometry(model);
        if(geometry.SolidBodyCount<1||!double.IsFinite(geometry.VolumeMm3)||geometry.VolumeMm3<=0||geometry.FaceCount<1)
            throw new InvalidOperationException("Automatic checkpoints require a valid positive-volume solid.");
        var references=CaptureFeatureReferences(model,plan with { Operations=plan.Operations.Take(completed).ToArray() },objects);
        if(references.Count!=completed) throw new InvalidOperationException("Checkpoint feature map is incomplete.");
        var directory=plan.Recovery.Directory??Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyDocuments),"AutoSolidWorks","Working","Checkpoints");
        directory=Path.Combine(Path.GetFullPath(directory),Guid.NewGuid().ToString("N"));
        var path=Path.Combine(directory,$"checkpoint_{completed}_{Path.GetFileName(directory)}.SLDPRT");
        EnforceAllowedOutputRoot(path);
        Directory.CreateDirectory(directory);
        var errors=0;var warnings=0;
        if(!model.Extension.SaveAs(path,0,(int)(swSaveAsOptions_e.swSaveAsOptions_Silent|swSaveAsOptions_e.swSaveAsOptions_Copy),null,ref errors,ref warnings)||errors!=0||!File.Exists(path))
            throw new IOException($"Checkpoint copy save failed (errors={errors}, warnings={warnings}).");
        var inspection=InspectOnSta(new(path),app);
        if(!inspection.Success||inspection.Geometry is not { } saved||saved.SolidBodyCount!=geometry.SolidBodyCount||
           Math.Abs(saved.VolumeMm3-geometry.VolumeMm3)>Math.Max(1e-5,geometry.VolumeMm3*1e-8))
            throw new InvalidOperationException("Saved checkpoint could not be reopened with matching geometry.");
        var manifest=new ModelingCheckpointManifest {
            NativePath=path,NativeSha256=DrawingPlanValidation.FileHash(path),OriginalPlan=plan,
            SourceModelSha256=plan.SourceModelPath is { } source?DrawingPlanValidation.FileHash(source):null,
            CompletedOperationCount=completed,FeatureReferences=references,Geometry=saved
        };
        var manifestPath=Path.Combine(directory,"checkpoint.json");
        File.WriteAllText(manifestPath,JsonSerializer.Serialize(manifest,new JsonSerializerOptions(ModelingIrJson.Options){WriteIndented=true}));
        return (manifestPath,manifest);
    }

    private static void RestoreCheckpointFeatures(IModelDoc2 model,ModelingCheckpointManifest manifest,Dictionary<string,object> objects)
    {
        foreach(var reference in manifest.FeatureReferences)
        {
            IFeature? feature=null;
            if(reference.PersistentReferenceBase64 is { } persistent)
            {
                int error=0;
                feature=model.Extension.GetObjectByPersistReference3(Convert.FromBase64String(persistent),out error) as IFeature;
                if(error!=0) feature=null;
            }
            feature??=FindFeatureByName(model,reference.SolidWorksName);
            if(feature is null || !feature.Name.Equals(reference.SolidWorksName,StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Checkpoint feature '{reference.OperationId}' cannot be resolved uniquely.");
            objects.Add(reference.OperationId,feature);
        }
        var measured=MeasureGeometry(model);
        if(measured.SolidBodyCount!=manifest.Geometry.SolidBodyCount||Math.Abs(measured.VolumeMm3-manifest.Geometry.VolumeMm3)>Math.Max(1e-5,manifest.Geometry.VolumeMm3*1e-8))
            throw new InvalidOperationException("Opened checkpoint geometry differs from its saved inspection.");
    }

    private static ModelingRecoveryState RecoveryState(ModelingPlan plan,string? path,ModelingCheckpointManifest? manifest,string? failedId) => new(
        path,manifest?.NativePath,plan.Operations.Take(manifest?.CompletedOperationCount??0).Select(o=>o.Id).ToArray(),
        plan.Operations.Skip(manifest?.CompletedOperationCount??0).Select(o=>o.Id).ToArray(),failedId,
        manifest is not null&&manifest.CompletedOperationCount<plan.Operations.Count,
        manifest is null?"No reusable checkpoint. Correct the failing operation and rebuild.":
            "Keep the full corrected operation list and unchanged source requirements; set recovery.resume_manifest_path. The verified prefix will be reused, and the remaining operations replayed in order.");
}
