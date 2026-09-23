using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CadModeling.Ir;

namespace CadModeling.Core;

public sealed record Stage4EvidenceBundle
{
    public IReadOnlyList<ConnectivityCheck> ConnectivityChecks { get; init; }=[];
    public IReadOnlyList<ProjectionReport> ProjectionReports { get; init; }=[];
}

public enum RevolvedDrivingParameterKind { Diameter, AxialLength }

public sealed record RevolvedDrivingDimensionBinding
{
    public required string SourceFactId { get; init; }
    public RevolvedDrivingParameterKind Parameter { get; init; }
    public required string DimensionName { get; init; }
    public string? OwnerFeatureName { get; init; }
    public RadialDimensionSemantic NativeRadialSemantic { get; init; }=RadialDimensionSemantic.Diameter;
}

public sealed record RevolvedFamilyInspectionBinding
{
    public required string FeatureName { get; init; }
    /// <summary>
    /// Independent native-parameter map for source step facts. Every supported step must bind one
    /// diameter and one axial-length driving dimension; probe requests cannot define this set.
    /// </summary>
    public required IReadOnlyList<RevolvedDrivingDimensionBinding> DrivingDimensions { get; init; }
}

public sealed record HoleGroupInspectionBinding
{
    public string? PatternFeatureName { get; init; }
    public string? PatternCountDimensionName { get; init; }
    public string? PatternSpacingDimensionName { get; init; }
    public string? PatternAngleDimensionName { get; init; }
    /// <summary>Optional explicit local-XY padding for actual-instance enumeration around the frozen source group envelope.</summary>
    public double? InstanceScopePaddingMm { get; init; }
}

public sealed record EdgeTreatmentInspectionBinding
{
    public required string FeatureName { get; init; }
    public required string PrimaryDimensionName { get; init; }
    public string? SecondaryDimensionName { get; init; }
}

public sealed record Stage4RepairEvidenceBundle
{
    public required RepairAttempt Attempt { get; init; }
    public required RepairExecutionEvidence Execution { get; init; }
    public required CoverageReport Coverage { get; init; }
    public required ModelDiff Diff { get; init; }
    public IReadOnlyList<RepairCheckResult> Checks { get; init; }=[];
}

public sealed record Stage4SavedModelAcceptance<TObservation> where TObservation:class
{
    public required string NativePath { get; init; }
    public required ModelInspection Inspection { get; init; }
    public TObservation? Observation { get; init; }
    public required Stage4VerificationResult Verification { get; init; }
    public bool InspectionCacheHit { get; init; }
    public bool Passed=>Inspection.Success&&Inspection.ModelReopened&&Inspection.CaptureComplete&&Verification.Passed;
}

/// <summary>
/// Production saved-model adapter for the finite T17-T19 families.  Expected values come from
/// the source profile; actual geometry/feature parameters come from a read-only, rebuilt, reopened
/// native inspection.  T08/T09 evidence is accepted only through producer-owned result types.
/// </summary>
public sealed class Stage4SavedModelAcceptanceWorkflow(IModelingExecutor executor,ArtifactCacheStore cache)
{
    private long _requestSequence;

    public async Task<Stage4SavedModelAcceptance<RevolvedFamilyObservation>> VerifyRevolvedAsync(
        string nativePath,RevolvedFamilyProfile profile,RevolvedFamilyInspectionBinding binding,Stage4EvidenceBundle evidence,CancellationToken cancellationToken=default)
    {
        ArgumentNullException.ThrowIfNull(profile);ArgumentNullException.ThrowIfNull(binding);ArgumentNullException.ThrowIfNull(evidence);
        var (inspection,cacheHit)=await InspectCachedAsync(nativePath,profile.SourceRevisionId,
            new ModelInspectionRequest(Path.GetFullPath(nativePath),EditabilityProbes:profile.EditabilityProbes),cancellationToken);
        var bound=BindEvidence(profile.SourceSha256,profile.SourceRevisionId,profile.RequiredScopeFingerprint,inspection.ModelSha256,profile.CheckRequirementFingerprints,evidence);
        var measured=profile.Steps.Select(step=>MeasureRevolvedStep(profile,step,inspection.Cylinders)).ToArray();
        var revolveFeature=inspection.Features?.SingleOrDefault(f=>f.Name.Equals(binding.FeatureName,StringComparison.Ordinal));
        var revolveRecognized=revolveFeature is {Suppressed:false}&&revolveFeature.Type.Contains("Revol",StringComparison.OrdinalIgnoreCase);
        var axisCylinder=inspection.Cylinders.Where(c=>!c.Interior).OrderByDescending(c=>c.AreaMm2).FirstOrDefault();
        var observation=new RevolvedFamilyObservation
        {
            SourceSha256=profile.SourceSha256,SourceRevisionId=profile.SourceRevisionId,ModelSha256=inspection.ModelSha256??string.Empty,
            ModelReopened=inspection.ModelReopened,NativeFeatureKind=revolveRecognized?NativeFeatureKind.RevolveBoss:NativeFeatureKind.ReferencePlane,
            NativeDrivingDimensionsEditable=RevolvedProbesMatch(profile,binding,inspection,revolveFeature),
            RebuildSucceeded=inspection.RebuildSucceeded,
            AxisOriginMm=axisCylinder?.AxisStartMm??new(double.NaN,double.NaN,double.NaN),
            AxisDirection=axisCylinder?.Direction??new(double.NaN,double.NaN,double.NaN),Steps=measured,CheckEvidence=bound
        };
        var verification=RevolvedFamilyVerifier.Evaluate(profile,observation);
        if(profile.Kind==RevolvedFamilyKind.SteppedShaftWithAxialHole&&!RevolvedAxialScopeMatches(profile,evidence.ConnectivityChecks,inspection.ModelSha256))
            verification=AppendFailure(verification,"axial-hole-geometry-scope","The T08 producer must measure this rotation axis and the entire source axial interval, not another void in the same model.");
        return PackageResult(nativePath,inspection,observation,verification,cacheHit);
    }

