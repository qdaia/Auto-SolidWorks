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
        try { return await _sta.InvokeAsync(()=>
        {
            var result=BuildAssemblyOnSta(plan,cancellationToken);
            if(!result.Success) return result;
            // BuildAssemblyOnSta has closed its documents before drawing export begins.
            // Keep both stages on the same STA and under the same serial gate.
            if(cancellationToken.IsCancellationRequested)return result with{Success=false,Message="执行在装配读回后停止；绘图阶段未提交。"};
            var drawing=ExportDrawingOnSta(DrawingExportValidation.ForAssembly(plan.NativePath),cancellationToken);
            return result with { Success=drawing.Success,Drawing=drawing,Message=drawing.Success
                ?"创建并重新打开该装配及其匹配的第一角SLDDRW/PDF图纸。"
                :"装配保存并重新打开，但图纸/PDF导出失败："+drawing.Message };
        },cancellationToken); }
        finally { _serialGate.Release(); }
    }
    private static AssemblyResult BuildAssemblyOnSta(AssemblyPlan plan,CancellationToken cancellationToken=default)
    {
        SldWorks? app=null; IModelDoc2? model=null; var opened=new List<IModelDoc2>();bool? originalUpdateComponentNames=null;var retainInstanceNames=false;
        try
        {
            AssemblyPlanValidation.Validate(plan);
            cancellationToken.ThrowIfCancellationRequested();
            using var startup=SolidWorksStartupDialogSuppressor.Start();
            app=(SldWorks)Activator.CreateInstance(Type.GetTypeFromProgID("SldWorks.Application",true)!)!;
            app.Visible=true;
            originalUpdateComponentNames=app.GetUserPreferenceToggle((int)swUserPreferenceToggle_e.swExtRefUpdateCompNames);
            app.SetUserPreferenceToggle((int)swUserPreferenceToggle_e.swExtRefUpdateCompNames,false);
            var template=app.GetDocumentTemplate((int)swDocumentTypes_e.swDocASSEMBLY,"",0,0,0);
            if(string.IsNullOrWhiteSpace(template)||!File.Exists(template))
            {
                var configured=Environment.GetEnvironmentVariable("SOLIDWORKS_ASSEMBLY_TEMPLATE");
                var templatesRoot=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"SOLIDWORKS");
                template=configured is not null && File.Exists(configured)?configured:
                    Directory.Exists(templatesRoot)?Directory.EnumerateFiles(templatesRoot,"*.asmdot",SearchOption.AllDirectories)
                        .OrderBy(p=>p.Contains("MBD",StringComparison.OrdinalIgnoreCase)).FirstOrDefault():null;
            }
            if(string.IsNullOrWhiteSpace(template)||!File.Exists(template)) throw new InvalidOperationException("没有配置 SolidWorks 基准的原生装配模板。");
            model=(IModelDoc2?)app.NewDocument(template,0,0,0)??throw new InvalidOperationException("无法创建装配文档。");
            var assembly=(IAssemblyDoc)model; var math=(IMathUtility)app.GetMathUtility();
            var components=new Dictionary<string,IComponent2>();
            foreach(var source in plan.Components)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var doc=(IModelDoc2?)app.GetOpenDocumentByName(source.Path);
                if(doc is null)
                {
                    int errors=0,warnings=0;
                    doc=(IModelDoc2?)app.OpenDoc6(source.Path,source.Path.EndsWith(".sldasm",StringComparison.OrdinalIgnoreCase)?2:1,
                        (int)swOpenDocOptions_e.swOpenDocOptions_Silent,source.Configuration,ref errors,ref warnings);
                    if(doc is null) throw new IOException($"无法加载组件 '{source.Id}' (错误={errors}).");
                    opened.Add(doc);
                }
                var activationError=0;
                var active=(IModelDoc2?)app.ActivateDoc3(model.GetTitle(),false,(int)swRebuildOnActivation_e.swDontRebuildActiveDoc,ref activationError);
                if(active is null || activationError!=0) throw new InvalidOperationException("无法在加载其组件后激活装配体。");
                var component=assembly.AddComponent5(source.Path,0,"",!string.IsNullOrEmpty(source.Configuration),source.Configuration,0,0,0)
                    ??throw new InvalidOperationException($"无法插入组件 '{source.Id}'。");
                RenameAssemblyComponent(app,model,component,string.IsNullOrWhiteSpace(source.Name)?"零部件_"+(components.Count+1):source.Name);
                model.ClearSelection2(true); if(!component.Select4(false,null,false)) throw new InvalidOperationException("无法选择插入的组件。");
                assembly.UnfixComponent();
                component.Transform2=(MathTransform)math.CreateTransform(ComponentTransform(source));
                if(source.Fixed)
                {
                    model.ClearSelection2(true);component.Select4(false,null,false);assembly.FixComponent();
                    if(!component.IsFixed()) throw new InvalidOperationException($"无法修复组件 '{source.Id}'。");
                }
                components.Add(source.Id,component);
            }
            void SelectMateEntity(AssemblyMateEntity endpoint,bool append)
            {
                var component=components[endpoint.ComponentId]; var data=model.ISelectionManager.CreateSelectData(); data.Mark=1;
                if(endpoint.Entity is null)
                { if(!component.Select4(append,data,false)) throw new InvalidOperationException("无法选择对齐部件。"); return; }
                var part=(IModelDoc2?)component.GetModelDoc2()??throw new InvalidOperationException("配合零部件未解析。");
                var entities=ResolveEntities(part,new Dictionary<string,object>(),endpoint.Entity);
                if(entities.Count!=1) throw new ArgumentException("每个对齐约束的端点必须对应一个实体。");
                var corresponding=component.GetCorrespondingEntity(entities[0]);
                var selected=corresponding switch { IEntity e=>e.Select4(append,data),IFeature f=>f.Select2(append,1),_=>false };
                if(!selected) throw new InvalidOperationException("无法将对齐实体映射到装配坐标中。");
            }
            var mateNames=new List<string>();
            foreach(var m in plan.Mates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var previousMateIds=MateFeatures(model).Select(f=>f.GetID()).ToHashSet();
                model.ClearSelection2(true); SelectMateEntity(m.First,false);SelectMateEntity(m.Second,true);
                var kind=m.Kind switch {
                    AssemblyMateKind.Coincident=>swMateType_e.swMateCOINCIDENT,AssemblyMateKind.Concentric=>swMateType_e.swMateCONCENTRIC,
                    AssemblyMateKind.Parallel=>swMateType_e.swMatePARALLEL,AssemblyMateKind.Perpendicular=>swMateType_e.swMatePERPENDICULAR,
                    AssemblyMateKind.Distance=>swMateType_e.swMateDISTANCE,AssemblyMateKind.Angle=>swMateType_e.swMateANGLE,_=>swMateType_e.swMateLOCK };
                var mate=assembly.AddMate5((int)kind,(int)(m.AntiAligned?swMateAlign_e.swMateAlignANTI_ALIGNED:swMateAlign_e.swMateAlignALIGNED),false,
                    Mm(m.Value),Mm(m.Value),Mm(m.Value),1,1,Radians(m.Value),Radians(m.Value),Radians(m.Value),false,m.LockRotation,0,out var status);
                if(mate is null||status!=(int)swAddMateError_e.swAddMateError_NoError) throw new InvalidOperationException($"配合 {m.Name} 创建失败（status={status}）。");
                var feature=mate as IFeature ?? MateFeatures(model).SingleOrDefault(f=>!previousMateIds.Contains(f.GetID()))
                    ?? throw new InvalidOperationException("原生对齐特征在对齐创建后未找到。");
                feature.Name=m.Name;
                mateNames.Add(feature.Name);
            }
            model.ClearSelection2(true);
            if(!model.ForceRebuild3(false)) throw new InvalidOperationException("重建装配失败。");
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
            var idsByInstance=componentResults.ToDictionary(c=>c.InstanceName,c=>c.Id,StringComparer.Ordinal);
            if(plan.CheckInterference && plan.RejectUnapprovedInterference && interferences.Any(i=>
                !AssemblyPlanValidation.InterferenceAllowed(plan,i.Components.Select(name=>idsByInstance.GetValueOrDefault(name,name)).ToArray(),i.VolumeMm3)))
                return new(false,"ASSEMBLY_INTERFERENCE: 存在未明确允许的组件干涉；未保存为验收成功的装配体。",null,componentResults,mateNames,interferences);
            if(plan.RequireFullyConstrainedComponents && components.Values.Any(c=>!c.IsFixed() && c.GetConstrainedStatus()!=(int)swConstrainedStatus_e.swFullyConstrained))
                return new(false,"ASSEMBLY_UNDERCONSTRAINED: 存在未完全约束的组件。",null,componentResults,mateNames,interferences);
            var mobilityCreated=AssemblyMobilityContract.Verify(plan,new NativeAssemblyMobilitySession(model,components));
            void Save(string path)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Directory.CreateDirectory(Path.GetDirectoryName(path)!); int errors=0,warnings=0;
                if(!model.Extension.SaveAs(path,0,1,null,ref errors,ref warnings)||errors!=0||!File.Exists(path)||new FileInfo(path).Length==0)
                    throw new IOException($"装配保存失败：{path}(errors={errors}).");
            }
            Save(plan.NativePath); foreach(var path in plan.ExportPaths) Save(path);
            app.CloseDoc(model.GetTitle());ReleaseCom(model);model=null;
            int openErrors=0,openWarnings=0;
            model=(IModelDoc2?)app.OpenDoc6(plan.NativePath,2,(int)(swOpenDocOptions_e.swOpenDocOptions_Silent|swOpenDocOptions_e.swOpenDocOptions_ReadOnly),"",ref openErrors,ref openWarnings);
            if(model is null||((IAssemblyDoc)model).GetComponentCount(true)!=plan.Components.Count) throw new IOException("保存的装配体无法以预期的组件数量重新打开。");
            var reopenedComponents=(((IAssemblyDoc)model).GetComponents(true) as object[]??[]).Cast<IComponent2>().ToDictionary(c=>c.Name2);
            foreach(var expected in componentResults)
            {
                if(!reopenedComponents.TryGetValue(expected.InstanceName,out var component) || component.IsFixed()!=expected.Fixed)
                    throw new IOException("保存的装配组件身份或固定状态在重新打开时发生变化。");
                var transform=(double[])component.Transform2.ArrayData;
                var source=plan.Components.Single(c=>c.Id==expected.Id);
                if(!Path.GetFullPath(component.GetPathName()).Equals(Path.GetFullPath(source.Path),StringComparison.OrdinalIgnoreCase)
                    || !string.IsNullOrEmpty(source.Configuration) && component.ReferencedConfiguration!=source.Configuration)
                    throw new IOException("装配重开后组件路径或请求配置改变。");
                if(plan.RequireFullyConstrainedComponents && !component.IsFixed() && component.GetConstrainedStatus()!=(int)swConstrainedStatus_e.swFullyConstrained)
                    throw new IOException("装配重开后组件未完全约束。");
                if(transform.Length!=expected.Transform.Count || transform.Where((value,index)=>Math.Abs(value-expected.Transform[index])>1e-8).Any())
                    throw new IOException($"组件 '{expected.Id}' 重新打开时发生变换。");
            }
            var savedComponentsById=componentResults.ToDictionary(c=>c.Id,c=>reopenedComponents[c.InstanceName],StringComparer.Ordinal);
            var mobilitySaved=AssemblyMobilityContract.VerifySaved(plan,new NativeAssemblyMobilitySession(model,savedComponentsById),mobilityCreated,
                (a,b)=>SamePersistentIdentity(model,a,b));
            var reopenedMates=MateFeatures(model).Select(f=>f.Name).ToHashSet(StringComparer.Ordinal);
            if(mateNames.Any(name=>!reopenedMates.Contains(name))) throw new IOException("保存的装配缺少一个创建的对齐特征。");
            var mateReadbacks=new List<AssemblyMateReadback>();
            foreach(var spec in plan.Mates)
            {
                var feature=MateFeatures(model).Single(f=>f.Name==spec.Name);
                var mate=(IMate2)feature.GetSpecificFeature2();
                var expectedType=spec.Kind switch
                {
                    AssemblyMateKind.Coincident=>swMateType_e.swMateCOINCIDENT,AssemblyMateKind.Concentric=>swMateType_e.swMateCONCENTRIC,
                    AssemblyMateKind.Parallel=>swMateType_e.swMatePARALLEL,AssemblyMateKind.Perpendicular=>swMateType_e.swMatePERPENDICULAR,
                    AssemblyMateKind.Distance=>swMateType_e.swMateDISTANCE,AssemblyMateKind.Angle=>swMateType_e.swMateANGLE,_=>swMateType_e.swMateLOCK
                };
                if(feature.IsSuppressed() || mate.Type!=(int)expectedType)
                    throw new IOException("重开后的配合类型或抑制状态不符合请求。");
                mateReadbacks.Add(VerifySavedMate(model,feature,mate,spec,componentResults,reopenedComponents));
                if(spec.Kind is AssemblyMateKind.Distance or AssemblyMateKind.Angle)
                {
                    var value=mate.DisplayDimension?.GetDimension2(0)?.GetSystemValue2("");
                    var expectedValue=spec.Kind==AssemblyMateKind.Angle?Radians(spec.Value):Mm(spec.Value);
                    if(value is null || !double.IsFinite(value.Value) || Math.Abs(value.Value-expectedValue)>1e-9)
                        throw new IOException("重开后的配合驱动数值不符合请求。");
                }
            }
            // Filename synchronization replaces saved aliases on every later reopen.
            // A successful assembly must retain this option off to preserve its names.
            retainInstanceNames=true;
            return new(true,"创建、装配、重建、检查干涉并重新打开保存的原生装配。",plan.NativePath,componentResults,mateNames,interferences,true){MateReadbacks=mateReadbacks,MobilityReadbacks=mobilitySaved};
        }
        catch(Exception ex) {return new(false,ex.Message,plan.NativePath);}
        finally
        {
            if(app is not null)
            {
                if(model is not null) try {app.CloseDoc(model.GetTitle());} catch(System.Runtime.InteropServices.COMException) { }
                foreach(var doc in opened) try {app.CloseDoc(doc.GetTitle());} catch(System.Runtime.InteropServices.COMException) { }
                if(!retainInstanceNames && originalUpdateComponentNames is {} previous)app.SetUserPreferenceToggle((int)swUserPreferenceToggle_e.swExtRefUpdateCompNames,previous);
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
    private static void RenameAssemblyComponent(ISldWorks app,IModelDoc2 model,IComponent2 component,string name)
    {
        // Name2 requires selection and a temporarily disabled filename-sync option.
        // Restore the user's application preference even if native renaming fails.
        model.ClearSelection2(true);
        if(!component.Select4(false,null,false))throw new InvalidOperationException("无法选择待命名的装配零部件。");
        var option=(int)swUserPreferenceToggle_e.swExtRefUpdateCompNames;
        var previous=app.GetUserPreferenceToggle(option);
        try
        {
            app.SetUserPreferenceToggle(option,false);
            component.Name2=name;
            var actual=component.Name2;
            if(actual!=name && !System.Text.RegularExpressions.Regex.IsMatch(actual,"^"+System.Text.RegularExpressions.Regex.Escape(name)+@"-\d+$"))
                throw new InvalidOperationException($"装配零部件名称未保留：要求“{name}”，实际“{actual}”。");
        }
        finally {app.SetUserPreferenceToggle(option,previous);}
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
