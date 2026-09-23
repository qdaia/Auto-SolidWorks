using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using CadModeling.Core;
using CadModeling.Ir;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

internal sealed partial class SolidWorksComExecutor
{
    public async Task<ModelInspection> InspectAsync(ModelInspectionRequest request,CancellationToken cancellationToken=default)
    {
        await _serialGate.WaitAsync(cancellationToken);
        try { return await _sta.InvokeAsync(()=>InspectOnSta(request),cancellationToken); }
        finally { _serialGate.Release(); }
    }
    private static ModelInspection InspectOnSta(ModelInspectionRequest request,SldWorks? borrowedApplication=null)
    {
        using var timing = CadModeling.Ir.PerformanceTrace.Begin("native.inspect");
        SldWorks? app=null; IModelDoc2? model=null; bool owned=false;
        string? importDirectory=null; string? nativeInspectionDirectory=null;
        try
        {
            if(!Path.IsPathFullyQualified(request.InputPath) || !File.Exists(request.InputPath))
                throw new ArgumentException("Inspection requires an existing absolute model path.");
            app=borrowedApplication??(SldWorks)Activator.CreateInstance(Type.GetTypeFromProgID("SldWorks.Application",true)!)!;
            var requestedExtension=Path.GetExtension(request.InputPath);
            var neutral=requestedExtension.Equals(".step",StringComparison.OrdinalIgnoreCase)||requestedExtension.Equals(".stp",StringComparison.OrdinalIgnoreCase);
            var nativeModel=requestedExtension.Equals(".sldprt",StringComparison.OrdinalIgnoreCase)||requestedExtension.Equals(".sldasm",StringComparison.OrdinalIgnoreCase);
            var needsDriveIsolation=RequiresIsolatedNativeCopy(requestedExtension,request.EditabilityProbes??[]);
            var originalSha=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(request.InputPath)));
            if(!neutral&&!needsDriveIsolation)
            {
                model=(IModelDoc2?)app.GetOpenDocumentByName(request.InputPath);
                if(model is not null)
                    throw new InvalidOperationException("Read-only inspection refuses to rebuild or probe a user-open document. Close it or use an isolated inspection request.");
            }
            if(needsDriveIsolation)
            {
                nativeInspectionDirectory=Path.Combine(NativeInspectionRoot,Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(nativeInspectionDirectory);
                var isolatedPath=Path.Combine(nativeInspectionDirectory,"inspect_"+Path.GetFileName(nativeInspectionDirectory)+requestedExtension);
                File.Copy(request.InputPath,isolatedPath,false);
                if(!Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(isolatedPath))).Equals(originalSha,StringComparison.Ordinal))
                    throw new IOException("Native source changed while creating the isolated probe copy.");
                int errors=0,warnings=0;
                model=(IModelDoc2?)PerformanceTrace.Measure("native.open", () => app.OpenDoc6(isolatedPath,requestedExtension.Equals(".sldasm",StringComparison.OrdinalIgnoreCase)?2:1,
                    (int)(swOpenDocOptions_e.swOpenDocOptions_Silent|swOpenDocOptions_e.swOpenDocOptions_ReadOnly),"",ref errors,ref warnings));
                owned=model is not null&&Path.GetFullPath(model.GetPathName()).Equals(Path.GetFullPath(isolatedPath),StringComparison.OrdinalIgnoreCase);
                if(!owned)throw new IOException($"Could not open isolated native inspection copy (errors={errors}).");
            }
            if(model is null)
            {
                int errors=0,warnings=0;
                var extension=Path.GetExtension(request.InputPath);
                if(extension.Equals(".step",StringComparison.OrdinalIgnoreCase) || extension.Equals(".stp",StringComparison.OrdinalIgnoreCase))
                {
                    // OpenDoc6 is for native files. Import a uniquely named disposable STEP copy so that
                    // an already-open SLDPRT with the same basename cannot be returned or replaced.
                    importDirectory=Path.Combine(StepInspectionRoot,Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(importDirectory);
                    var importPath=Path.Combine(importDirectory,"inspect_"+Path.GetFileName(importDirectory)+".step");
                    File.Copy(request.InputPath,importPath,false);
                    var interconnect=app.GetUserPreferenceToggle((int)swUserPreferenceToggle_e.swMultiCAD_Enable3DInterconnect);
                    var defaultPart=app.GetUserPreferenceStringValue((int)swUserPreferenceStringValue_e.swDefaultTemplatePart);
                    var useDefaultTemplates=app.GetUserPreferenceToggle((int)swUserPreferenceToggle_e.swAlwaysUseDefaultTemplates);
                    try
                    {
                        app.SetUserPreferenceToggle((int)swUserPreferenceToggle_e.swMultiCAD_Enable3DInterconnect,true);
                        // Some installations retain a ~BLANK_PART_TEMPLATE placeholder. Imports need
                        // a real template even though explicit NewDocument builds already locate one.
                        app.SetUserPreferenceStringValue((int)swUserPreferenceStringValue_e.swDefaultTemplatePart,FindPartTemplate(app));
                        app.SetUserPreferenceToggle((int)swUserPreferenceToggle_e.swAlwaysUseDefaultTemplates,true);
                        var importData=app.GetImportFileData(importPath) as IImportStepData
                            ?? throw new IOException("SolidWorks did not provide STEP import data.");
                        importData.MapConfigurationData=false;
                        model=(IModelDoc2?)app.LoadFile4(importPath,"",importData,ref errors);
                        owned=model is not null;
                    }
                    finally
                    {
                        app.SetUserPreferenceStringValue((int)swUserPreferenceStringValue_e.swDefaultTemplatePart,defaultPart);
                        app.SetUserPreferenceToggle((int)swUserPreferenceToggle_e.swAlwaysUseDefaultTemplates,useDefaultTemplates);
                        app.SetUserPreferenceToggle((int)swUserPreferenceToggle_e.swMultiCAD_Enable3DInterconnect,interconnect);
                    }
                }
                else
                {
                    model=(IModelDoc2?)PerformanceTrace.Measure("native.open", () => app.OpenDoc6(request.InputPath,request.InputPath.EndsWith(".sldasm",StringComparison.OrdinalIgnoreCase)?2:1,
                        (int)(swOpenDocOptions_e.swOpenDocOptions_Silent|swOpenDocOptions_e.swOpenDocOptions_ReadOnly),"",ref errors,ref warnings));
                    owned=model is not null&&Path.GetFullPath(model.GetPathName()).Equals(Path.GetFullPath(request.InputPath),StringComparison.OrdinalIgnoreCase);
                    if(model is not null&&!owned) throw new InvalidOperationException("Native inspection resolved another open file; no measurement was accepted.");
                }
                if(model is null) throw new IOException($"Could not open model (errors={errors}).");
            }
            var modelSha=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(request.InputPath)));
            if(modelSha!=originalSha)throw new IOException("Native source changed before inspection began.");
            var rebuildSucceeded=Convert.ToBoolean(PerformanceTrace.Measure("native.rebuild", () => model.ForceRebuild3(false)));
            if(!rebuildSucceeded) throw new InvalidOperationException("Saved-model inspection rebuild failed; incomplete native state cannot be accepted.");
            var editabilityProbes=RunEditabilityProbes(model,request.InputPath,modelSha,request.EditabilityProbes??[]);
            if(needsDriveIsolation)
            {
                // Persistent references can embed the document path. Only probe receipts belong
                // to this disposable copy; geometry and feature identities come from the source.
                PerformanceTrace.Measure("native.close", () => app.CloseDoc(model.GetTitle()));
                ReleaseCom(model);model=null;owned=false;
                var source=InspectOnSta(request with{EditabilityProbes=[]},app);
                if(!source.Success||source.ModelSha256!=modelSha||DrawingPlanValidation.FileHash(request.InputPath)!=modelSha)
                    throw new IOException("Original saved-model inspection failed or changed after the isolated probe: "+source.Message);
                return source with{EditabilityProbes=editabilityProbes};
            }
            var features=new List<ModelFeatureInfo>(); var visited=new HashSet<int>();
            var cosmeticThreads=new List<MeasuredCosmeticThread>();
            var uniqueDimensions=new Dictionary<string,(ModelDimension Dimension,string FirstFeature)>(StringComparer.Ordinal);
            void Visit(IFeature feature)
            {
                if(!visited.Add(feature.GetID())) return;
                if(features.Count>=1000) throw new InvalidOperationException("Feature inspection exceeded 1000 features; incomplete results cannot be accepted.");
                if(ReadCosmeticThread(model,feature) is { } thread)cosmeticThreads.Add(thread);
                foreach(var dim in ReadNativeFeatureDimensions(feature))
                    uniqueDimensions.TryAdd(dim.Name,(dim,feature.Name));
                features.Add(new(feature.Name,feature.GetTypeName2(),feature.IsSuppressed(),Persistent(model,feature),[])
                {DrivingEdgePersistentReferences=ReadDrivingEdgeReferences(model,feature),
                    ParentFeatureNames=(feature.GetParents() as object[]??[]).OfType<IFeature>().Select(p=>p.Name).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()});
                for(var child=feature.IGetFirstSubFeature();child is not null;child=child.IGetNextSubFeature()) Visit(child);
            }
            for(var f=model.IFirstFeature();f is not null;f=f.IGetNextFeature()) Visit(f);
            var featureNames=features.Select(f=>f.Name).ToHashSet(StringComparer.Ordinal);
            features=features.Select(f=>f with {Dimensions=uniqueDimensions.Values.Where(d=>
                (featureNames.Contains(d.Dimension.Name.Split('@').Last())?d.Dimension.Name.Split('@').Last():d.FirstFeature)==f.Name)
                .Select(d=>d.Dimension).ToArray()}).ToList();
            var entities=new List<ModelEntityInfo>();
            foreach(var query in request.Queries??[])
            foreach(var entity in ResolveEntities(model,new Dictionary<string,object>(),query))
            {
                string geometry="Other"; double? radius=null; Vector3? direction=null; string? name=null;
                if(entity is IEdge edge)
                {
                    var curve=(ICurve)edge.GetCurve();
                    if(curve.IsCircle()) {var data=(double[])curve.CircleParams;geometry="Circle";radius=data[6]*1000;direction=new(data[3],data[4],data[5]);}
                    else if(curve.IsLine()) {var data=(double[])curve.LineParams;geometry="Line";direction=new(data[3],data[4],data[5]);}
                }
                if(entity is IFace2 face)
                {
                    var surface=(ISurface)face.GetSurface();
                    if(surface.IsCylinder()) {var data=(double[])surface.CylinderParams;geometry="Cylinder";radius=data[6]*1000;direction=new(data[3],data[4],data[5]);}
                    else if(surface.IsPlane()) {var data=(double[])surface.PlaneParams;geometry="Plane";direction=new(data[0],data[1],data[2]);}
                }
                if(entity is IFeature feature) {name=feature.Name;geometry=feature.GetTypeName2();}
                if(entity is IBody2 body) name=body.Name;
                entities.Add(new(query.Kind.ToString(),geometry,name,Persistent(model,entity),radius,direction));
            }
            var bodies=model is IPartDoc part?part.GetBodies2(-1,false) as object[] ?? []:[];
            var measured=bodies.Length>0?MeasureGeometry(model):null;
            IReadOnlyList<MeasuredCylinder> cylinders=model is IPartDoc?MeasureCylinders(model):[];
            ModelVerificationResult? verification=null;
            if(request.Verification is { } spec&&ModelVerification.HasChecks(spec))
                verification=VerifySourceRequirements(model,spec,measured,out cylinders);
            var referenceInputs=(request.GeometryReferences??[]).Concat((request.Measurements??[]).SelectMany(query=>query.SecondaryGeometry is { } secondary
                ?new GeometryRef[]{query.Geometry,secondary}:new GeometryRef[]{query.Geometry}))
                .Concat((request.ConnectivityQueries??[]).SelectMany(query=>query.GeometryRefs)).ToArray();
            if(referenceInputs.GroupBy(item=>item.RefId,StringComparer.Ordinal).Any(group=>group.Select(GeometryRefResolver.Fingerprint).Distinct().Count()>1))
                throw new ArgumentException("A GeometryRef id was reused with conflicting identity fields.");
            var uniqueReferences=referenceInputs.GroupBy(item=>item.RefId,StringComparer.Ordinal).Select(group=>group.First()).ToArray();
            var refResults=uniqueReferences.Select(reference=>nativeModel
                ?ResolveGeometryReference(model,request.InputPath,modelSha,reference,request.DocumentRevision,request.SourceRevisionId)
                :new GeometryRefResolution{Reference=reference,Status=GeometryRefResolutionStatus.Unsupported,ResolvedModelSha256=modelSha,Message="Stable GeometryRef resolution requires a native saved SolidWorks document."}).ToArray();
            var byRef=refResults.ToDictionary(item=>item.Reference.RefId,StringComparer.Ordinal);
            var directional=(request.Measurements??[]).Select(query=>DirectionalMeasurementEngine.Measure(query,byRef[query.Geometry.RefId],
                query.SecondaryGeometry is null?null:byRef[query.SecondaryGeometry.RefId],modelSha,owned&&nativeModel)).ToArray();
            var connectivity=(request.ConnectivityQueries??[]).Select(query=>
                InspectConnectivity(model,query,byRef,modelSha,owned&&nativeModel)).ToArray();
            var featureTreeFingerprint=Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(features.Select(f=>new{f.Name,f.Type,f.Suppressed,f.PersistentReference,f.ParentFeatureNames,Dimensions=f.Dimensions.Select(d=>new{d.Name,d.SystemValue,d.ParameterType,d.Unit,d.Value})}),ModelingIrJson.Options))));
            if(!Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(request.InputPath))).Equals(modelSha,StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Read-only inspection changed the saved model bytes; evidence was discarded.");
            return new(verification?.Passed??true,verification is { Passed:false }?"Model read succeeded, but declared source requirement verification failed.":"Read model features, dimensions and geometry without saving changes.",request.InputPath,
                measured,features,entities) {
                ModelSha256=modelSha,ModelReopened=owned&&nativeModel,CaptureComplete=true,RebuildSucceeded=rebuildSucceeded,FeatureTreeFingerprint=featureTreeFingerprint,
                Verification=verification,Cylinders=cylinders,Cones=model is IPartDoc?MeasureCones(model):[],CosmeticThreads=cosmeticThreads,
                GeometryRefResolutions=refResults,Measurements=directional,ConnectivityChecks=connectivity,
                EditabilityProbes=editabilityProbes,
                Configurations=(model.GetConfigurationNames() as string[]??[]),
                Components=model is IAssemblyDoc assembly ? (assembly.GetComponents(true) as object[]??[]).Cast<IComponent2>()
                    .Select(c=>new AssemblyComponentResult(c.Name2,c.Name2,c.GetPathName(),(double[])c.Transform2.ArrayData,c.IsFixed())).ToArray():[]
            };
        }
        catch(Exception ex) {return new(false,ex.Message,request.InputPath);}
        finally
        {
            if(owned && model is not null && app is not null) PerformanceTrace.Measure("native.close", () => app.CloseDoc(model.GetTitle()));
            if(owned) ReleaseCom(model);
            if(borrowedApplication is null) ReleaseCom(app);
            if(importDirectory is not null) RecycleStepInspectionCopy(importDirectory);
            if(nativeInspectionDirectory is not null) RecycleNativeInspectionCopy(nativeInspectionDirectory);
        }
    }
    private static IReadOnlyList<ModelDimension> ReadNativeFeatureDimensions(IFeature feature)
    {
        var result=new List<ModelDimension>();var seen=new HashSet<string>(StringComparer.Ordinal);
        // Feature iteration must use IFeature.GetNextDisplayDimension, not the drawing-view iterator.
        for(var display=feature.GetFirstDisplayDimension() as IDisplayDimension;display is not null;
            display=feature.GetNextDisplayDimension(display) as IDisplayDimension)
        {
            if(result.Count>=5000)throw new InvalidOperationException("Native dimension inventory exceeded its bounded size.");
            if(display.GetDimension2(0) is not IDimension dim)throw new InvalidOperationException("Native display dimension has no parameter.");
            var name=string.Join('@',dim.FullName.Split('@').Take(2));
            if(!seen.Add(name))throw new InvalidOperationException("Native dimension enumeration repeated a parameter.");
            var value=dim.GetSystemValue2("");
            if(!double.IsFinite(value))throw new InvalidOperationException("Native dimension is nonfinite.");
            // IDimension.GetType is swDimensionParamType_e; IDisplayDimension.Type2 is swDimensionType_e.
            var type=(swDimensionParamType_e)dim.GetType();
            var unit=type switch {swDimensionParamType_e.swDimensionParamTypeDoubleLinear=>"Millimeter",
                swDimensionParamType_e.swDimensionParamTypeDoubleAngular=>"Degree",swDimensionParamType_e.swDimensionParamTypeInteger=>"Unitless",_=>"Unknown"};
            result.Add(new(name,value,((swDimensionType_e)display.Type2).ToString()) {ParameterType=type.ToString(),Unit=unit,
                Value=unit=="Millimeter"?value*1000:unit=="Degree"?value*180/Math.PI:unit=="Unitless"?value:null});
        }
        return result;
    }
    private static IReadOnlyList<string> ReadDrivingEdgeReferences(IModelDoc2 model,IFeature feature)
    {
        if(feature.IsSuppressed())return[];
        object? definition=null;var release=false;
        try
        {
            definition=feature.GetDefinition();
            object[] edges;
            switch(definition)
            {
                case ISimpleFilletFeatureData2 fillet:
                    if(!fillet.AccessSelections(model,null))return[];
                    release=true;edges=fillet.Edges as object[]??[];break;
                case IChamferFeatureData2 chamfer:
                    if(!chamfer.AccessSelections(model,null))return[];
                    release=true;edges=chamfer.Edges as object[]??[];break;
                default:return[];
            }
            return edges.OfType<IEdge>().Select(edge=>Persistent(model,edge)).Where(x=>x is not null).Cast<string>().Distinct(StringComparer.Ordinal).ToArray();
        }
        catch(COMException){return[];}
        finally
        {
            if(release)
            {
                try
                {
                    if(definition is ISimpleFilletFeatureData2 fillet)fillet.ReleaseSelectionAccess();
                    else if(definition is IChamferFeatureData2 chamfer)chamfer.ReleaseSelectionAccess();
                }
                catch(COMException){ }
            }
        }
    }
    private static IReadOnlyList<NativeEditabilityProbeResult> RunEditabilityProbes(IModelDoc2 model,string inputPath,string originalFileSha,
        IReadOnlyList<NativeEditabilityProbeSpec> probes)
    {
        if(probes.Count>32)throw new ArgumentException("Native editability inspection is limited to 32 declared probes.");
        if(probes.Select(p=>p.ProbeId).Distinct(StringComparer.Ordinal).Count()!=probes.Count)
            throw new ArgumentException("Native editability probe ids must be unique.");
        var results=new List<NativeEditabilityProbeResult>();
        foreach(var probe in probes)
        {
            if(string.IsNullOrWhiteSpace(probe.ProbeId)||string.IsNullOrWhiteSpace(probe.DimensionName)||!double.IsFinite(probe.TrialValue)||!Enum.IsDefined(probe.Unit))
            {results.Add(new(probe.ProbeId,probe.DimensionName,false,"Probe identity/value/unit is invalid."));continue;}
            var dimension=model.Parameter(probe.DimensionName) as IDimension;
            if(dimension is null)
            {results.Add(new(probe.ProbeId,probe.DimensionName,false,"Native driving dimension was not found."));continue;}
            var parameterType=(swDimensionParamType_e)dimension.GetType();
            var unitCompatible=parameterType switch
            {
                swDimensionParamType_e.swDimensionParamTypeDoubleLinear=>probe.Unit is DrawingValueUnit.Millimeter or DrawingValueUnit.Inch or DrawingValueUnit.Meter,
                swDimensionParamType_e.swDimensionParamTypeDoubleAngular=>probe.Unit==DrawingValueUnit.Degree,
                swDimensionParamType_e.swDimensionParamTypeInteger=>probe.Unit==DrawingValueUnit.Unitless,
                _=>false
            };
            var ownerName=probe.FeatureName??probe.DimensionName.Split('@').Skip(1).FirstOrDefault();
            var owner=string.IsNullOrWhiteSpace(ownerName)?null:FindFeatureByName(model,ownerName);
            if(!unitCompatible||owner is null||owner.IsSuppressed())
            {
                results.Add(new(probe.ProbeId,probe.DimensionName,false,"Probe unit is incompatible with the native parameter, or its owning feature is missing/suppressed.")
                {ModelSha256=originalFileSha,FeatureName=ownerName,Unit=probe.Unit,RequestedTrialValue=probe.TrialValue});
                continue;
            }
            var original=dimension.GetSystemValue2("");
            if(!double.IsFinite(original))
            {results.Add(new(probe.ProbeId,probe.DimensionName,false,"Original native system value is non-finite."));continue;}
            var trial=probe.Unit switch
            {
                DrawingValueUnit.Millimeter=>probe.TrialValue/1000d,
                DrawingValueUnit.Inch=>probe.TrialValue*0.0254,
                DrawingValueUnit.Meter=>probe.TrialValue,
                DrawingValueUnit.Degree=>probe.TrialValue*Math.PI/180d,
                DrawingValueUnit.Unitless=>probe.TrialValue,
                _=>double.NaN
            };
            if(!EditabilityTrialChanges(original,trial))
            {
                results.Add(new(probe.ProbeId,probe.DimensionName,false,"Trial value must produce a real native parameter change; a no-op drive is not editability evidence.")
                {ModelSha256=originalFileSha,FeatureName=ownerName,Unit=probe.Unit,RequestedTrialValue=probe.TrialValue,OriginalSystemValue=original,TrialSystemValue=trial});
                continue;
            }
            var trialRebuild=false;var restore=false;var restoreRebuild=false;double? trialReadback=null;double? restoreReadback=null;string? error=null;
            try
            {
                var set=dimension.SetSystemValue3(trial,(int)swSetValueInConfiguration_e.swSetValue_InThisConfiguration,null);
                if(set!=0)error=$"Trial dimension update returned status {set}.";
                else
                {
                    trialRebuild=Convert.ToBoolean(PerformanceTrace.Measure("native.rebuild", () => model.ForceRebuild3(false)));
                    trialReadback=dimension.GetSystemValue2("");
                    if(!trialRebuild||!EditabilityReadbackMatches(trial,trialReadback))
                        error="Trial rebuild/readback did not prove that the requested native parameter actually changed.";
                }
            }
            catch(Exception ex) when(ex is COMException or InvalidCastException) {error="Trial update failed: "+ex.Message;}
            finally
            {
                try
                {
                    restore=dimension.SetSystemValue3(original,(int)swSetValueInConfiguration_e.swSetValue_InThisConfiguration,null)==0;
                    if(restore)
                    {
                        restoreRebuild=Convert.ToBoolean(PerformanceTrace.Measure("native.rebuild", () => model.ForceRebuild3(false)));
                        restoreReadback=dimension.GetSystemValue2("");
                        if(!restoreRebuild||!EditabilityReadbackMatches(original,restoreReadback))
                            error=(error is null?"":" ")+"Restore rebuild/readback did not return the native parameter to its original value.";
                    }
                }
                catch(COMException ex) {error=(error is null?"":" ")+"Restore failed: "+ex.Message;}
            }
            var unchanged=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(inputPath))).Equals(originalFileSha,StringComparison.OrdinalIgnoreCase);
            var passed=error is null&&trialRebuild&&restore&&restoreRebuild&&trialReadback is not null&&restoreReadback is not null&&unchanged;
            results.Add(new(probe.ProbeId,probe.DimensionName,passed,passed?"Trial value was read back after rebuild, original value was read back after restore/rebuild, and source bytes remained unchanged.":
                error??"Trial/rebuild/restore/file-integrity probe did not complete successfully.")
            {ModelSha256=originalFileSha,FeatureName=ownerName,Unit=probe.Unit,RequestedTrialValue=probe.TrialValue,OriginalSystemValue=original,TrialSystemValue=trial,
                TrialReadbackSystemValue=trialReadback,RestoreReadbackSystemValue=restoreReadback,TrialRebuildSucceeded=trialRebuild,RestoreSucceeded=restore,
                RestoreRebuildSucceeded=restoreRebuild,ModelFileUnchanged=unchanged});
        }
        return results;
    }
    private static bool RequiresIsolatedNativeCopy(string extension,IReadOnlyList<NativeEditabilityProbeSpec> probes) =>
        (extension.Equals(".sldprt",StringComparison.OrdinalIgnoreCase)||extension.Equals(".sldasm",StringComparison.OrdinalIgnoreCase))&&probes.Count>0;
    private static bool EditabilityTrialChanges(double original,double trial) =>
        double.IsFinite(original)&&double.IsFinite(trial)&&Math.Abs(trial-original)>1e-10*Math.Max(1d,Math.Max(Math.Abs(original),Math.Abs(trial)));
    private static bool EditabilityReadbackMatches(double expected,double? actual) =>
        double.IsFinite(expected)&&actual is { } value&&double.IsFinite(value)&&Math.Abs(value-expected)<=1e-9*Math.Max(1d,Math.Abs(expected));
    // The local STEP translator rejects otherwise identical files below AppData (generic error 1).
    // Use an owned workspace under Documents; keep copies isolated and recycle them after inspection.
    private static string StepInspectionRoot => Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyDocuments),"AutoSolidWorks","Working","StepInspection");
    private static string NativeInspectionRoot => Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyDocuments),"AutoSolidWorks","Working","NativeInspection");
    private static void RecycleStepInspectionCopy(string directory)
    {
        var full=Path.GetFullPath(directory);
        var root=Path.GetFullPath(StepInspectionRoot)+Path.DirectorySeparatorChar;
        if(!full.StartsWith(root,StringComparison.OrdinalIgnoreCase) || !Directory.Exists(full)) return;
        try
        {
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(full,
                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin,
                Microsoft.VisualBasic.FileIO.UICancelOption.ThrowException);
        }
        catch(IOException) { /* Retain the isolated copy if it is still locked; never delete permanently. */ }
        catch(UnauthorizedAccessException) { /* Retain it for recoverable cleanup. */ }
    }
    private static void RecycleNativeInspectionCopy(string directory)
    {
        var full=Path.GetFullPath(directory);
        var root=Path.GetFullPath(NativeInspectionRoot)+Path.DirectorySeparatorChar;
        if(!full.StartsWith(root,StringComparison.OrdinalIgnoreCase)||!Directory.Exists(full))return;
        try
        {
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(full,
                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin,
                Microsoft.VisualBasic.FileIO.UICancelOption.ThrowException);
        }
        catch(IOException) { }
        catch(UnauthorizedAccessException) { }
    }
    private static string? Persistent(IModelDoc2 model,object entity)
    {
        try { return model.Extension.GetPersistReference3(entity) is byte[] bytes ? Convert.ToBase64String(bytes) : null; }
        catch(COMException) {return null;}
    }
}
