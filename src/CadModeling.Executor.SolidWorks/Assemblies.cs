using CadModeling.Core;
using CadModeling.Ir;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using Environment = System.Environment;

internal sealed partial class SolidWorksComExecutor
{
    public async Task<AssemblyResult> BuildAssemblyAsync(AssemblyPlan plan,CancellationToken cancellationToken=default)
    {
        await _serialGate.WaitAsync(cancellationToken);
        try { return await _sta.InvokeAsync(()=>BuildAssemblyOnSta(plan),cancellationToken); }
        finally { _serialGate.Release(); }
    }
    private static AssemblyResult BuildAssemblyOnSta(AssemblyPlan plan)
    {
        SldWorks? app=null; IModelDoc2? model=null; var opened=new List<IModelDoc2>();
        try
        {
            AssemblyPlanValidation.Validate(plan);
            using var startup=SolidWorksStartupDialogSuppressor.Start();
            app=(SldWorks)Activator.CreateInstance(Type.GetTypeFromProgID("SldWorks.Application",true)!)!;
            app.Visible=true;
            var template=app.GetDocumentTemplate((int)swDocumentTypes_e.swDocASSEMBLY,"",0,0,0);
            if(string.IsNullOrWhiteSpace(template)||!File.Exists(template))
            {
                var configured=Environment.GetEnvironmentVariable("SOLIDWORKS_ASSEMBLY_TEMPLATE");
                var templatesRoot=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"SOLIDWORKS");
                template=configured is not null && File.Exists(configured)?configured:
                    Directory.Exists(templatesRoot)?Directory.EnumerateFiles(templatesRoot,"*.asmdot",SearchOption.AllDirectories)
                        .OrderBy(p=>p.Contains("MBD",StringComparison.OrdinalIgnoreCase)).FirstOrDefault():null;
            }
            if(string.IsNullOrWhiteSpace(template)||!File.Exists(template)) throw new InvalidOperationException("No native assembly template is configured in SolidWorks.");
            model=(IModelDoc2?)app.NewDocument(template,0,0,0)??throw new InvalidOperationException("Cannot create assembly document.");
            var assembly=(IAssemblyDoc)model; var math=(IMathUtility)app.GetMathUtility();
            var components=new Dictionary<string,IComponent2>();
            foreach(var source in plan.Components)
            {
                var doc=(IModelDoc2?)app.GetOpenDocumentByName(source.Path);
                if(doc is null)
                {
                    int errors=0,warnings=0;
                    doc=(IModelDoc2?)app.OpenDoc6(source.Path,source.Path.EndsWith(".sldasm",StringComparison.OrdinalIgnoreCase)?2:1,
                        (int)swOpenDocOptions_e.swOpenDocOptions_Silent,source.Configuration,ref errors,ref warnings);
                    if(doc is null) throw new IOException($"Cannot load component '{source.Id}' (errors={errors}).");
                    opened.Add(doc);
                }
                var activationError=0;
                var active=(IModelDoc2?)app.ActivateDoc3(model.GetTitle(),false,(int)swRebuildOnActivation_e.swDontRebuildActiveDoc,ref activationError);
                if(active is null || activationError!=0) throw new InvalidOperationException("Cannot activate the assembly after loading its component.");
                var component=assembly.AddComponent5(source.Path,0,"",!string.IsNullOrEmpty(source.Configuration),source.Configuration,0,0,0)
                    ??throw new InvalidOperationException($"Cannot insert component '{source.Id}'.");
                component.Name2=source.Id;
                model.ClearSelection2(true); if(!component.Select4(false,null,false)) throw new InvalidOperationException("Cannot select inserted component.");
                assembly.UnfixComponent();
                component.Transform2=(MathTransform)math.CreateTransform(ComponentTransform(source));
                if(source.Fixed)
                {
                    model.ClearSelection2(true);component.Select4(false,null,false);assembly.FixComponent();
                    if(!component.IsFixed()) throw new InvalidOperationException($"Could not fix component '{source.Id}'.");
                }
                components.Add(source.Id,component);
            }
            void SelectMateEntity(AssemblyMateEntity endpoint,bool append)
            {
                var component=components[endpoint.ComponentId]; var data=model.ISelectionManager.CreateSelectData(); data.Mark=1;
                if(endpoint.Entity is null)
                { if(!component.Select4(append,data,false)) throw new InvalidOperationException("Cannot select mate component."); return; }
                var part=(IModelDoc2?)component.GetModelDoc2()??throw new InvalidOperationException("Mate component is not resolved.");
                var entities=ResolveEntities(part,new Dictionary<string,object>(),endpoint.Entity);
                if(entities.Count!=1) throw new ArgumentException("Each mate endpoint must resolve exactly one entity.");
                var corresponding=component.GetCorrespondingEntity(entities[0]);
                var selected=corresponding switch { IEntity e=>e.Select4(append,data),IFeature f=>f.Select2(append,1),_=>false };
                if(!selected) throw new InvalidOperationException("Cannot map the mate entity into assembly coordinates.");
            }
            var mateNames=new List<string>();
            foreach(var m in plan.Mates)
            {
                var previousMateIds=MateFeatures(model).Select(f=>f.GetID()).ToHashSet();
                model.ClearSelection2(true); SelectMateEntity(m.First,false);SelectMateEntity(m.Second,true);
                var kind=m.Kind switch {
                    AssemblyMateKind.Coincident=>swMateType_e.swMateCOINCIDENT,AssemblyMateKind.Concentric=>swMateType_e.swMateCONCENTRIC,
                    AssemblyMateKind.Parallel=>swMateType_e.swMatePARALLEL,AssemblyMateKind.Perpendicular=>swMateType_e.swMatePERPENDICULAR,
                    AssemblyMateKind.Distance=>swMateType_e.swMateDISTANCE,AssemblyMateKind.Angle=>swMateType_e.swMateANGLE,_=>swMateType_e.swMateLOCK };
                var mate=assembly.AddMate5((int)kind,(int)(m.AntiAligned?swMateAlign_e.swMateAlignANTI_ALIGNED:swMateAlign_e.swMateAlignALIGNED),false,
                    Mm(m.Value),Mm(m.Value),Mm(m.Value),1,1,Radians(m.Value),Radians(m.Value),Radians(m.Value),false,m.LockRotation,0,out var status);
                if(mate is null||status!=(int)swAddMateError_e.swAddMateError_NoError) throw new InvalidOperationException($"Mate '{m.Name}' failed (status={status}).");
                var feature=mate as IFeature ?? MateFeatures(model).SingleOrDefault(f=>!previousMateIds.Contains(f.GetID()))
                    ?? throw new InvalidOperationException("Native mate feature was not found after mate creation.");
                feature.Name=m.Name;
                mateNames.Add(feature.Name);
            }
            model.ClearSelection2(true);
            if(!model.ForceRebuild3(false)) throw new InvalidOperationException("Assembly rebuild failed.");
            var interferences=new List<AssemblyInterference>();
            if(plan.CheckInterference)
            {
                var manager=assembly.InterferenceDetectionManager;
                try
                {
                    manager.TreatCoincidenceAsInterference=false;manager.IncludeMultibodyPartInterferences=false;
                    manager.IgnoreHiddenBodies=false; manager.UseTransform=true;
                    foreach(var i in (manager.GetInterferences() as object[]??[]).Cast<IInterference>())
                        interferences.Add(new(i.Volume*1e9,(i.Components as object[]??[]).Cast<IComponent2>().Select(c=>c.Name2).ToArray()));
                }
                finally {manager.Done();}
            }
            var componentResults=plan.Components.Select(c=>new AssemblyComponentResult(c.Id,components[c.Id].Name2,c.Path,
                (double[])components[c.Id].Transform2.ArrayData,components[c.Id].IsFixed())).ToArray();
            void Save(string path)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!); int errors=0,warnings=0;
                if(!model.Extension.SaveAs(path,0,1,null,ref errors,ref warnings)||errors!=0||!File.Exists(path)||new FileInfo(path).Length==0)
                    throw new IOException($"Assembly save failed: {path} (errors={errors}).");
            }
            Save(plan.NativePath); foreach(var path in plan.ExportPaths) Save(path);
            app.CloseDoc(model.GetTitle());ReleaseCom(model);model=null;
            int openErrors=0,openWarnings=0;
            model=(IModelDoc2?)app.OpenDoc6(plan.NativePath,2,(int)(swOpenDocOptions_e.swOpenDocOptions_Silent|swOpenDocOptions_e.swOpenDocOptions_ReadOnly),"",ref openErrors,ref openWarnings);
            if(model is null||((IAssemblyDoc)model).GetComponentCount(true)!=plan.Components.Count) throw new IOException("Saved assembly could not reopen with the expected component count.");
            var reopenedComponents=(((IAssemblyDoc)model).GetComponents(true) as object[]??[]).Cast<IComponent2>().ToDictionary(c=>c.Name2);
            foreach(var expected in componentResults)
            {
                if(!reopenedComponents.TryGetValue(expected.InstanceName,out var component) || component.IsFixed()!=expected.Fixed)
                    throw new IOException("Saved assembly component identity or fixed state changed on reopening.");
                var transform=(double[])component.Transform2.ArrayData;
                if(transform.Length!=expected.Transform.Count || transform.Where((value,index)=>Math.Abs(value-expected.Transform[index])>1e-8).Any())
                    throw new IOException($"Component '{expected.Id}' transform changed on reopening.");
            }
            var reopenedMates=MateFeatures(model).Select(f=>f.Name).ToHashSet(StringComparer.Ordinal);
            if(mateNames.Any(name=>!reopenedMates.Contains(name))) throw new IOException("Saved assembly is missing a created mate.");
            return new(true,"Created, mated, rebuilt, checked interference and reopened the saved native assembly.",plan.NativePath,componentResults,mateNames,interferences,true);
        }
        catch(Exception ex) {return new(false,ex.Message,plan.NativePath);}
        finally
        {
            if(app is not null)
            {
                if(model is not null) try {app.CloseDoc(model.GetTitle());} catch(System.Runtime.InteropServices.COMException) { }
                foreach(var doc in opened) try {app.CloseDoc(doc.GetTitle());} catch(System.Runtime.InteropServices.COMException) { }
            }
            ReleaseCom(model);ReleaseCom(app);
        }
    }
    private static IReadOnlyList<IFeature> MateFeatures(IModelDoc2 model)
    {
        var result=new List<IFeature>(); var visited=new HashSet<int>();
        void Visit(IFeature feature)
        {
            if(!visited.Add(feature.GetID())) return;
            if(feature.GetSpecificFeature2() is IMate2) result.Add(feature);
            for(var child=feature.IGetFirstSubFeature();child is not null;child=child.IGetNextSubFeature()) Visit(child);
        }
        for(var f=model.IFirstFeature();f is not null;f=f.IGetNextFeature()) Visit(f);
        return result;
    }
    // XYZ Euler rotations applied X, then Y, then Z. SOLIDWORKS uses row vectors and metres in its 16-value transform.
    private static double[] ComponentTransform(AssemblyComponentSpec c)
    {
        var x=Radians(c.RotationDegrees.X);var y=Radians(c.RotationDegrees.Y);var z=Radians(c.RotationDegrees.Z);
        var cx=Math.Cos(x);var sx=Math.Sin(x);var cy=Math.Cos(y);var sy=Math.Sin(y);var cz=Math.Cos(z);var sz=Math.Sin(z);
        return [cz*cy,sz*cy,-sy, cz*sy*sx-sz*cx,sz*sy*sx+cz*cx,cy*sx,
            cz*sy*cx+sz*sx,sz*sy*cx-cz*sx,cy*cx,Mm(c.TranslationMm.X),Mm(c.TranslationMm.Y),Mm(c.TranslationMm.Z),1,0,0,0];
    }
}