    public async Task<Stage4SavedModelAcceptance<HoleGroupObservation>> VerifyHoleGroupAsync(
        string nativePath,HoleGroupProfile profile,HoleGroupInspectionBinding binding,Stage4EvidenceBundle evidence,CancellationToken cancellationToken=default)
    {
        ArgumentNullException.ThrowIfNull(profile);ArgumentNullException.ThrowIfNull(binding);ArgumentNullException.ThrowIfNull(evidence);
        var (inspection,cacheHit)=await InspectCachedAsync(nativePath,profile.SourceRevisionId,
            new ModelInspectionRequest(Path.GetFullPath(nativePath),EditabilityProbes:profile.PatternEditabilityProbes),cancellationToken);
        var bound=BindEvidence(profile.SourceSha256,profile.SourceRevisionId,profile.RequiredScopeFingerprint,inspection.ModelSha256,profile.CheckRequirementFingerprints,evidence);
        var connectivity=evidence.ConnectivityChecks.Where(check=>
            (profile.ConnectivityCheckId is null||check.RequirementId.Equals(profile.ConnectivityCheckId,StringComparison.Ordinal)||check.RequirementId.StartsWith(profile.ConnectivityCheckId+":",StringComparison.Ordinal))&&
            check.SourceSha256?.Equals(profile.SourceSha256,StringComparison.OrdinalIgnoreCase)==true&&
            check.SourceRevisionId.Equals(profile.SourceRevisionId,StringComparison.Ordinal)&&
            check.SourceFactId.Equals(profile.SourceFactId,StringComparison.Ordinal)&&
            check.SourceFactFingerprint.Equals(profile.SourceFactFingerprint,StringComparison.OrdinalIgnoreCase)&&
            check.ActualModelSha256.Equals(inspection.ModelSha256??string.Empty,StringComparison.OrdinalIgnoreCase)&&
            check.ModelReopened&&check.ObservationComplete&&check.Status==ConnectivityStatus.Passed&&
            Stage4EvidenceBinding.Hash(check.GeometryScopeFingerprint)&&check.GeometryScopeAnchorMm is { } anchor&&Finite(anchor)).ToArray();
        var instances=EnumerateHoleInstances(profile,binding,inspection,connectivity);
        var pattern=BuildPatternObservation(profile,binding,inspection.Features??[],inspection.EditabilityProbes,inspection.ModelSha256,inspection.RebuildSucceeded);
        var observation=new HoleGroupObservation
        {
            SourceSha256=profile.SourceSha256,SourceRevisionId=profile.SourceRevisionId,ModelSha256=inspection.ModelSha256??string.Empty,
            ModelReopened=inspection.ModelReopened,Instances=instances,Pattern=pattern,CheckEvidence=bound
        };
        var verification=HoleGroupVerifier.Evaluate(profile,observation);
        if(profile.HoleKind==HoleKind.Countersink&&instances.Any(i=>i.CountersinkDiameterMm is null)||
           profile.HoleKind==HoleKind.Tapped&&instances.Any(i=>i.ThreadDesignation is null))
            verification=AppendFailure(verification,"subtype-acquisition-incomplete","Every hole must have one complete, geometrically bound native cone or cosmetic thread. Missing or ambiguous acquisition remains unverifiable.");
        return PackageResult(nativePath,inspection,observation,verification,cacheHit);
    }

    public async Task<Stage4SavedModelAcceptance<EdgeTreatmentObservation>> VerifyEdgeTreatmentAsync(
        string nativePath,EdgeTreatmentIntent intent,EdgeTreatmentInspectionBinding binding,Stage4RepairEvidenceBundle? repair=null,CancellationToken cancellationToken=default)
    {
        ArgumentNullException.ThrowIfNull(intent);ArgumentNullException.ThrowIfNull(binding);
        var request=new ModelInspectionRequest(Path.GetFullPath(nativePath),GeometryReferences:intent.TargetEdges,SourceRevisionId:intent.SourceRevisionId,
            EditabilityProbes:intent.EditabilityProbes);
        var (inspection,cacheHit)=await InspectCachedAsync(nativePath,intent.SourceRevisionId,request,cancellationToken);
        var feature=(inspection.Features??[]).SingleOrDefault(f=>f.Name.Equals(binding.FeatureName,StringComparison.Ordinal));
        var (typeRecognized,route)=RecognizeEdgeTreatment(feature?.Type);
        var primaryUnit=intent.Route==EdgeTreatmentRoute.SolidChamfer?"Millimeter":"Millimeter";
        var secondaryUnit=intent.Route==EdgeTreatmentRoute.SolidChamfer&&intent.ChamferMode==ChamferMode.DistanceAngle?"Degree":"Millimeter";
        var primary=Dimension(feature,binding.PrimaryDimensionName,primaryUnit);
        var secondary=Dimension(feature,binding.SecondaryDimensionName,secondaryUnit);
        var requiredEdgeDimensions=RequiredEdgeTreatmentDimensions(intent,binding);
        var probeMatch=ProbesMatch(intent.EditabilityProbes,inspection.EditabilityProbes,inspection.ModelSha256,binding.FeatureName,requiredEdgeDimensions,feature);
        var topologyChanged=intent.TargetEdges.Any(r=>!r.ModelSha256.Equals(inspection.ModelSha256??string.Empty,StringComparison.OrdinalIgnoreCase));
        var actual=new EdgeTreatmentObservation
        {
            SourceSha256=intent.SourceSha256,SourceRevisionId=intent.SourceRevisionId,ModelSha256=inspection.ModelSha256??string.Empty,
            ModelReopened=inspection.ModelReopened,ActualRoute=route,
            RadiusMm=intent.Route is EdgeTreatmentRoute.ProfileArc or EdgeTreatmentRoute.SolidFillet?primary:0,
            DistanceMm=intent.Route==EdgeTreatmentRoute.SolidChamfer?primary:0,
            SecondDistanceMm=intent.Route==EdgeTreatmentRoute.SolidChamfer&&(intent.ChamferMode is ChamferMode.TwoDistances or ChamferMode.EqualDistance)?secondary:0,
            AngleDegrees=intent.Route==EdgeTreatmentRoute.SolidChamfer&&intent.ChamferMode==ChamferMode.DistanceAngle?secondary:0,ChamferMode=intent.ChamferMode,
            NativeFeatureType=feature?.Type??string.Empty,NativeFeatureTypeRecognized=typeRecognized,FeatureSuppressed=feature?.Suppressed??false,
            NativeEditable=feature is {Suppressed:false}&&typeRecognized&&probeMatch,RebuildSucceeded=inspection.RebuildSucceeded,
            TopologyChanged=topologyChanged,
            EdgeResolutions=inspection.GeometryRefResolutions,
            NativeDrivingEdgePersistentReferences=feature?.DrivingEdgePersistentReferences??[],
            RepairStrategiesUsed=repair is null?[]:InferRepairStrategies(repair.Attempt),
            RepairEvidenceSupplied=repair is not null,
            RepairAttempt=repair?.Attempt,RepairExecution=repair?.Execution,RepairCoverage=repair?.Coverage,RepairChecks=repair?.Checks??[],CollateralDiff=repair?.Diff
        };
        var verification=EdgeTreatmentVerifier.Evaluate(intent,actual);
        if(repair is not null&&repair.Execution.EvidenceMode!="native_executor")
            verification=AppendFailure(verification,"native-repair-execution-required",
                "Saved native acceptance requires an actual native_executor repair receipt. Synthetic fixtures are restricted to development contract tests.");
        if(intent.TargetEdges.Any(r=>r.InputToFeature is not null&&r.InputToFeature!=binding.FeatureName))
            verification=AppendFailure(verification,"native-edge-input-scope","Feature-input references must belong to the exact native treatment being certified.");
        if(feature is null)verification=AppendFailure(verification,"native-feature-missing","The named saved native edge-treatment feature was not found.");
        return PackageResult(nativePath,inspection,actual,verification,cacheHit);
    }

