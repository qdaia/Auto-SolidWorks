using System.Runtime.InteropServices;
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
        SldWorks? app=null; IModelDoc2? model=null; bool owned=false;
        string? importDirectory=null;
        try
        {
            if(!Path.IsPathFullyQualified(request.InputPath) || !File.Exists(request.InputPath))
                throw new ArgumentException("Inspection requires an existing absolute model path.");
            app=borrowedApplication??(SldWorks)Activator.CreateInstance(Type.GetTypeFromProgID("SldWorks.Application",true)!)!;
            var requestedExtension=Path.GetExtension(request.InputPath);
            var neutral=requestedExtension.Equals(".step",StringComparison.OrdinalIgnoreCase)||requestedExtension.Equals(".stp",StringComparison.OrdinalIgnoreCase);
            model=neutral?null:(IModelDoc2?)app.GetOpenDocumentByName(request.InputPath);
            if(model is not null&&!Path.GetFullPath(model.GetPathName()).Equals(Path.GetFullPath(request.InputPath),StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("SolidWorks returned a different open document. Use a unique model filename.");
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
                    model=(IModelDoc2?)app.OpenDoc6(request.InputPath,request.InputPath.EndsWith(".sldasm",StringComparison.OrdinalIgnoreCase)?2:1,
                        (int)(swOpenDocOptions_e.swOpenDocOptions_Silent|swOpenDocOptions_e.swOpenDocOptions_ReadOnly),"",ref errors,ref warnings);
                    owned=model is not null&&Path.GetFullPath(model.GetPathName()).Equals(Path.GetFullPath(request.InputPath),StringComparison.OrdinalIgnoreCase);
                    if(model is not null&&!owned) throw new InvalidOperationException("Native inspection resolved another open file; no measurement was accepted.");
                }
                if(model is null) throw new IOException($"Could not open model (errors={errors}).");
            }
            var features=new List<ModelFeatureInfo>(); var visited=new HashSet<int>();
            var uniqueDimensions=new Dictionary<string,(ModelDimension Dimension,string FirstFeature)>(StringComparer.Ordinal);
            void Visit(IFeature feature)
            {
                if(!visited.Add(feature.GetID())) return;
                if(features.Count>=1000) throw new InvalidOperationException("Feature inspection exceeded 1000 features; incomplete results cannot be accepted.");
                foreach(var dim in ReadNativeFeatureDimensions(feature))
                    uniqueDimensions.TryAdd(dim.Name,(dim,feature.Name));
                features.Add(new(feature.Name,feature.GetTypeName2(),feature.IsSuppressed(),Persistent(model,feature),[]));
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
            IReadOnlyList<MeasuredCylinder> cylinders=[];
            var verification=request.Verification is { } spec&&ModelVerification.HasChecks(spec)?VerifySourceRequirements(model,spec,measured,out cylinders):null;
            return new(verification?.Passed??true,verification is { Passed:false }?"Model read succeeded, but declared source requirement verification failed.":"Read model features, dimensions and geometry without saving changes.",request.InputPath,
                measured,features,entities) {
                Verification=verification,Cylinders=cylinders,
                Configurations=(model.GetConfigurationNames() as string[]??[]),
                Components=model is IAssemblyDoc assembly ? (assembly.GetComponents(true) as object[]??[]).Cast<IComponent2>()
                    .Select(c=>new AssemblyComponentResult(c.Name2,c.Name2,c.GetPathName(),(double[])c.Transform2.ArrayData,c.IsFixed())).ToArray():[]
            };
        }
        catch(Exception ex) {return new(false,ex.Message,request.InputPath);}
        finally
        {
            if(owned && model is not null && app is not null) app.CloseDoc(model.GetTitle());
            if(owned) ReleaseCom(model);
            if(borrowedApplication is null) ReleaseCom(app);
            if(importDirectory is not null) RecycleStepInspectionCopy(importDirectory);
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
    // The local STEP translator rejects otherwise identical files below AppData (generic error 1).
    // Use an owned workspace under Documents; keep copies isolated and recycle them after inspection.
    private static string StepInspectionRoot => Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyDocuments),"AutoSolidWorks","Working","StepInspection");
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
    private static string? Persistent(IModelDoc2 model,object entity)
    {
        try { return model.Extension.GetPersistReference3(entity) is byte[] bytes ? Convert.ToBase64String(bytes) : null; }
        catch(COMException) {return null;}
    }
}
