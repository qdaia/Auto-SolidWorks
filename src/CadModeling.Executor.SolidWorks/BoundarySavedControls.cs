using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using CadModeling.Core;
using CadModeling.Ir;
using SolidWorks.Interop.sldworks;

internal sealed partial class SolidWorksComExecutor
{
    // SOLIDWORKS 2025 SDK swCommands_e values, kept here to avoid a new runtime
    // dependency on swcommands.dll. These commands never target another document.
    private const int BoundaryEditCommand=623,BoundaryAcceptCommand=-2,BoundaryCancelCommand=-1;

    private static ISldWorks ExistingBoundaryApplication()
    {
        var processes=Process.GetProcessesByName("SLDWORKS");
        if(processes.Length!=1)throw new InvalidOperationException("BOUNDARY_EDITOR_UNAVAILABLE：现有原生会话不唯一，不启动新会话。");
        var app=(ISldWorks)(Activator.CreateInstance(Type.GetTypeFromProgID("SldWorks.Application",true)!)
            ??throw new InvalidOperationException("BOUNDARY_EDITOR_UNAVAILABLE：无法绑定现有原生应用。"));
        if(app.GetProcessID()!=processes[0].Id||app.RevisionNumber()!="33.0.0")
            throw new InvalidOperationException("BOUNDARY_EDITOR_UNAVAILABLE：原生编辑读回只认证现有 SOLIDWORKS 2025 SP0 会话。");
        // Do not ReleaseCom this shared application RCW: the outer executor owns it.
        return app;
    }

    private static void RequireBoundaryEditorDocument(ISldWorks app,IModelDoc2 model)
    {
        if(app.IActiveDoc2 is not {} active||app.IsSame(active,model)!=1)
            throw new InvalidOperationException("BOUNDARY_EDITOR_IDENTITY：活动文档不是本次受控目标。");
    }

    private static void StartBoundaryEditor(ISldWorks app,IModelDoc2 model,IFeature feature)
    {
        RequireBoundaryEditorDocument(app,model);
        app.GetRunningCommandInfo(out _,out _,out var active);
        if(active)throw new InvalidOperationException("BOUNDARY_EDITOR_BUSY：已有原生命令，不能开始编辑。");
        var reference=RequireDefinitionIdentity(model,feature);
        model.ClearSelection2(true);
        if(!feature.Select2(false,0)||model.ISelectionManager.GetSelectedObjectCount2(-1)!=1
            ||!SamePersistentIdentity(model,reference,RequireDefinitionIdentity(model,model.ISelectionManager.GetSelectedObject6(1,-1))))
            throw new InvalidOperationException("BOUNDARY_EDITOR_IDENTITY：编辑目标选择不是唯一原生身份。");
        if(!app.RunCommand(BoundaryEditCommand,""))throw new InvalidOperationException("BOUNDARY_EDITOR_UNAVAILABLE：正式特征编辑未启动。");
        RequireBoundaryEditorActive(app,model,feature.Name);
    }

    private static void RequireBoundaryEditorActive(ISldWorks app,IModelDoc2 model,string name)
    {
        RequireBoundaryEditorDocument(app,model);
        app.GetRunningCommandInfo(out var command,out var currentName,out var active);
        if(!active||command!=BoundaryEditCommand||currentName!=name)
            throw new InvalidOperationException("BOUNDARY_EDITOR_IDENTITY：活动编辑命令或特征身份改变。");
    }

    private static void FinishBoundaryEditor(ISldWorks app,IModelDoc2 model,string name,bool accept)
    {
        RequireBoundaryEditorActive(app,model,name);
        var done=app.RunCommand(accept?BoundaryAcceptCommand:BoundaryCancelCommand,"");
        app.GetRunningCommandInfo(out _,out _,out var active);
        if(!done||active)throw new InvalidOperationException("BOUNDARY_EDITOR_PENDING：编辑尚未确认结束，不重放请求。");
    }