    private static Stage4SavedModelAcceptance<T> PackageResult<T>(string nativePath,ModelInspection inspection,T observation,
        Stage4VerificationResult verification,bool cacheHit) where T:class
    {
        // Missing required native values remain NaN for deterministic fail-closed evaluation.
        // Do not emit these internal sentinels into a public numeric JSON contract, nor replace
        // unknown measurements with source values. Return the failed checks without an observation.
        T? captured=observation;
        try { _=JsonSerializer.SerializeToElement(observation,ModelingIrJson.Options); }
        catch(ArgumentException)
        {
            captured=null;
            verification=AppendFailure(verification,"native-observation-not-serializable",
                "Native observation contains unavailable/non-finite required measurements; the observation is withheld and acceptance remains unverifiable.");
        }
        return new(){NativePath=Path.GetFullPath(nativePath),Inspection=inspection,Observation=captured,Verification=verification,InspectionCacheHit=cacheHit};
    }

    private async Task<(ModelInspection Inspection,bool CacheHit)> InspectCachedAsync(string nativePath,string sourceRevision,ModelInspectionRequest request,CancellationToken cancellationToken)
    {
        var full=Path.GetFullPath(nativePath);
        if(!File.Exists(full))return (new(false,"Saved native model does not exist.",full),false);
        var modelSha=DrawingPlanValidation.FileHash(full);
        var requestFingerprint=Sha(JsonSerializer.Serialize(request,ModelingIrJson.Options));
        var health=await executor.HealthAsync(cancellationToken);
        var deps=new[]{new ArtifactDependency("model.sha256",modelSha),new ArtifactDependency("inspection.request.fingerprint",requestFingerprint),
            new ArtifactDependency("inspector.version",ComponentContentIdentity.ForType(typeof(Stage4SavedModelAcceptanceWorkflow),"stage4-saved-model-adapter/v1")),
            new ArtifactDependency("solidworks.version",health.SolidWorksRevision??"unavailable"),new ArtifactDependency("model.reopened","true")};
        var seq=Interlocked.Increment(ref _requestSequence);var slot="stage4-inspection:"+Sha(full+"|"+requestFingerprint);
        cache.MarkCurrent(slot,sourceRevision,seq);
        var context=new FreshnessContext{SourceRevisionId=sourceRevision,CurrentRequestSequence=seq,Dependencies=deps};
        if(cache.TryGetFresh<ModelInspection>(slot,context,out var cached,out _)&&cached is not null)return(cached,true);
        var inspection=await executor.InspectAsync(request,cancellationToken);
        if(!inspection.Success||!inspection.CaptureComplete||!inspection.ModelReopened||!string.Equals(inspection.ModelSha256,modelSha,StringComparison.OrdinalIgnoreCase))return(inspection,false);
        var artifact=new CachedArtifact{ArtifactId=slot,Kind=CacheArtifactKind.SavedModelInspection,SourceRevisionId=sourceRevision,RequestSequence=seq,Dependencies=deps};
        cache.TryPublish(slot,artifact,inspection,out _);
        return(inspection,false);
    }

