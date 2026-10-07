using System.Security.Cryptography;
using System.Text.Json;
using CadModeling.Core;
using CadModeling.Ir;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

internal sealed partial class SolidWorksComExecutor
{
    public async Task<ObservationRenderArtifact> CaptureObservationAsync(ObservationCaptureRequest request,CancellationToken cancellationToken=default)
    {
        await _serialGate.WaitAsync(cancellationToken);
        try { return await _sta.InvokeAsync(()=>CaptureObservationOnSta(request),cancellationToken); }
        finally { _serialGate.Release(); }
    }

    private static ObservationRenderArtifact CaptureObservationOnSta(ObservationCaptureRequest capture)
    {
        using var timing = CadModeling.Ir.PerformanceTrace.Begin("native.observation");
        var request=capture.Observation;
        try { ObservationSession.Validate(request); }
        catch(ArgumentException ex) { return Failed(ex.Message); }
        if(request.RequestedView==ObservationViewKind.SourceCrop)
            return Failed("源数据由摄入绘制产生；SolidWorks执行器不会生成模型截图作为源证据。");
        if(!Path.IsPathFullyQualified(capture.NativePath)||!File.Exists(capture.NativePath)||
           !Path.GetExtension(capture.NativePath).Equals(".sldprt",StringComparison.OrdinalIgnoreCase))
            return Failed("活动模型观察需要一个现有的绝对 .SLDPRT 路径。");
        if(!Path.IsPathFullyQualified(capture.OutputDirectory)||capture.PixelWidth is <256 or >4096||capture.PixelHeight is <256 or >4096)
            return Failed("观察输出目录必须为绝对路径，且位图尺寸必须在 256..4096 像素范围内。");
        var nativePath=Path.GetFullPath(capture.NativePath);
        var modelHash=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(nativePath)));
        if(!modelHash.Equals(request.ModelSha256,StringComparison.OrdinalIgnoreCase))
            return Failed("保存的模型 SHA-256 与 ObservationRequest 不同； stale 观察捕捉被拒绝。");

        if(request.RequestedView==ObservationViewKind.FullPlaneSection)
        {
            if(request.Section is null) return Failed("平面剖面观察缺少已锁定的 SectionSpec。");
            var captured=CaptureSectionOnSta(new SectionCaptureRequest{NativePath=nativePath,Spec=request.Section});
            if(!captured.Success||!captured.Complete||captured.Snapshot is null)
                return Failed("T10 区域剖面观察不完整："+captured.Message);
            var fingerprint=ObservationSession.Fingerprint(request);
            var versionDirectory=Path.Combine(Path.GetFullPath(capture.OutputDirectory),$"{Safe(request.RequestId)}_{fingerprint[..12]}_{Guid.NewGuid():N}");
            var output=Path.Combine(versionDirectory,"section-observation.json");
            EnforceAllowedOutputRoot(output);Directory.CreateDirectory(versionDirectory);
            File.WriteAllText(output,JsonSerializer.Serialize(captured,ModelingIrJson.Options));
            if(new FileInfo(output).Length==0) return Failed("T10 包围盒剖面观察artifact为空。");
            if(!modelHash.Equals(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(nativePath))),StringComparison.OrdinalIgnoreCase))
                return Failed("原生模型在剖面观察证据被捕捉时被更改。");
            return new()
            {
                Success=true,OutputPath=output,OutputSha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(output))),
                ActualView=ObservationViewKind.FullPlaneSection,CameraIdentity="T10:BRep-section",
                SectionPlaneFingerprint=SectionVerifier.Fingerprint(request.Section),CoverageIds=[request.Section.SectionId],
                Message="捕获了一个新的固定平面 T10 B-Rep 剖面艺术作品；没有正射投影截图作为后备。"
            };
        }

        SldWorks? app=null;IModelDoc2? model=null;var owned=false;
        try
        {
            app=(SldWorks)Activator.CreateInstance(Type.GetTypeFromProgID("SldWorks.Application",true)!)!;
            model=(IModelDoc2?)app.GetOpenDocumentByName(nativePath);
            if(model is not null)
                throw new InvalidOperationException("活动位图观察拒绝更改已打开用户文档的视图；关闭它以便执行者可以打开一个隔离的只读实例。");
            if(model is null)
            {
                int errors=0,warnings=0;
                model=(IModelDoc2?)PerformanceTrace.Measure("native.open", () => app.OpenDoc6(nativePath,(int)swDocumentTypes_e.swDocPART,
                    (int)(swOpenDocOptions_e.swOpenDocOptions_Silent|swOpenDocOptions_e.swOpenDocOptions_ReadOnly),"",ref errors,ref warnings));
                owned=model is not null&&Path.GetFullPath(model.GetPathName()).Equals(nativePath,StringComparison.OrdinalIgnoreCase);
                if(model is null||!owned) throw new IOException($"无法重新打开已保存的零件进行观察（errors={errors}）。");
            }
            if(model.GetSaveFlag()) throw new InvalidOperationException("观察拒绝未保存的内存模型状态。");

            PrepareModelPresentation(model);
            var viewId=request.RequestedView switch
            {
                ObservationViewKind.OrthographicFront=>(int)swStandardViews_e.swFrontView,
                ObservationViewKind.OrthographicTop=>(int)swStandardViews_e.swTopView,
                ObservationViewKind.OrthographicRight=>(int)swStandardViews_e.swRightView,
                ObservationViewKind.Isometric=>(int)swStandardViews_e.swIsometricView,
                _=>throw new InvalidOperationException("请求的模型观察视图未被SolidWorks渲染器注册。")
            };
            model.ShowNamedView2("",viewId);
            var focused=false;
            if(request.TargetGeometry is { } target)
            {
                var resolution=ResolveGeometryReference(model,nativePath,modelHash,target,null,request.SourceRevisionId);
                if(resolution.Status!=GeometryRefResolutionStatus.Resolved||resolution.Candidate?.NativePersistentReference is not {Length:>0} encoded)
                    throw new InvalidOperationException("观测目标 GeometryRef 在当前保存的模型中无法唯一确定。");
                int state=0;var entity=model.Extension.GetObjectByPersistReference3(Convert.FromBase64String(encoded),out state);
                if(state!=0||entity is null) throw new InvalidOperationException("已解决的观察目标在渲染之前过期。");
                model.ClearSelection2(true);
                focused=entity switch
                {
                    IEntity e=>e.Select4(false,null),
                    IFeature f=>f.Select2(false,0),
                    IBody2 b=>b.Select2(false,null),
                    _=>false
                };
                if(!focused) throw new InvalidOperationException("无法为聚焦捕捉对象选择观察目标。");
                model.ViewZoomToSelection();
            }
            else model.ViewZoomtofit2();
            model.GraphicsRedraw2();

            var fingerprint=ObservationSession.Fingerprint(request);
            var versionDirectory=Path.Combine(Path.GetFullPath(capture.OutputDirectory),$"{Safe(request.RequestId)}_{fingerprint[..12]}_{Guid.NewGuid():N}");
            var output=Path.Combine(versionDirectory,"observation.bmp");
            EnforceAllowedOutputRoot(output);Directory.CreateDirectory(versionDirectory);
            if(!Convert.ToBoolean(model.SaveBMP(output,capture.PixelWidth,capture.PixelHeight))||!File.Exists(output)||new FileInfo(output).Length==0)
                throw new IOException("SolidWorks 失败导出当前观察位图。");
            var outputHash=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(output)));
            if(!modelHash.Equals(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(nativePath))),StringComparison.OrdinalIgnoreCase))
                throw new IOException("原生模型在观察证据渲染时被更改。");
            return new()
            {
                Success=true,
                OutputPath=output,
                OutputSha256=outputHash,
                ActualView=request.RequestedView,
                CameraIdentity=$"standard:{request.RequestedView}:{(focused?"target-selection":"zoom-fit")}",
                CoverageIds=request.TargetGeometry is null?[]:[request.TargetGeometry.RefId],
                Message="捕获了一个基于身份的新位图图像，来自保存的精确模型；未使用先前的位图替代方案。"
            };
        }
        catch(Exception ex) when(ex is IOException or InvalidOperationException or UnauthorizedAccessException or ArgumentException)
        { return Failed(ex.Message); }
        finally
        {
            // Never mutate an already-open user document on the refusal path.  Only the isolated
            // read-only document opened and owned by this capture may have its selection cleared.
            if(owned) try { model?.ClearSelection2(true); } catch { }
            if(owned&&model is not null&&app is not null) PerformanceTrace.Measure("native.close", () => app.CloseDoc(model.GetTitle()));
            if(owned) ReleaseCom(model);
            ReleaseCom(app);
        }

        ObservationRenderArtifact Failed(string message)=>new(){Success=false,ActualView=request.RequestedView,Message=message};
        static string Safe(string value)
        {
            var invalid=Path.GetInvalidFileNameChars().ToHashSet();
            var safe=new string(value.Select(ch=>invalid.Contains(ch)?'_':ch).ToArray()).Trim();
            return string.IsNullOrWhiteSpace(safe)?"observation":safe[..Math.Min(safe.Length,64)];
        }
    }
}