    private static byte[] ReadBoundarySavedBytes(string path)
    {
        using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite);
        if(stream.Length is <=0 or >1_073_741_824)throw new InvalidOperationException("BOUNDARY_CONTROL_COPY_UNAVAILABLE：保存文件为空或超出读回预算。");
        using var bytes=new MemoryStream();stream.CopyTo(bytes);return bytes.ToArray();
    }
    private static string BoundaryFileSha(string path)=>Convert.ToHexString(SHA256.HashData(ReadBoundarySavedBytes(path))).ToLowerInvariant();

    private static BoundaryDefinitionSnapshot ReadSavedBoundaryControls(IModelDoc2 model,IFeature feature)
    {
        var original=ReadBoundaryDefinition(model,feature);
        var path=model.GetPathName();var wasModified=model.GetSaveFlag();
        if(wasModified||!Path.IsPathFullyQualified(path)||!File.Exists(path)
            ||!Path.GetExtension(path).Equals(".SLDPRT",StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("BOUNDARY_CONTROL_COPY_UNAVAILABLE：原生控制认证需要未修改的已保存零件。");
        var app=ExistingBoundaryApplication();
        app.GetRunningCommandInfo(out _,out _,out var busy);
        if(busy)throw new InvalidOperationException("BOUNDARY_EDITOR_BUSY：已有命令，不能创建控制观察副本。");
        var previous=app.IActiveDoc2;
        var directory=Path.Combine(Path.GetTempPath(),"AutoSolidWorks","boundary-control-readback",Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var copyPath=Path.Combine(directory,"边界控制独立读回.SLDPRT");
        var bytes=ReadBoundarySavedBytes(path);var sourceSha=Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        using(var target=new FileStream(copyPath,FileMode.CreateNew,FileAccess.Write,FileShare.None))target.Write(bytes);
        IModelDoc2? copy=null;bool editing=false;
        BoundaryDefinitionSnapshot? before=null,after=null;int error=0,warning=0;
        try
        {
            if(model.GetSaveFlag()||BoundaryFileSha(path)!=sourceSha)throw new InvalidOperationException("BOUNDARY_CONTROL_COPY_CHANGED：复制期间来源改变。");
            copy=app.OpenDoc6(copyPath,1,1,"",ref error,ref warning) as IModelDoc2;
            if(copy is null||error!=0||copy.GetSaveFlag()||!copy.GetPathName().Equals(copyPath,StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("BOUNDARY_CONTROL_COPY_UNAVAILABLE：独立副本无法打开或身份不符。");
            int state=0;
            var copied=copy.Extension.GetObjectByPersistReference3(Convert.FromBase64String(original.FeatureReference),out state) as IFeature;
            if(state!=0||copied is null||copied.GetTypeName2()!="NetBlend"||copied.GetID()!=feature.GetID())
                throw new InvalidOperationException("BOUNDARY_CONTROL_COPY_IDENTITY：副本不包含相同持久身份的边界特征。");
            before=ReadBoundaryDefinition(copy,copied);
            editing=true;StartBoundaryEditor(app,copy,copied);
            Thread.Sleep(200); // bounded initialization of the native editor's data
            FinishBoundaryEditor(app,copy,copied.Name,true);editing=false;
            after=ReadBoundaryDefinition(copy,copied);
            if(copied.IsSuppressed()||copied.GetErrorCode2(out _)!=0)
                throw new InvalidOperationException("BOUNDARY_CONTROL_COPY_INVALID：副本编辑读回含原生特征故障。");
            app.CloseDoc(copy.GetTitle());copy=null;
            RestoreBoundaryObserver(app,previous);
            var evidence=new BoundaryControlCopyEvidence(sourceSha,BoundaryFileSha(copyPath),BoundaryFileSha(path),
                BoundaryFileSha(copyPath),wasModified,model.GetSaveFlag(),false,original,before,after);
            var result=BoundarySurfaceContract.VerifyCopiedSavedControls(evidence,(a,b)=>SamePersistentIdentity(model,a,b));
            File.WriteAllText(Path.Combine(directory,"readback.json"),JsonSerializer.Serialize(new{source_path=path,
                copy_path=copyPath,parameter_input=false,evidence,control_source="native_editor_byte_identical_unsaved_copy",
                source_document_unchanged=true,copy_file_unchanged=true},ModelingIrJson.Options));
            return result;
        }
        finally
        {
            if(copy is not null)
            {
                app.GetRunningCommandInfo(out var command,out var currentName,out var activeCommand);
                if(activeCommand)
                {
                    if(editing&&app.IActiveDoc2 is {} active&&app.IsSame(active,copy)==1
                        &&command==BoundaryEditCommand&&currentName==feature.Name)
                        FinishBoundaryEditor(app,copy,feature.Name,false);
                    else throw new InvalidOperationException("BOUNDARY_EDITOR_PENDING：副本观察后的命令归属不明，保留现场且不重放。");
                }
                app.CloseDoc(copy.GetTitle());
                RestoreBoundaryObserver(app,previous);
            }
        }
    }

    private static void RestoreBoundaryObserver(ISldWorks app,IModelDoc2? previous)
    {
        if(previous is null)return;
        if(app.IActiveDoc2 is {} active&&app.IsSame(active,previous)==1)return;
        app.GetRunningCommandInfo(out _,out _,out var busy);
        if(busy)throw new InvalidOperationException("BOUNDARY_EDITOR_PENDING：观察后有活动命令，不能切换文档。");
        int error=0;var restored=app.ActivateDoc3(previous.GetTitle(),false,0,ref error) as IModelDoc2;
        if(error!=0||restored is null||app.IsSame(restored,previous)!=1)
            throw new InvalidOperationException("BOUNDARY_EDITOR_IDENTITY：无法恢复观察前的活动文档。");
    }
}