    private static IReadOnlyList<Stage4BoundCheckEvidence> BindEvidence(string sourceSha,string sourceRevision,string scope,string? modelSha,
        IReadOnlyDictionary<string,string> expected,Stage4EvidenceBundle evidence)
    {
        if(modelSha is null)return[];
        var result=new List<Stage4BoundCheckEvidence>();
        foreach(var item in evidence.ConnectivityChecks)
            if(expected.ContainsKey(item.RequirementId))result.Add(Stage4EvidenceAdapter.FromConnectivity(item,scope));
        foreach(var report in evidence.ProjectionReports)
            foreach(var requirement in report.Requirements.Where(r=>expected.ContainsKey(r.RequirementId)))
                result.Add(Stage4EvidenceAdapter.FromProjection(report,requirement.RequirementId,scope));
        // Keep producer-owned identities unchanged; the family verifier performs the exact source/model/scope comparison.
        return result.Where(item=>item.SourceSha256.Equals(sourceSha,StringComparison.OrdinalIgnoreCase)&&item.SourceRevisionId==sourceRevision&&item.ModelSha256.Equals(modelSha,StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    private static RevolvedStepMeasurement MeasureRevolvedStep(RevolvedFamilyProfile profile,RevolvedStepRequirement expected,IReadOnlyList<MeasuredCylinder> cylinders)
    {
        var axis=Unit(profile.AxisDirection);var targetStart=expected.AxialStartMm;var targetEnd=targetStart+expected.AxialLengthMm;
        var matches=cylinders.Where(c=>!c.Interior&&Finite(c.AxisStartMm)&&Finite(c.AxisEndMm)&&Finite(c.Direction)&&
            Math.Abs(Dot(Unit(c.Direction),axis))>.999&&LateralDistance(c.AxisStartMm,profile.AxisOriginMm,axis)<=expected.ToleranceMm)
            .Select(c=>(c,a:Dot(Sub(c.AxisStartMm,profile.AxisOriginMm),axis),b:Dot(Sub(c.AxisEndMm,profile.AxisOriginMm),axis)))
            .Where(x=>Math.Abs(Math.Min(x.a,x.b)-Math.Min(targetStart,targetEnd))<=expected.ToleranceMm&&Math.Abs(Math.Max(x.a,x.b)-Math.Max(targetStart,targetEnd))<=expected.ToleranceMm)
            .OrderBy(x=>Math.Abs(x.c.RadiusMm*2-expected.ExpectedDiameterMm)).ToArray();
        return matches.Length==0?new(double.NaN,double.NaN,double.NaN):new(Math.Min(matches[0].a,matches[0].b),Math.Abs(matches[0].b-matches[0].a),matches[0].c.RadiusMm*2);
    }

    private sealed record HoleLayer(MeasuredCylinder Cylinder,ProfilePoint Center,double Start,double End);

    private static IReadOnlyList<HoleInstanceMeasurement> EnumerateHoleInstances(HoleGroupProfile profile,HoleGroupInspectionBinding binding,
        ModelInspection inspection,IReadOnlyList<ConnectivityCheck> connectivity)
    {
        var cylinders=inspection.Cylinders;
        var (u,v,n)=Basis(profile.Frame);var origin=profile.Frame.OriginMm;
        var layers=cylinders.Where(c=>c.Interior&&Finite(c.AxisStartMm)&&Finite(c.AxisEndMm)&&Finite(c.Direction)&&double.IsFinite(c.RadiusMm)&&c.RadiusMm>0&&
                Math.Abs(Dot(Unit(c.Direction),n))>.999)
            .Select(c=>
            {
                var a=Dot(Sub(c.AxisStartMm,origin),n);var b=Dot(Sub(c.AxisEndMm,origin),n);
                return new HoleLayer(c,ToLocal(c.AxisStartMm,origin,u,v),Math.Min(a,b),Math.Max(a,b));
            }).ToArray();
        var groups=new List<List<HoleLayer>>();
        foreach(var layer in layers.OrderBy(x=>x.Center.Xmm).ThenBy(x=>x.Center.Ymm).ThenBy(x=>x.Cylinder.RadiusMm))
        {
            var group=groups.FirstOrDefault(g=>Distance(g[0].Center,layer.Center)<=profile.PositionToleranceMm);
            if(group is null){group=[];groups.Add(group);}group.Add(layer);
        }
        var padding=binding.InstanceScopePaddingMm??DefaultHoleScopePadding(profile);
        if(!double.IsFinite(padding)||padding<0)return[];
        var minX=profile.ExpectedCenters.Min(p=>p.Xmm)-padding;var maxX=profile.ExpectedCenters.Max(p=>p.Xmm)+padding;
        var minY=profile.ExpectedCenters.Min(p=>p.Ymm)-padding;var maxY=profile.ExpectedCenters.Max(p=>p.Ymm)+padding;
        var result=new List<HoleInstanceMeasurement>();
        foreach(var group in groups)
        {
            var center=new ProfilePoint(group.Average(x=>x.Center.Xmm),group.Average(x=>x.Center.Ymm));
            if(center.Xmm<minX||center.Xmm>maxX||center.Ymm<minY||center.Ymm>maxY)continue;
            var minimumRadius=group.Min(x=>x.Cylinder.RadiusMm);
            var primary=group.Where(x=>Math.Abs(x.Cylinder.RadiusMm-minimumRadius)<=1e-6)
                .OrderByDescending(x=>x.End-x.Start).ToArray();
            if(primary.Length==0)continue;
            var primaryStart=primary.Min(x=>x.Start);var primaryEnd=primary.Max(x=>x.End);
            var primaryDiameter=primary.Average(x=>x.Cylinder.RadiusMm*2);
            var secondaryLayers=group.Where(x=>x.Cylinder.RadiusMm>minimumRadius+1e-6).ToArray();
            var secondary=profile.HoleKind==HoleKind.Counterbore&&secondaryLayers.Length==1?secondaryLayers[0]:null;
            bool? layerReverse=null;var channelStart=primaryStart;var channelEnd=primaryEnd;
            if(secondary is not null)
            {
                var frontContinuous=secondary.Start<primaryStart-profile.SizeToleranceMm/2&&Math.Abs(secondary.End-primaryStart)<=profile.SizeToleranceMm;
                var rearContinuous=secondary.End>primaryEnd+profile.SizeToleranceMm/2&&Math.Abs(secondary.Start-primaryEnd)<=profile.SizeToleranceMm;
                if(frontContinuous^rearContinuous)
                {
                    layerReverse=rearContinuous;channelStart=Math.Min(primaryStart,secondary.Start);channelEnd=Math.Max(primaryEnd,secondary.End);
                }
            }
            MeasuredCone? sink=null;bool? sinkReverse=null;
            if(profile.HoleKind==HoleKind.Countersink)
            {
                var target=Add(origin,Add(Scale(u,center.Xmm),Scale(v,center.Ymm)));
                var cones=inspection.Cones.Where(c=>c.Interior&&c.CompleteWall&&!string.IsNullOrWhiteSpace(c.PersistentReference)&&
                    Finite(c.AxisStartMm)&&Finite(c.AxisEndMm)&&Finite(c.Direction)&&Norm(c.Direction)>1e-12&&
                    Math.Abs(Dot(Unit(c.Direction),n))>.999&&LateralDistance(c.AxisStartMm,target,n)<=profile.PositionToleranceMm&&
                    Math.Abs(Math.Min(c.StartRadiusMm,c.EndRadiusMm)-minimumRadius)<=profile.SizeToleranceMm/2).ToArray();
                if(cones.Length==1)
                {
                    var c=cones[0];var small=c.StartRadiusMm<c.EndRadiusMm?c.AxisStartMm:c.AxisEndMm;
                    var large=c.StartRadiusMm<c.EndRadiusMm?c.AxisEndMm:c.AxisStartMm;
                    var smallStation=Dot(Sub(small,origin),n);var largeStation=Dot(Sub(large,origin),n);
                    var front=largeStation<primaryStart-profile.SizeToleranceMm/2&&Math.Abs(smallStation-primaryStart)<=profile.SizeToleranceMm;
                    var rear=largeStation>primaryEnd+profile.SizeToleranceMm/2&&Math.Abs(smallStation-primaryEnd)<=profile.SizeToleranceMm;
                    if(front^rear){sink=c;sinkReverse=rear;channelStart=Math.Min(primaryStart,largeStation);channelEnd=Math.Max(primaryEnd,largeStation);}
                }
            }
            MeasuredCosmeticThread? thread=null;bool? threadReverse=null;
            if(profile.HoleKind==HoleKind.Tapped)
            {
                var target=Add(origin,Add(Scale(u,center.Xmm),Scale(v,center.Ymm)));
                var threads=inspection.CosmeticThreads.Where(t=>t.Complete&&!string.IsNullOrWhiteSpace(t.EdgePersistentReference)&&
                    Finite(t.EntranceCenterMm)&&Finite(t.AxisDirection)&&Norm(t.AxisDirection)>1e-12&&
                    Math.Abs(Dot(Unit(t.AxisDirection),n))>.999&&LateralDistance(t.EntranceCenterMm,target,n)<=profile.PositionToleranceMm&&
                    Math.Abs(t.DrillRadiusMm-minimumRadius)<=profile.SizeToleranceMm/2&&
                    (Math.Abs(Dot(Sub(t.EntranceCenterMm,origin),n)-primaryStart)<=profile.SizeToleranceMm||
                     Math.Abs(Dot(Sub(t.EntranceCenterMm,origin),n)-primaryEnd)<=profile.SizeToleranceMm)).ToArray();
                if(threads.Length==1){thread=threads[0];threadReverse=Math.Abs(Dot(Sub(thread.EntranceCenterMm,origin),n)-primaryEnd)<=profile.SizeToleranceMm;}
            }
            var topology=MatchConnectivity(profile,connectivity,center,origin,u,v,n,channelStart,channelEnd);
            var through=DeriveThrough(topology);var reverse=DeriveBlindReverse(topology,n);
            result.Add(new()
            {
                Center=center,DiameterMm=primaryDiameter,ThroughAll=through,DepthMm=channelEnd-channelStart,ReverseDirection=reverse,
                CounterboreDiameterMm=secondary?.Cylinder.RadiusMm*2,CounterboreDepthMm=secondary is null?null:secondary.End-secondary.Start,
                CounterboreReverseDirection=layerReverse,
                CountersinkDiameterMm=sink is null?null:2*Math.Max(sink.StartRadiusMm,sink.EndRadiusMm),
                CountersinkAngleDegrees=sink?.IncludedAngleDegrees,CountersinkReverseDirection=sinkReverse,
                ThreadMajorDiameterMm=thread?.MajorDiameterMm,ThreadDesignation=thread?.Designation,
                ThreadThroughAll=thread?.ThroughAll,ThreadDepthMm=thread?.BlindDepthMm,ThreadReverseDirection=threadReverse,
                TopologyEvidenceId=topology is null?null:$"T08:{topology.RequirementId}:{topology.GeometryScopeFingerprint}",
                TopologyScopeFingerprint=topology?.GeometryScopeFingerprint,
                TopologySourceFactFingerprint=topology?.SourceFactFingerprint,
                TopologyModelSha256=topology?.ActualModelSha256
            });
        }
        return result;
    }

    private static ConnectivityCheck? MatchConnectivity(HoleGroupProfile profile,IReadOnlyList<ConnectivityCheck> checks,ProfilePoint center,
        Vector3 origin,Vector3 u,Vector3 v,Vector3 normal,double channelStart,double channelEnd)
    {
        var target=Add(origin,Add(Scale(u,center.Xmm),Scale(v,center.Ymm)));
        var expectedKind=profile.ThroughAll?ConnectivityKind.ThroughHole:ConnectivityKind.BlindHole;
        var matches=checks.Where(check=>check.Kind==expectedKind&&ConnectivityIntervalMatchesProfile(check,target,origin,normal,channelStart,channelEnd,
                profile.PositionToleranceMm,profile.SizeToleranceMm))
            .ToArray();
        if(matches.Length!=1)return null;
        var match=matches[0];
        if(checks.Count(other=>other.GeometryScopeFingerprint?.Equals(match.GeometryScopeFingerprint,StringComparison.OrdinalIgnoreCase)==true)!=1)return null;
        return match;
    }

    private static bool ConnectivityIntervalMatchesProfile(ConnectivityCheck check,Vector3 target,Vector3 profileOrigin,Vector3 profileNormal,
        double channelStart,double channelEnd,double positionTolerance,double sizeTolerance)
    {
        if(check.ObservedAxisOriginMm is not { } axisOrigin||check.ObservedAxis is not { } axis||check.ObservedAxialInterval is not { } interval||
           !Finite(axisOrigin)||!Finite(axis)||Norm(axis)<=1e-12)return false;
        var unit=Unit(axis);
        if(Math.Abs(Dot(unit,profileNormal))<=.999||LateralDistance(axisOrigin,target,profileNormal)>positionTolerance)return false;
        var worldStart=Add(axisOrigin,Scale(unit,interval.StartMm));
        var worldEnd=Add(axisOrigin,Scale(unit,interval.EndMm));
        var profileStart=Dot(Sub(worldStart,profileOrigin),profileNormal);
        var profileEnd=Dot(Sub(worldEnd,profileOrigin),profileNormal);
        return Math.Abs(Math.Min(profileStart,profileEnd)-channelStart)<=sizeTolerance&&
               Math.Abs(Math.Max(profileStart,profileEnd)-channelEnd)<=sizeTolerance;
    }

    private static NativePatternObservation? BuildPatternObservation(HoleGroupProfile profile,HoleGroupInspectionBinding binding,IReadOnlyList<ModelFeatureInfo> features,
        IReadOnlyList<NativeEditabilityProbeResult> editability,string? modelSha,bool rebuildSucceeded)
    {
        if(profile.PatternKind==HoleGroupPatternKind.None)return null;
        if(string.IsNullOrWhiteSpace(binding.PatternFeatureName))return null;
        var feature=features.SingleOrDefault(f=>f.Name.Equals(binding.PatternFeatureName,StringComparison.Ordinal));if(feature is null)return null;
        var (recognized,kind)=RecognizePattern(feature.Type);
        var count=Dimension(feature,binding.PatternCountDimensionName,"Unitless");
        var spacing=profile.PatternKind==HoleGroupPatternKind.Linear?Dimension(feature,binding.PatternSpacingDimensionName,"Millimeter"):0;
        var angle=profile.PatternKind==HoleGroupPatternKind.Circular?Dimension(feature,binding.PatternAngleDimensionName,"Degree"):0;
        var requiredDimensions=profile.PatternKind==HoleGroupPatternKind.Linear
            ?new[]{binding.PatternCountDimensionName,binding.PatternSpacingDimensionName}.OfType<string>().Where(x=>!string.IsNullOrWhiteSpace(x)).ToArray()
            :new[]{binding.PatternCountDimensionName,binding.PatternAngleDimensionName}.OfType<string>().Where(x=>!string.IsNullOrWhiteSpace(x)).ToArray();
        var editable=recognized&&!feature.Suppressed&&ProbesMatch(profile.PatternEditabilityProbes,editability,modelSha,feature.Name,requiredDimensions,feature);
        return new(){Kind=kind,NativeType=feature.Type,TypeRecognized=recognized,Suppressed=feature.Suppressed,
            Count=double.IsFinite(count)?(int)Math.Round(count):0,SpacingMm=spacing,AngleDegrees=angle,Editable=editable,RebuildSucceeded=rebuildSucceeded};
    }

    private static double Dimension(ModelFeatureInfo? feature,string? name,string expectedUnit)
    {
        if(feature is null||string.IsNullOrWhiteSpace(name))return double.NaN;
        var matches=feature.Dimensions.Where(d=>d.Name.Equals(name,StringComparison.Ordinal)).ToArray();
        return matches.Length==1&&matches[0].Unit.Equals(expectedUnit,StringComparison.Ordinal)&&matches[0].Value is { } value&&double.IsFinite(value)?value:double.NaN;
    }

    private static (bool Recognized,NativeFeatureKind Kind) RecognizePattern(string type)=>type switch
    {
        "LPattern" or "LPattern2"=>(true,NativeFeatureKind.LinearPattern),
        "CirPattern" or "CirPattern2"=>(true,NativeFeatureKind.CircularPattern),
        _=>(false,NativeFeatureKind.ReferencePlane)
    };

    private static (bool Recognized,EdgeTreatmentRoute Route) RecognizeEdgeTreatment(string? type)=>type switch
    {
        "Fillet" or "FilletXpert"=>(true,EdgeTreatmentRoute.SolidFillet),
        "Chamfer"=>(true,EdgeTreatmentRoute.SolidChamfer),
        "Sketch" or "ProfileFeature"=>(true,EdgeTreatmentRoute.ProfileArc),
        _=>(false,EdgeTreatmentRoute.ProfileArc)
    };

    private static bool ProbesMatch(IReadOnlyList<NativeEditabilityProbeSpec> expected,IReadOnlyList<NativeEditabilityProbeResult> actual,string? modelSha,
        string? requiredFeatureName,IReadOnlyList<string> requiredDimensionNames,ModelFeatureInfo? feature)
    {
        var required=requiredDimensionNames.Where(name=>!string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.Ordinal).ToArray();
        if(expected.Count==0||!Stage4EvidenceBinding.Hash(modelSha)||actual.Count!=expected.Count||string.IsNullOrWhiteSpace(requiredFeatureName)||feature is null||feature.Suppressed||
           !feature.Name.Equals(requiredFeatureName,StringComparison.Ordinal)||required.Length==0||expected.Count!=required.Length||
           !expected.Select(p=>p.DimensionName).ToHashSet(StringComparer.Ordinal).SetEquals(required)||
           required.Any(name=>feature.Dimensions.Count(d=>d.Name.Equals(name,StringComparison.Ordinal))!=1))return false;
        foreach(var probe in expected)
        {
            var matches=actual.Where(r=>r.ProbeId.Equals(probe.ProbeId,StringComparison.Ordinal)&&r.DimensionName.Equals(probe.DimensionName,StringComparison.Ordinal)).ToArray();
            if(matches.Length!=1)return false;
            var result=matches[0];var owner=probe.FeatureName??probe.DimensionName.Split('@').Skip(1).FirstOrDefault();
            if(!string.Equals(owner,requiredFeatureName,StringComparison.Ordinal)||!result.Passed||!string.Equals(result.ModelSha256,modelSha,StringComparison.OrdinalIgnoreCase)||result.Unit!=probe.Unit||
               result.RequestedTrialValue is not { } requested||!double.IsFinite(requested)||Math.Abs(requested-probe.TrialValue)>1e-10*Math.Max(1d,Math.Abs(probe.TrialValue))||
               !string.Equals(result.FeatureName,requiredFeatureName,StringComparison.Ordinal)||result.TrialSystemValue is not { } trial||result.TrialReadbackSystemValue is not { } trialRead||
               result.OriginalSystemValue is not { } original||result.RestoreReadbackSystemValue is not { } restored||
               Math.Abs(trial-trialRead)>1e-9*Math.Max(1d,Math.Abs(trial))||Math.Abs(original-restored)>1e-9*Math.Max(1d,Math.Abs(original))||
               !result.TrialRebuildSucceeded||!result.RestoreSucceeded||!result.RestoreRebuildSucceeded||!result.ModelFileUnchanged)return false;
        }
        return true;
    }

    private static IReadOnlyList<string> RequiredEdgeTreatmentDimensions(EdgeTreatmentIntent intent,EdgeTreatmentInspectionBinding binding)
    {
        var dimensions=new List<string>();
        if(!string.IsNullOrWhiteSpace(binding.PrimaryDimensionName))dimensions.Add(binding.PrimaryDimensionName);
        if(intent.Route==EdgeTreatmentRoute.SolidChamfer&&intent.ChamferMode is ChamferMode.DistanceAngle or ChamferMode.TwoDistances&&
           !string.IsNullOrWhiteSpace(binding.SecondaryDimensionName))dimensions.Add(binding.SecondaryDimensionName!);
        return dimensions;
    }

    private static bool RevolvedAxialScopeMatches(RevolvedFamilyProfile profile,IReadOnlyList<ConnectivityCheck> checks,string? modelSha)
    {
        if(profile.Steps.Count==0||!Finite(profile.AxisOriginMm)||!Finite(profile.AxisDirection)||Norm(profile.AxisDirection)<=1e-12)return false;
        var matches=checks.Where(c=>c.RequirementId==profile.AxialHoleConnectivityCheckId).ToArray();
        if(matches.Length!=1)return false;
        var check=matches[0];
        return check.Status==ConnectivityStatus.Passed&&check.Kind==ConnectivityKind.ThroughHole&&check.ModelReopened&&check.ObservationComplete&&
            string.Equals(check.ActualModelSha256,modelSha,StringComparison.OrdinalIgnoreCase)&&Stage4EvidenceBinding.Hash(check.GeometryScopeFingerprint)&&
            ConnectivityIntervalMatchesProfile(check,profile.AxisOriginMm,profile.AxisOriginMm,Unit(profile.AxisDirection),
                profile.Steps.Min(s=>s.AxialStartMm),profile.Steps.Max(s=>s.AxialStartMm+s.AxialLengthMm),profile.AxisToleranceMm,profile.Steps.Min(s=>s.ToleranceMm));
    }

    private static bool RevolvedProbesMatch(RevolvedFamilyProfile profile,RevolvedFamilyInspectionBinding binding,ModelInspection inspection,ModelFeatureInfo? feature)
    {
        var required=RequiredRevolvedDimensions(profile,binding,feature,inspection.Features??[]);
        if(required.Count==0||profile.EditabilityProbes.Count!=required.Count||inspection.EditabilityProbes.Count!=required.Count||
            !profile.EditabilityProbes.Select(p=>p.DimensionName).ToHashSet(StringComparer.Ordinal).SetEquals(required))return false;
        foreach(var group in binding.DrivingDimensions.GroupBy(d=>d.OwnerFeatureName??binding.FeatureName,StringComparer.Ordinal))
        {
            var dimensions=group.Select(d=>d.DimensionName).ToArray();
            var owner=inspection.Features?.SingleOrDefault(f=>f.Name==group.Key);
            if(!ProbesMatch(profile.EditabilityProbes.Where(p=>dimensions.Contains(p.DimensionName)).ToArray(),
                inspection.EditabilityProbes.Where(p=>dimensions.Contains(p.DimensionName)).ToArray(),inspection.ModelSha256,group.Key,dimensions,owner))return false;
        }
        return true;
    }

    private static IReadOnlyList<string> RequiredRevolvedDimensions(RevolvedFamilyProfile profile,RevolvedFamilyInspectionBinding binding,ModelFeatureInfo? feature,IReadOnlyList<ModelFeatureInfo> features)
    {
        if(string.IsNullOrWhiteSpace(binding.FeatureName)||feature is null||feature.Suppressed||!feature.Name.Equals(binding.FeatureName,StringComparison.Ordinal)||
           binding.DrivingDimensions.Count!=profile.Steps.Count*2)return[];
        var sourceFacts=profile.Steps.Select(step=>step.SourceFactId).ToHashSet(StringComparer.Ordinal);
        if(binding.DrivingDimensions.Any(item=>!sourceFacts.Contains(item.SourceFactId)||string.IsNullOrWhiteSpace(item.DimensionName))||
           binding.DrivingDimensions.Select(item=>item.DimensionName).Distinct(StringComparer.Ordinal).Count()!=binding.DrivingDimensions.Count)return[];
        foreach(var step in profile.Steps)
        {
            foreach(var parameter in new[]{RevolvedDrivingParameterKind.Diameter,RevolvedDrivingParameterKind.AxialLength})
            {
                var mappings=binding.DrivingDimensions.Where(item=>item.SourceFactId.Equals(step.SourceFactId,StringComparison.Ordinal)&&item.Parameter==parameter).ToArray();
                if(mappings.Length!=1)return[];
                var mapping=mappings[0];
                if(!Enum.IsDefined(mapping.NativeRadialSemantic))return[];
                var ownerName=mapping.OwnerFeatureName??binding.FeatureName;
                var owner=features.SingleOrDefault(f=>f.Name==ownerName);
                if(owner is null||owner.Suppressed)return[];
                if(ownerName!=binding.FeatureName&&(!feature.ParentFeatureNames.Contains(ownerName,StringComparer.Ordinal)||
                    owner.Type is not ("ProfileFeature" or "Sketch")))return[];
                var native=owner.Dimensions.Where(item=>item.Name.Equals(mapping.DimensionName,StringComparison.Ordinal)).ToArray();
                var expected=parameter==RevolvedDrivingParameterKind.Diameter?step.ExpectedDiameterMm:step.AxialLengthMm;
                var multiplier=parameter==RevolvedDrivingParameterKind.Diameter&&mapping.NativeRadialSemantic==RadialDimensionSemantic.Radius?2d:1d;
                if(native.Length!=1||!native[0].Unit.Equals("Millimeter",StringComparison.Ordinal)||native[0].Value is not { } actual||!double.IsFinite(actual)||
                   Math.Abs(actual*multiplier-expected)>step.ToleranceMm)return[];
            }
        }
        return binding.DrivingDimensions.Select(item=>item.DimensionName).ToArray();
    }

    private static double DefaultHoleScopePadding(HoleGroupProfile profile)
    {
        if(profile.PatternKind==HoleGroupPatternKind.Linear&&double.IsFinite(profile.PatternSpacingMm)&&profile.PatternSpacingMm>0)
            return profile.PatternSpacingMm+profile.PositionToleranceMm;
        if(profile.ExpectedCenters.Count>1)
        {
            var nearest=profile.ExpectedCenters.Select((p,i)=>profile.ExpectedCenters.Where((_,j)=>j!=i).Select(q=>Distance(p,q)).DefaultIfEmpty(0).Min()).Max();
            if(double.IsFinite(nearest)&&nearest>0)return nearest+profile.PositionToleranceMm;
        }
        return Math.Max(profile.DiameterMm,profile.PositionToleranceMm*2);
    }

    private static bool? DeriveThrough(ConnectivityCheck? check)
    {
        if(check is not {Status:ConnectivityStatus.Passed,ModelReopened:true,ObservationComplete:true})return null;
        if(check.Kind==ConnectivityKind.ThroughHole)return true;
        if(check.Kind==ConnectivityKind.BlindHole)return false;
        var start=Opening(check.StartEvidence);var end=Opening(check.EndEvidence);
        if(start==true&&end==true)return true;
        if(start is not null&&end is not null&&start!=end)return false;
        return null;
    }

    private static bool? DeriveBlindReverse(ConnectivityCheck? check,Vector3 frameNormal)
    {
        if(DeriveThrough(check)!=false||check?.ObservedAxis is not { } axis||!Finite(axis))return null;
        var start=Opening(check.StartEvidence);var end=Opening(check.EndEvidence);var dot=Dot(Unit(axis),frameNormal);
        if(Math.Abs(dot)<.999)return null;
        if(start==true&&end==false)return dot<0;
        if(end==true&&start==false)return dot>0;
        return null;
    }

    private static bool? Opening(AxialOpeningEvidence? evidence)
    {
        if(evidence is not {Complete:true}||evidence.SampleCount<3||evidence.MaterialSampleCount<0||evidence.VoidSampleCount<0||
           evidence.MaterialSampleCount+evidence.VoidSampleCount!=evidence.SampleCount)return null;
        if(evidence.MaterialSampleCount==0&&evidence.VoidSampleCount==evidence.SampleCount)return true;
        if(evidence.MaterialSampleCount==evidence.SampleCount&&evidence.MaterialThicknessBeyondMm is { } t&&double.IsFinite(t)&&t>evidence.NumericalUncertaintyMm)return false;
        return null;
    }

    private static IReadOnlyList<EdgeTreatmentRepairStrategy> InferRepairStrategies(RepairAttempt attempt)=>attempt.Kind switch
    {
        RepairKind.FilletSelection or RepairKind.GeometryRefRebind=>[EdgeTreatmentRepairStrategy.ReselectEdges],
        _=>[]
    };
    private static Stage4VerificationResult AppendFailure(Stage4VerificationResult result,string id,string message)=>new([..result.Checks,new(id,RequirementCheckStatus.Unverifiable,message)]);
    private static string Sha(string value)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static bool Finite(Vector3 v)=>double.IsFinite(v.X)&&double.IsFinite(v.Y)&&double.IsFinite(v.Z);
    private static double Dot(Vector3 a,Vector3 b)=>a.X*b.X+a.Y*b.Y+a.Z*b.Z;
    private static double Norm(Vector3 v)=>Math.Sqrt(Dot(v,v));
    private static Vector3 Unit(Vector3 v){var n=Norm(v);return n>1e-12?new(v.X/n,v.Y/n,v.Z/n):new(double.NaN,double.NaN,double.NaN);}
    private static Vector3 Add(Vector3 a,Vector3 b)=>new(a.X+b.X,a.Y+b.Y,a.Z+b.Z);
    private static Vector3 Sub(Vector3 a,Vector3 b)=>new(a.X-b.X,a.Y-b.Y,a.Z-b.Z);
    private static Vector3 Scale(Vector3 v,double s)=>new(v.X*s,v.Y*s,v.Z*s);
    private static double Distance(ProfilePoint a,ProfilePoint b)=>Math.Sqrt(Math.Pow(a.Xmm-b.Xmm,2)+Math.Pow(a.Ymm-b.Ymm,2));
    private static double LateralDistance(Vector3 p,Vector3 origin,Vector3 axis){var d=Sub(p,origin);return Norm(Sub(d,Scale(axis,Dot(d,axis))));}
    private static (Vector3 U,Vector3 V,Vector3 N) Basis(SketchFrame frame){var n=Unit(frame.Normal);var x=frame.XDirection;var u=Unit(Sub(x,Scale(n,Dot(x,n))));return(u,new(n.Y*u.Z-n.Z*u.Y,n.Z*u.X-n.X*u.Z,n.X*u.Y-n.Y*u.X),n);}
    private static ProfilePoint ToLocal(Vector3 p,Vector3 origin,Vector3 u,Vector3 v){var d=Sub(p,origin);return new(Dot(d,u),Dot(d,v));}
}
