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
            return Failed("Source crops are produced by drawing ingestion; the SolidWorks executor will not fabricate a model screenshot as source evidence.");
        if(!Path.IsPathFullyQualified(capture.NativePath)||!File.Exists(capture.NativePath)||
           !Path.GetExtension(capture.NativePath).Equals(".sldprt",StringComparison.OrdinalIgnoreCase))
            return Failed("Active model observation requires an existing absolute .SLDPRT path.");
        if(!Path.IsPathFullyQualified(capture.OutputDirectory)||capture.PixelWidth is <256 or >4096||capture.PixelHeight is <256 or >4096)
            return Failed("Observation output directory must be absolute and bitmap dimensions must be within 256..4096 pixels.");
        var nativePath=Path.GetFullPath(capture.NativePath);
        var modelHash=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(nativePath)));
        if(!modelHash.Equals(request.ModelSha256,StringComparison.OrdinalIgnoreCase))
            return Failed("Saved model SHA-256 differs from the ObservationRequest; stale observation capture was rejected.");

        if(request.RequestedView==ObservationViewKind.FullPlaneSection)
        {
            if(request.Section is null) return Failed("Full-plane section observation is missing its frozen SectionSpec.");
            var captured=CaptureSectionOnSta(new SectionCaptureRequest{NativePath=nativePath,Spec=request.Section});
            if(!captured.Success||!captured.Complete||captured.Snapshot is null)
                return Failed("T10 section observation is incomplete: "+captured.Message);
            var fingerprint=ObservationSession.Fingerprint(request);
            var versionDirectory=Path.Combine(Path.GetFullPath(capture.OutputDirectory),$"{Safe(request.RequestId)}_{fingerprint[..12]}_{Guid.NewGuid():N}");
            var output=Path.Combine(versionDirectory,"section-observation.json");
            EnforceAllowedOutputRoot(output);Directory.CreateDirectory(versionDirectory);
            File.WriteAllText(output,JsonSerializer.Serialize(captured,ModelingIrJson.Options));
            if(new FileInfo(output).Length==0) return Failed("T10 section observation artifact was empty.");
            if(!modelHash.Equals(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(nativePath))),StringComparison.OrdinalIgnoreCase))
                return Failed("Native model changed while section observation evidence was captured.");
            return new()
            {
                Success=true,OutputPath=output,OutputSha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(output))),
                ActualView=ObservationViewKind.FullPlaneSection,CameraIdentity="T10:BRep-section",
                SectionPlaneFingerprint=SectionVerifier.Fingerprint(request.Section),CoverageIds=[request.Section.SectionId],
                Message="Captured a new fixed-plane T10 B-Rep section artifact; no orthographic screenshot fallback was used."
            };
        }

        SldWorks? app=null;IModelDoc2? model=null;var owned=false;
        try
        {
            app=(SldWorks)Activator.CreateInstance(Type.GetTypeFromProgID("SldWorks.Application",true)!)!;
            model=(IModelDoc2?)app.GetOpenDocumentByName(nativePath);
            if(model is not null)
                throw new InvalidOperationException("Active bitmap observation refuses to change the view of an already-open user document; close it so the executor can open an isolated read-only instance.");
            if(model is null)
            {
                int errors=0,warnings=0;
                model=(IModelDoc2?)PerformanceTrace.Measure("native.open", () => app.OpenDoc6(nativePath,(int)swDocumentTypes_e.swDocPART,
                    (int)(swOpenDocOptions_e.swOpenDocOptions_Silent|swOpenDocOptions_e.swOpenDocOptions_ReadOnly),"",ref errors,ref warnings));
                owned=model is not null&&Path.GetFullPath(model.GetPathName()).Equals(nativePath,StringComparison.OrdinalIgnoreCase);
                if(model is null||!owned) throw new IOException($"Could not reopen exact saved part for observation (errors={errors}).");
            }
            if(model.GetSaveFlag()) throw new InvalidOperationException("Observation refuses unsaved in-memory model state.");

            PrepareModelPresentation(model);
            var viewId=request.RequestedView switch
            {
                ObservationViewKind.OrthographicFront=>(int)swStandardViews_e.swFrontView,
                ObservationViewKind.OrthographicTop=>(int)swStandardViews_e.swTopView,
                ObservationViewKind.OrthographicRight=>(int)swStandardViews_e.swRightView,
                ObservationViewKind.Isometric=>(int)swStandardViews_e.swIsometricView,
                _=>throw new InvalidOperationException("Requested model observation view is not registered by the SolidWorks renderer.")
            };
            model.ShowNamedView2("",viewId);
            var focused=false;
            if(request.TargetGeometry is { } target)
            {
                var resolution=ResolveGeometryReference(model,nativePath,modelHash,target,null,request.SourceRevisionId);
                if(resolution.Status!=GeometryRefResolutionStatus.Resolved||resolution.Candidate?.NativePersistentReference is not {Length:>0} encoded)
                    throw new InvalidOperationException("Observation target GeometryRef did not uniquely resolve on the current saved model.");
                int state=0;var entity=model.Extension.GetObjectByPersistReference3(Convert.FromBase64String(encoded),out state);
                if(state!=0||entity is null) throw new InvalidOperationException("Resolved observation target became stale before rendering.");
                model.ClearSelection2(true);
                focused=entity switch
                {
                    IEntity e=>e.Select4(false,null),
                    IFeature f=>f.Select2(false,0),
                    IBody2 b=>b.Select2(false,null),
                    _=>false
                };
                if(!focused) throw new InvalidOperationException("Resolved observation target could not be selected for object-focused capture.");
                model.ViewZoomToSelection();
            }
            else model.ViewZoomtofit2();
            model.GraphicsRedraw2();

            var fingerprint=ObservationSession.Fingerprint(request);
            var versionDirectory=Path.Combine(Path.GetFullPath(capture.OutputDirectory),$"{Safe(request.RequestId)}_{fingerprint[..12]}_{Guid.NewGuid():N}");
            var output=Path.Combine(versionDirectory,"observation.bmp");
            EnforceAllowedOutputRoot(output);Directory.CreateDirectory(versionDirectory);
            if(!Convert.ToBoolean(model.SaveBMP(output,capture.PixelWidth,capture.PixelHeight))||!File.Exists(output)||new FileInfo(output).Length==0)
                throw new IOException("SolidWorks failed to export the current observation bitmap.");
            var outputHash=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(output)));
            if(!modelHash.Equals(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(nativePath))),StringComparison.OrdinalIgnoreCase))
                throw new IOException("Native model changed while observation evidence was rendered.");
            return new()
            {
                Success=true,
                OutputPath=output,
                OutputSha256=outputHash,
                ActualView=request.RequestedView,
                CameraIdentity=$"standard:{request.RequestedView}:{(focused?"target-selection":"zoom-fit")}",
                CoverageIds=request.TargetGeometry is null?[]:[request.TargetGeometry.RefId],
                Message="Captured a new identity-bound bitmap from the exact saved model; no prior bitmap fallback is used."
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
