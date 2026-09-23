using CadModeling.Ir;

namespace CadModeling.Core;

public sealed record Stage4Check(string Id,RequirementCheckStatus Status,string Message);
public sealed record Stage4VerificationResult(IReadOnlyList<Stage4Check> Checks)
{
    public bool Passed=>Checks.Count>0&&Checks.All(c=>c.Status==RequirementCheckStatus.Passed);
}

public sealed record Stage4BoundCheckEvidence
{
    public required string CheckId { get; init; }
    public RequirementCheckStatus Status { get; init; }
    public required string EvidenceId { get; init; }
    public required string SourceSha256 { get; init; }
    public required string SourceRevisionId { get; init; }
    public required string ModelSha256 { get; init; }
    public required string RequiredScopeFingerprint { get; init; }
    public required string RequirementFingerprint { get; init; }
    public bool ModelReopened { get; init; }
    public bool CaptureComplete { get; init; }
    public bool ProducerScopePassed { get; init; }=true;
}

public enum RadialDimensionSemantic { Diameter, Radius }
public enum RevolvedFamilyKind { SteppedShaft, SteppedShaftWithAxialHole }

public sealed record RevolvedStepRequirement
{
    public required string SourceFactId { get; init; }
    public double AxialStartMm { get; init; }
    public double AxialLengthMm { get; init; }
    public double RadialValueMm { get; init; }
    public RadialDimensionSemantic RadialSemantic { get; init; }=RadialDimensionSemantic.Diameter;
    public double ToleranceMm { get; init; }=0.05;
    public double ExpectedDiameterMm=>RadialSemantic==RadialDimensionSemantic.Diameter?RadialValueMm:RadialValueMm*2;
}

public sealed record RevolvedFamilyProfile
{
    public const string ContractVersion="autosolidworks.revolved-family/v1";
    public string Contract { get; init; }=ContractVersion;
    public required string ProfileId { get; init; }
    public required string SourceSha256 { get; init; }
    public required string SourceRevisionId { get; init; }
    public required GeometryRef AxisReference { get; init; }
    public required string RequiredScopeFingerprint { get; init; }
    public required IReadOnlyDictionary<string,string> CheckRequirementFingerprints { get; init; }
    public IReadOnlyList<NativeEditabilityProbeSpec> EditabilityProbes { get; init; }=[];
    public Vector3 AxisOriginMm { get; init; }=new(0,0,0);
    public Vector3 AxisDirection { get; init; }=new(1,0,0);
    public RevolvedFamilyKind Kind { get; init; }
    public required IReadOnlyList<RevolvedStepRequirement> Steps { get; init; }
    public IReadOnlyList<string> RequiredViewCheckIds { get; init; }=[];
    public string? AxialHoleConnectivityCheckId { get; init; }
    public double AxisToleranceMm { get; init; }=0.05;
    public double DirectionToleranceDegrees { get; init; }=0.1;
}

public sealed record RevolvedStepMeasurement(double AxialStartMm,double AxialLengthMm,double DiameterMm);

public sealed record RevolvedFamilyObservation
{
    public required string SourceSha256 { get; init; }
    public required string SourceRevisionId { get; init; }
    public required string ModelSha256 { get; init; }
    public bool ModelReopened { get; init; }
    public NativeFeatureKind NativeFeatureKind { get; init; }=NativeFeatureKind.RevolveBoss;
    public bool NativeDrivingDimensionsEditable { get; init; }
    public bool RebuildSucceeded { get; init; }
    public Vector3 AxisOriginMm { get; init; }=new(0,0,0);
    public Vector3 AxisDirection { get; init; }=new(1,0,0);
    public required IReadOnlyList<RevolvedStepMeasurement> Steps { get; init; }
    public IReadOnlyList<Stage4BoundCheckEvidence> CheckEvidence { get; init; }=[];
}

public static class RevolvedFamilyVerifier
{
    public static Stage4VerificationResult Evaluate(RevolvedFamilyProfile profile,RevolvedFamilyObservation actual)
    {
        ArgumentNullException.ThrowIfNull(profile);ArgumentNullException.ThrowIfNull(actual);
        var checks=new List<Stage4Check>();
        var requiredCheckIds=profile.RequiredViewCheckIds
            .Concat(profile.Kind==RevolvedFamilyKind.SteppedShaftWithAxialHole&&profile.AxialHoleConnectivityCheckId is {Length:>0} axial?[axial]:[])
            .ToArray();
        var profileValid=profile.Contract==RevolvedFamilyProfile.ContractVersion&&Stage4EvidenceBinding.Hash(profile.SourceSha256)&&Stage4EvidenceBinding.Hash(profile.RequiredScopeFingerprint)&&
            !string.IsNullOrWhiteSpace(profile.ProfileId)&&!string.IsNullOrWhiteSpace(profile.SourceRevisionId)&&profile.Steps.Count>0&&
            profile.Steps.Select(s=>s.SourceFactId).Distinct(StringComparer.Ordinal).Count()==profile.Steps.Count&&profile.Steps.All(s=>
                !string.IsNullOrWhiteSpace(s.SourceFactId)&&Finite(s.AxialStartMm,s.AxialLengthMm,s.RadialValueMm,s.ToleranceMm)&&
                s.AxialLengthMm>0&&s.RadialValueMm>0&&s.ToleranceMm>0)&&
            profile.RequiredViewCheckIds.Count>0&&requiredCheckIds.Distinct(StringComparer.Ordinal).Count()==requiredCheckIds.Length&&
            (profile.Kind!=RevolvedFamilyKind.SteppedShaftWithAxialHole||!string.IsNullOrWhiteSpace(profile.AxialHoleConnectivityCheckId))&&
            profile.CheckRequirementFingerprints.Count==requiredCheckIds.Length&&requiredCheckIds.All(id=>profile.CheckRequirementFingerprints.TryGetValue(id,out var fp)&&Stage4EvidenceBinding.Hash(fp))&&
            profile.EditabilityProbes.Count>0&&profile.EditabilityProbes.Select(p=>p.ProbeId).Distinct(StringComparer.Ordinal).Count()==profile.EditabilityProbes.Count&&
            profile.EditabilityProbes.All(p=>!string.IsNullOrWhiteSpace(p.ProbeId)&&!string.IsNullOrWhiteSpace(p.DimensionName)&&double.IsFinite(p.TrialValue))&&
            Finite(profile.AxisToleranceMm,profile.DirectionToleranceDegrees)&&profile.AxisToleranceMm>0&&profile.DirectionToleranceDegrees>0&&profile.DirectionToleranceDegrees<90;
        Add("profile-contract",profileValid,"Finite nonempty family requirements and frozen T08/T09 requirement fingerprints are mandatory.");
        Add("identity",profileValid&&Stage4EvidenceBinding.Hash(actual.ModelSha256)&&
            profile.SourceSha256.Equals(actual.SourceSha256,StringComparison.OrdinalIgnoreCase)&&
            profile.SourceRevisionId.Equals(actual.SourceRevisionId,StringComparison.Ordinal)&&actual.ModelReopened&&
            profile.AxisReference.SourceRevisionId==profile.SourceRevisionId&&profile.AxisReference.SourceFactIds.Count>0,
            "Source revision/SHA and reopened native model must match the family contract.");
        var axisValid=Finite(profile.AxisOriginMm)&&Finite(profile.AxisDirection)&&Norm(profile.AxisDirection)>1e-12&&
            Finite(actual.AxisOriginMm)&&Finite(actual.AxisDirection)&&Norm(actual.AxisDirection)>1e-12;
        if(axisValid)
        {
            var a=Unit(profile.AxisDirection);var b=Unit(actual.AxisDirection);
            var directionError=Math.Acos(Math.Clamp(Math.Abs(Dot(a,b)),-1,1))*180/Math.PI;
            var offset=Norm(Cross(Sub(actual.AxisOriginMm,profile.AxisOriginMm),a));
            Add("axis",directionError<=profile.DirectionToleranceDegrees&&offset<=profile.AxisToleranceMm,
                $"Axis direction error={directionError:R} deg, offset={offset:R} mm.");
        }
        else Add("axis",false,"Axis vectors/origins must be finite and nonzero.");
        Add("native-revolve",actual.NativeFeatureKind==NativeFeatureKind.RevolveBoss&&actual.NativeDrivingDimensionsEditable&&actual.RebuildSucceeded,
            "Supported family requires an editable native revolve boss that rebuilds after a driving-dimension edit.");
        Add("step-count",actual.Steps.Count==profile.Steps.Count,$"Expected {profile.Steps.Count} revolved steps, measured {actual.Steps.Count}.");
        if(actual.Steps.Count==profile.Steps.Count)
        {
            var expected=profile.Steps.OrderBy(s=>s.AxialStartMm).ToArray();
            var measured=actual.Steps.OrderBy(s=>s.AxialStartMm).ToArray();
            for(var i=0;i<expected.Length;i++)
            {
                var e=expected[i];var m=measured[i];
                var ok=Finite(e.AxialStartMm,e.AxialLengthMm,e.RadialValueMm,e.ToleranceMm,m.AxialStartMm,m.AxialLengthMm,m.DiameterMm)&&
                    e.AxialLengthMm>0&&e.RadialValueMm>0&&e.ToleranceMm>0&&m.DiameterMm>0&&
                    Math.Abs(e.AxialStartMm-m.AxialStartMm)<=e.ToleranceMm&&Math.Abs(e.AxialLengthMm-m.AxialLengthMm)<=e.ToleranceMm&&
                    Math.Abs(e.ExpectedDiameterMm-m.DiameterMm)<=e.ToleranceMm;
                Add("step:"+e.SourceFactId,ok,$"Expected start/length/diameter {e.AxialStartMm:R}/{e.AxialLengthMm:R}/{e.ExpectedDiameterMm:R} mm; measured {m.AxialStartMm:R}/{m.AxialLengthMm:R}/{m.DiameterMm:R} mm.");
            }
        }
        foreach(var id in requiredCheckIds)
            Add((id==profile.AxialHoleConnectivityCheckId?"axial-hole:":"view:")+id,
                Stage4EvidenceBinding.Passes(id,profile.SourceSha256,profile.SourceRevisionId,actual.ModelSha256,profile.RequiredScopeFingerprint,
                    profile.CheckRequirementFingerprints.GetValueOrDefault(id),actual.CheckEvidence),
                "Required T08/T09 evidence must be uniquely bound to this source revision, reopened model, frozen scope and requirement fingerprint.");
        return new(checks);
        void Add(string id,bool ok,string message)=>checks.Add(new(id,ok?RequirementCheckStatus.Passed:RequirementCheckStatus.Failed,message));
    }

    private static bool Finite(Vector3 v)=>Finite(v.X,v.Y,v.Z);
    private static bool Finite(params double[] v)=>v.All(double.IsFinite);
    private static double Norm(Vector3 v)=>Math.Sqrt(Dot(v,v));
    private static Vector3 Unit(Vector3 v){var n=Norm(v);return new(v.X/n,v.Y/n,v.Z/n);}
    private static double Dot(Vector3 a,Vector3 b)=>a.X*b.X+a.Y*b.Y+a.Z*b.Z;
    private static Vector3 Sub(Vector3 a,Vector3 b)=>new(a.X-b.X,a.Y-b.Y,a.Z-b.Z);
    private static Vector3 Cross(Vector3 a,Vector3 b)=>new(a.Y*b.Z-a.Z*b.Y,a.Z*b.X-a.X*b.Z,a.X*b.Y-a.Y*b.X);
}

public enum HoleGroupPatternKind { None, Linear, Circular }

public sealed record HoleGroupProfile
{
    public const string ContractVersion="autosolidworks.hole-group/v1";
    public string Contract { get; init; }=ContractVersion;
    public required string GroupId { get; init; }
    public required string SourceSha256 { get; init; }
    public required string SourceRevisionId { get; init; }
    public required string SourceFactId { get; init; }
    public required string SourceFactFingerprint { get; init; }
    public required string RequiredScopeFingerprint { get; init; }
    public required IReadOnlyDictionary<string,string> CheckRequirementFingerprints { get; init; }
    /// <summary>Model-space entrance-plane frame; ExpectedCenters use this local X/Y basis.</summary>
    public SketchFrame Frame { get; init; }=new();
    public IReadOnlyList<NativeEditabilityProbeSpec> PatternEditabilityProbes { get; init; }=[];
    public HoleKind HoleKind { get; init; }
    public HoleGroupPatternKind PatternKind { get; init; }
    public required IReadOnlyList<ProfilePoint> ExpectedCenters { get; init; }
    public double DiameterMm { get; init; }
    public bool ThroughAll { get; init; }=true;
    public double DepthMm { get; init; }
    public bool ReverseDirection { get; init; }
    public double CounterboreDiameterMm { get; init; }
    public double CounterboreDepthMm { get; init; }
    public double CountersinkDiameterMm { get; init; }
    public double CountersinkAngleDegrees { get; init; }=90;
    public double ThreadMajorDiameterMm { get; init; }
    public string? ThreadDesignation { get; init; }
    public double PatternSpacingMm { get; init; }
    public double PatternAngleDegrees { get; init; }
    public double PositionToleranceMm { get; init; }=0.05;
    public double SizeToleranceMm { get; init; }=0.05;
    public string? ConnectivityCheckId { get; init; }
    public string? ProjectionCheckId { get; init; }
}

public sealed record HoleInstanceMeasurement
{
    public required ProfilePoint Center { get; init; }
    public double DiameterMm { get; init; }
    public bool? ThroughAll { get; init; }
    public double DepthMm { get; init; }
    public bool? ReverseDirection { get; init; }
    public double? CounterboreDiameterMm { get; init; }
    public double? CounterboreDepthMm { get; init; }
    public bool? CounterboreReverseDirection { get; init; }
    public double? CountersinkDiameterMm { get; init; }
    public double? CountersinkAngleDegrees { get; init; }
    public bool? CountersinkReverseDirection { get; init; }
    public double? ThreadMajorDiameterMm { get; init; }
    public string? ThreadDesignation { get; init; }
    public string? TopologyEvidenceId { get; init; }
    public bool? ThreadThroughAll { get; init; }
    public double? ThreadDepthMm { get; init; }
    public bool? ThreadReverseDirection { get; init; }
    public string? TopologyScopeFingerprint { get; init; }
    public string? TopologySourceFactFingerprint { get; init; }
    public string? TopologyModelSha256 { get; init; }
}

public sealed record NativePatternObservation
{
    public NativeFeatureKind Kind { get; init; }
    public string NativeType { get; init; }=string.Empty;
    public bool TypeRecognized { get; init; }=true;
    public bool Suppressed { get; init; }
    public int Count { get; init; }
    public double SpacingMm { get; init; }
    public double AngleDegrees { get; init; }
    public bool Editable { get; init; }
    public bool RebuildSucceeded { get; init; }
}

public sealed record HoleGroupObservation
{
    public required string SourceSha256 { get; init; }
    public required string SourceRevisionId { get; init; }
    public required string ModelSha256 { get; init; }
    public bool ModelReopened { get; init; }
    public required IReadOnlyList<HoleInstanceMeasurement> Instances { get; init; }
    public NativePatternObservation? Pattern { get; init; }
    public IReadOnlyList<Stage4BoundCheckEvidence> CheckEvidence { get; init; }=[];
}

public static class HoleGroupVerifier
{
    public static Stage4VerificationResult Evaluate(HoleGroupProfile profile,HoleGroupObservation actual)
    {
        ArgumentNullException.ThrowIfNull(profile);ArgumentNullException.ThrowIfNull(actual);
        var checks=new List<Stage4Check>();
        var projectionCheckValid=profile.ProjectionCheckId is {Length:>0} projectionId&&
            profile.CheckRequirementFingerprints.TryGetValue(projectionId,out var projectionFingerprint)&&Stage4EvidenceBinding.Hash(projectionFingerprint);
        var profileChecksValid=projectionCheckValid&&Stage4EvidenceBinding.Hash(profile.SourceFactFingerprint);
        var editabilityScopeValid=profile.PatternKind==HoleGroupPatternKind.None||profile.PatternEditabilityProbes.Count>0&&
            profile.PatternEditabilityProbes.Select(p=>p.ProbeId).Distinct(StringComparer.Ordinal).Count()==profile.PatternEditabilityProbes.Count&&
            profile.PatternEditabilityProbes.All(p=>!string.IsNullOrWhiteSpace(p.ProbeId)&&!string.IsNullOrWhiteSpace(p.DimensionName)&&double.IsFinite(p.TrialValue));
        Add("identity",profile.Contract==HoleGroupProfile.ContractVersion&&Stage4EvidenceBinding.Hash(profile.SourceSha256)&&Stage4EvidenceBinding.Hash(profile.RequiredScopeFingerprint)&&
            Stage4EvidenceBinding.Hash(actual.ModelSha256)&&profileChecksValid&&editabilityScopeValid&&profile.SourceSha256.Equals(actual.SourceSha256,StringComparison.OrdinalIgnoreCase)&&
            profile.SourceRevisionId.Equals(actual.SourceRevisionId,StringComparison.Ordinal)&&actual.ModelReopened,
            "Source identity and reopened saved model must match.");
        var validProfile=profile.ExpectedCenters.Count>0&&profile.ExpectedCenters.All(p=>double.IsFinite(p.Xmm)&&double.IsFinite(p.Ymm))&&
            Positive(profile.DiameterMm)&&Positive(profile.PositionToleranceMm)&&Positive(profile.SizeToleranceMm)&&
            (profile.ThroughAll||Positive(profile.DepthMm))&&(profile.HoleKind switch
            {
                HoleKind.Simple=>true,
                HoleKind.Counterbore=>Positive(profile.CounterboreDiameterMm)&&profile.CounterboreDiameterMm>profile.DiameterMm&&Positive(profile.CounterboreDepthMm),
                HoleKind.Countersink=>Positive(profile.CountersinkDiameterMm)&&profile.CountersinkDiameterMm>profile.DiameterMm&&Positive(profile.CountersinkAngleDegrees)&&profile.CountersinkAngleDegrees<180,
                HoleKind.Tapped=>Positive(profile.ThreadMajorDiameterMm)&&profile.ThreadMajorDiameterMm>profile.DiameterMm&&!string.IsNullOrWhiteSpace(profile.ThreadDesignation),
                _=>false
            });
        Add("profile",validProfile,"Hole-group dimensions, centers and tolerances must be explicit and finite.");
        Add("count",actual.Instances.Count==profile.ExpectedCenters.Count,$"Expected {profile.ExpectedCenters.Count} holes, measured {actual.Instances.Count}.");
        var used=new HashSet<int>();var usedTopologyEvidence=new HashSet<string>(StringComparer.Ordinal);var usedTopologyScopes=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for(var i=0;i<profile.ExpectedCenters.Count;i++)
        {
            var e=profile.ExpectedCenters[i];
            var candidates=actual.Instances.Select((m,index)=>(m,index,d:Distance(e,m.Center))).Where(x=>x.d<=profile.PositionToleranceMm&&!used.Contains(x.index)).OrderBy(x=>x.d).ToArray();
            if(candidates.Length!=1){Add($"instance:{i}",false,$"Expected center ({e.Xmm:R},{e.Ymm:R}) mm matched {candidates.Length} unused actual instances.");continue;}
            var (m,index,_)=candidates[0];used.Add(index);
            var topologyEvidenceId=m.TopologyEvidenceId;var topologyScope=m.TopologyScopeFingerprint;
            var topologyFact=m.TopologySourceFactFingerprint;var topologyModel=m.TopologyModelSha256;
            var topologyBound=!string.IsNullOrWhiteSpace(topologyEvidenceId)&&topologyScope is not null&&topologyFact is not null&&topologyModel is not null&&
                Stage4EvidenceBinding.Hash(topologyScope)&&Stage4EvidenceBinding.Hash(topologyFact)&&Stage4EvidenceBinding.Hash(topologyModel)&&
                topologyFact.Equals(profile.SourceFactFingerprint,StringComparison.OrdinalIgnoreCase)&&
                topologyModel.Equals(actual.ModelSha256,StringComparison.OrdinalIgnoreCase)&&
                usedTopologyEvidence.Add(topologyEvidenceId)&&usedTopologyScopes.Add(topologyScope);
            var shape=double.IsFinite(m.DiameterMm)&&Math.Abs(m.DiameterMm-profile.DiameterMm)<=profile.SizeToleranceMm&&m.ThroughAll==profile.ThroughAll&&
                (profile.ThroughAll||double.IsFinite(m.DepthMm)&&Math.Abs(m.DepthMm-profile.DepthMm)<=profile.SizeToleranceMm)&&
                (profile.ThroughAll||m.ReverseDirection==profile.ReverseDirection)&&topologyBound;
            if(profile.HoleKind==HoleKind.Counterbore)
                shape&=m.CounterboreDiameterMm is {} cbd&&m.CounterboreDepthMm is {} cbz&&m.CounterboreReverseDirection==profile.ReverseDirection&&
                    Math.Abs(cbd-profile.CounterboreDiameterMm)<=profile.SizeToleranceMm&&Math.Abs(cbz-profile.CounterboreDepthMm)<=profile.SizeToleranceMm;
            if(profile.HoleKind==HoleKind.Countersink)
                shape&=m.CountersinkDiameterMm is {} csd&&m.CountersinkAngleDegrees is {} csa&&m.CountersinkReverseDirection==profile.ReverseDirection&&
                    Math.Abs(csd-profile.CountersinkDiameterMm)<=profile.SizeToleranceMm&&Math.Abs(csa-profile.CountersinkAngleDegrees)<=0.1;
            if(profile.HoleKind==HoleKind.Tapped)
                shape&=m.ThreadMajorDiameterMm is {} major&&Math.Abs(major-profile.ThreadMajorDiameterMm)<=profile.SizeToleranceMm&&
                    m.ThreadThroughAll==profile.ThroughAll&&m.ThreadReverseDirection==profile.ReverseDirection&&
                    (profile.ThroughAll||m.ThreadDepthMm is {} threadDepth&&double.IsFinite(threadDepth)&&Math.Abs(threadDepth-profile.DepthMm)<=profile.SizeToleranceMm)&&
                    !string.IsNullOrWhiteSpace(profile.ThreadDesignation)&&string.Equals(m.ThreadDesignation,profile.ThreadDesignation,StringComparison.Ordinal);
            Add($"instance:{i}",shape,$"Measured hole at ({m.Center.Xmm:R},{m.Center.Ymm:R}); diameter/depth/direction/layers are checked per instance.");
        }
        if(actual.Instances.Count!=used.Count)Add("unmatched-actual",false,"One or more actual holes are not bijectively matched to source instances.");
        if(profile.PatternKind!=HoleGroupPatternKind.None)
        {
            var p=actual.Pattern;var expectedKind=profile.PatternKind==HoleGroupPatternKind.Linear?NativeFeatureKind.LinearPattern:NativeFeatureKind.CircularPattern;
            var ok=p is not null&&p.TypeRecognized&&!p.Suppressed&&p.Kind==expectedKind&&p.Count==profile.ExpectedCenters.Count&&p.Editable&&p.RebuildSucceeded&&
                (profile.PatternKind==HoleGroupPatternKind.Linear?Math.Abs(p.SpacingMm-profile.PatternSpacingMm)<=profile.SizeToleranceMm:
                    Math.Abs(p.AngleDegrees-profile.PatternAngleDegrees)<=0.1);
            Add("native-pattern",ok,"A supported array must be a recognized, unsuppressed native editable pattern with the declared count/spacing or angular span; unrelated/unknown features do not qualify.");
        }
        Add("required-check-scope",profileChecksValid,"Hole-group acceptance requires producer-owned per-instance T08 topology scope plus one frozen T09 projection check.");
        if(profile.ProjectionCheckId is { } id)
            Add("check:"+id,Stage4EvidenceBinding.Passes(id,profile.SourceSha256,profile.SourceRevisionId,actual.ModelSha256,profile.RequiredScopeFingerprint,
                    profile.CheckRequirementFingerprints.GetValueOrDefault(id),actual.CheckEvidence),
                "Declared T09 evidence must be uniquely bound to this source revision, reopened model, frozen scope and requirement fingerprint.");
        return new(checks);
        void Add(string id,bool ok,string message)=>checks.Add(new(id,ok?RequirementCheckStatus.Passed:RequirementCheckStatus.Failed,message));
    }

    private static bool Positive(double x)=>double.IsFinite(x)&&x>0;
    private static double Distance(ProfilePoint a,ProfilePoint b)=>Math.Sqrt(Math.Pow(a.Xmm-b.Xmm,2)+Math.Pow(a.Ymm-b.Ymm,2));
}

public enum EdgeTreatmentRoute { ProfileArc, SolidFillet, SolidChamfer }
public enum EdgeTreatmentRepairStrategy { ReselectEdges, SplitEdgeGroup, ReorderFeature }

public sealed record EdgeTreatmentIntent
{
    public const string ContractVersion="autosolidworks.edge-treatment/v1";
    public string Contract { get; init; }=ContractVersion;
    public required string IntentId { get; init; }
    public required string SourceLiteral { get; init; }
    public required string SourceSha256 { get; init; }
    public required string SourceRevisionId { get; init; }
    public EdgeTreatmentRoute Route { get; init; }
    public double RadiusMm { get; init; }
    public double DistanceMm { get; init; }
    public double SecondDistanceMm { get; init; }
    public double AngleDegrees { get; init; }
    public ChamferMode ChamferMode { get; init; }=ChamferMode.DistanceAngle;
    public IReadOnlyList<GeometryRef> TargetEdges { get; init; }=[];
    public IReadOnlyList<string> DependsOnOperationIds { get; init; }=[];
    public string? TargetOperationId { get; init; }
    // IReadOnlyCollection retains membership semantics and accepts HashSet callers,
    // while System.Text.Json can instantiate it when a public MCP payload is read.
    public IReadOnlyCollection<EdgeTreatmentRepairStrategy> AllowedRepairStrategies { get; init; }=new HashSet<EdgeTreatmentRepairStrategy>();
    public IReadOnlyList<NativeEditabilityProbeSpec> EditabilityProbes { get; init; }=[];
    public double ToleranceMm { get; init; }=0.05;
    public double AngleToleranceDegrees { get; init; }=0.1;
}

public sealed record EdgeTreatmentObservation
{
    public required string SourceSha256 { get; init; }
    public required string SourceRevisionId { get; init; }
    public required string ModelSha256 { get; init; }
    public bool ModelReopened { get; init; }
    public EdgeTreatmentRoute ActualRoute { get; init; }
    public double RadiusMm { get; init; }
    public double DistanceMm { get; init; }
    public double SecondDistanceMm { get; init; }
    public double AngleDegrees { get; init; }
    public ChamferMode ChamferMode { get; init; }=ChamferMode.DistanceAngle;
    public string NativeFeatureType { get; init; }=string.Empty;
    public bool NativeFeatureTypeRecognized { get; init; }=true;
    public bool FeatureSuppressed { get; init; }
    public bool NativeEditable { get; init; }
    public bool RebuildSucceeded { get; init; }
    public bool TopologyChanged { get; init; }
    public IReadOnlyList<GeometryRefResolution> EdgeResolutions { get; init; }=[];
    public IReadOnlyList<string> NativeDrivingEdgePersistentReferences { get; init; }=[];
    public IReadOnlyList<EdgeTreatmentRepairStrategy> RepairStrategiesUsed { get; init; }=[];
    public bool RepairEvidenceSupplied { get; init; }
    public RepairAttempt? RepairAttempt { get; init; }
    public RepairExecutionEvidence? RepairExecution { get; init; }
    public CoverageReport? RepairCoverage { get; init; }
    public IReadOnlyList<RepairCheckResult> RepairChecks { get; init; }=[];
    public RepairValidationResult? RepairValidation { get; init; }
    public ModelDiff? CollateralDiff { get; init; }
}

public static class EdgeTreatmentVerifier
{
    public static Stage4VerificationResult Evaluate(EdgeTreatmentIntent intent,EdgeTreatmentObservation actual)
    {
        ArgumentNullException.ThrowIfNull(intent);ArgumentNullException.ThrowIfNull(actual);
        var checks=new List<Stage4Check>();
        var intentValid=intent.Contract==EdgeTreatmentIntent.ContractVersion&&Stage4EvidenceBinding.Hash(intent.SourceSha256)&&
            !string.IsNullOrWhiteSpace(intent.SourceRevisionId)&&!string.IsNullOrWhiteSpace(intent.IntentId)&&!string.IsNullOrWhiteSpace(intent.SourceLiteral)&&
            Positive(intent.ToleranceMm)&&Positive(intent.AngleToleranceDegrees)&&intent.AngleToleranceDegrees<90&&
            intent.EditabilityProbes.Count>0&&intent.EditabilityProbes.Select(p=>p.ProbeId).Distinct(StringComparer.Ordinal).Count()==intent.EditabilityProbes.Count&&
            intent.EditabilityProbes.All(p=>!string.IsNullOrWhiteSpace(p.ProbeId)&&!string.IsNullOrWhiteSpace(p.DimensionName)&&double.IsFinite(p.TrialValue))&&
            (intent.Route==EdgeTreatmentRoute.ProfileArc||intent.TargetEdges.Count>0&&intent.TargetEdges.Select(r=>r.RefId).Distinct(StringComparer.Ordinal).Count()==intent.TargetEdges.Count);
        Add("intent-contract",intentValid,"Edge-treatment source identity, target scope and finite positive tolerances are required.");
        Add("identity",intentValid&&Stage4EvidenceBinding.Hash(actual.ModelSha256)&&intent.SourceSha256.Equals(actual.SourceSha256,StringComparison.OrdinalIgnoreCase)&&
            intent.SourceRevisionId.Equals(actual.SourceRevisionId,StringComparison.Ordinal)&&actual.ModelReopened,"Source identity and reopened candidate model must match.");
        Add("semantic-route",actual.NativeFeatureTypeRecognized&&!actual.FeatureSuppressed&&intent.Route==actual.ActualRoute,
            "Profile arc, solid fillet and solid chamfer must come from a recognized, unsuppressed native feature; source intent cannot fill an unknown actual type.");
        var parameterOk=intent.Route switch
        {
            EdgeTreatmentRoute.ProfileArc or EdgeTreatmentRoute.SolidFillet=>Positive(intent.RadiusMm)&&Positive(actual.RadiusMm)&&Math.Abs(intent.RadiusMm-actual.RadiusMm)<=intent.ToleranceMm,
            EdgeTreatmentRoute.SolidChamfer=>ChamferMatches(intent,actual),
            _=>false
        };
        Add("locked-parameters",parameterOk,"Repair may reselect/regroup/reorder, but source radius/chamfer values remain locked and are remeasured.");
        Add("editable",actual.NativeEditable&&actual.RebuildSucceeded,"Supported edge-treatment result must remain editable and rebuild after legal parameter driving.");
        if(intent.Route!=EdgeTreatmentRoute.ProfileArc)
        {
            var byRef=actual.EdgeResolutions.GroupBy(r=>r.Reference.RefId,StringComparer.Ordinal).ToDictionary(g=>g.Key,g=>g.ToArray(),StringComparer.Ordinal);
            var expectedIds=intent.TargetEdges.Select(r=>r.RefId).ToHashSet(StringComparer.Ordinal);
            var allResolved=intent.TargetEdges.Count>0&&byRef.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(expectedIds)&&intent.TargetEdges.All(reference=>byRef.TryGetValue(reference.RefId,out var rows)&&rows.Length==1&&rows[0].Status==GeometryRefResolutionStatus.Resolved&&
                SameGeometryReference(reference,rows[0].Reference)&&rows[0].Candidate is { } candidate&&GeometryRefResolver.Matches(reference,candidate)&&
                rows[0].CandidateIds.Count==1&&rows[0].ResolvedModelSha256.Equals(actual.ModelSha256,StringComparison.OrdinalIgnoreCase)&&
                (rows[0].Reference.SourceRevisionId is not { } edgeRevision||edgeRevision.Equals(intent.SourceRevisionId,StringComparison.Ordinal)));
            Add("edge-group",allResolved,"Every target edge must be uniquely re-resolved on the current saved model; edge ordinal/first-match fallback is forbidden.");
            var resolvedPersistent=actual.EdgeResolutions.Where(x=>x.Status==GeometryRefResolutionStatus.Resolved&&x.Candidate?.NativePersistentReference is {Length:>0})
                .Select(x=>x.Candidate!.NativePersistentReference!).ToHashSet(StringComparer.Ordinal);
            var featurePersistent=actual.NativeDrivingEdgePersistentReferences.Where(x=>!string.IsNullOrWhiteSpace(x)).ToHashSet(StringComparer.Ordinal);
            Add("native-edge-ownership",resolvedPersistent.Count==intent.TargetEdges.Count&&featurePersistent.SetEquals(resolvedPersistent),
                "The native fillet/chamfer definition must own exactly the persistent edges resolved for this source target group.");
        }
        if(actual.TopologyChanged)
            Add("topology-rebind",intent.Route==EdgeTreatmentRoute.ProfileArc||intent.TargetEdges.All(r=>actual.EdgeResolutions.Any(x=>x.Reference.RefId==r.RefId&&x.Rebound&&x.Status==GeometryRefResolutionStatus.Resolved)),
                "Topology change requires explicit current-model rebinding evidence for every solid edge target.");
        var repairPresent=actual.RepairEvidenceSupplied||actual.RepairAttempt is not null||actual.RepairExecution is not null||
            actual.RepairCoverage is not null||actual.CollateralDiff is not null||actual.RepairChecks.Count>0||actual.RepairStrategiesUsed.Count>0;
        var supportedRepairKind=!repairPresent||actual.RepairAttempt?.Kind is RepairKind.FilletSelection or RepairKind.GeometryRefRebind;
        var repairTargetBound=!repairPresent||actual.RepairAttempt is not null&&!string.IsNullOrWhiteSpace(intent.TargetOperationId)&&
            actual.RepairAttempt.TargetOperationId.Equals(intent.TargetOperationId,StringComparison.Ordinal);
        var repairStrategiesOk=!repairPresent||supportedRepairKind&&repairTargetBound&&actual.RepairStrategiesUsed.Count>0&&
            actual.RepairStrategiesUsed.All(intent.AllowedRepairStrategies.Contains);
        Add("repair-policy",repairStrategiesOk,
            "Any supplied repair history must use a supported edge-treatment repair kind, target this exact treatment operation, and map to an intent-authorized strategy.");
        if(repairPresent)
        {
            RepairValidationResult? recomputed=null;
            if(actual.RepairAttempt is not null&&actual.RepairExecution is not null&&actual.RepairCoverage is not null&&actual.CollateralDiff is not null)
                recomputed=ConstrainedRepairSession.ValidatePostRepair(actual.RepairAttempt,actual.RepairExecution,actual.RepairCoverage,actual.CollateralDiff,actual.RepairChecks);
            var boundToCurrent=supportedRepairKind&&repairTargetBound&&recomputed is {Status:RepairValidationStatus.Passed}&&
                recomputed.SourceSha256?.Equals(intent.SourceSha256,StringComparison.OrdinalIgnoreCase)==true&&
                recomputed.SourceRevisionId==intent.SourceRevisionId&&
                recomputed.CandidateModelSha256?.Equals(actual.ModelSha256,StringComparison.OrdinalIgnoreCase)==true&&
                recomputed.CandidateModelReopened&&actual.RepairAttempt?.RequestId==recomputed.RequestId&&actual.RepairExecution?.ExecutionId==recomputed.ExecutionId;
            Add("T14-repair-gate",boundToCurrent,
                "T19 recomputes the complete T14 gate and requires the exact attempt/execution/source/candidate identity; a portable Passed flag is insufficient.");
            Add("T13-collateral-diff",actual.CollateralDiff is {Status:ModelDiffStatus.Comparable,BaselineModelReopened:true,CandidateModelReopened:true,BaselineCaptureComplete:true,CandidateCaptureComplete:true}&&
                actual.CollateralDiff.CaptureScopeIds.Count>0&&!actual.CollateralDiff.HasUnexpectedImpact&&actual.RepairExecution is not null&&
                actual.CollateralDiff.BaselineModelSha256.Equals(actual.RepairExecution.BaselineModelSha256,StringComparison.OrdinalIgnoreCase)&&
                actual.CollateralDiff.CandidateModelSha256.Equals(actual.ModelSha256,StringComparison.OrdinalIgnoreCase),
                "Any repaired result must carry a complete reopened T13 diff bound to the same baseline/candidate execution.");
        }
        return new(checks);
        void Add(string id,bool ok,string message)=>checks.Add(new(id,ok?RequirementCheckStatus.Passed:RequirementCheckStatus.Failed,message));
    }

    private static bool Positive(double x)=>double.IsFinite(x)&&x>0;
    private static bool SameGeometryReference(GeometryRef expected,GeometryRef observed)
    {
        try{return GeometryRefResolver.Fingerprint(expected)==GeometryRefResolver.Fingerprint(observed);}
        catch(ArgumentException){return false;}
    }
    private static bool ChamferMatches(EdgeTreatmentIntent e,EdgeTreatmentObservation a)
    {
        if(e.ChamferMode!=a.ChamferMode||!Positive(e.ToleranceMm)||!Positive(e.AngleToleranceDegrees)||!Positive(e.DistanceMm)||!Positive(a.DistanceMm)||
           !double.IsFinite(a.AngleDegrees)||!double.IsFinite(a.SecondDistanceMm)||Math.Abs(e.DistanceMm-a.DistanceMm)>e.ToleranceMm)return false;
        return e.ChamferMode switch
        {
            ChamferMode.DistanceAngle=>double.IsFinite(e.AngleDegrees)&&e.AngleDegrees>0&&e.AngleDegrees<90&&a.AngleDegrees>0&&a.AngleDegrees<90&&
                Math.Abs(e.AngleDegrees-a.AngleDegrees)<=e.AngleToleranceDegrees,
            ChamferMode.TwoDistances=>Positive(e.SecondDistanceMm)&&Positive(a.SecondDistanceMm)&&Math.Abs(e.SecondDistanceMm-a.SecondDistanceMm)<=e.ToleranceMm,
            ChamferMode.EqualDistance=>Positive(a.SecondDistanceMm)&&Math.Abs(e.DistanceMm-a.SecondDistanceMm)<=e.ToleranceMm,
            _=>false
        };
    }
}

internal static class Stage4EvidenceBinding
{
    public static bool Hash(string? value)=>value is {Length:64}&&value.All(Uri.IsHexDigit);

    public static bool Passes(string id,string sourceSha,string sourceRevision,string modelSha,string scopeFingerprint,string? requirementFingerprint,
        IReadOnlyList<Stage4BoundCheckEvidence> evidence)
    {
        if(!Hash(sourceSha)||!Hash(modelSha)||!Hash(scopeFingerprint)||!Hash(requirementFingerprint))return false;
        var matching=evidence.Where(item=>item.CheckId.Equals(id,StringComparison.Ordinal)).ToArray();
        return matching.Length==1&&matching[0] is {Status:RequirementCheckStatus.Passed,ModelReopened:true,CaptureComplete:true,ProducerScopePassed:true} item&&
            !string.IsNullOrWhiteSpace(item.EvidenceId)&&item.SourceSha256.Equals(sourceSha,StringComparison.OrdinalIgnoreCase)&&
            item.SourceRevisionId.Equals(sourceRevision,StringComparison.Ordinal)&&item.ModelSha256.Equals(modelSha,StringComparison.OrdinalIgnoreCase)&&
            item.RequiredScopeFingerprint.Equals(scopeFingerprint,StringComparison.OrdinalIgnoreCase)&&
            item.RequirementFingerprint.Equals(requirementFingerprint,StringComparison.OrdinalIgnoreCase);
    }
}

public static class Stage4EvidenceAdapter
{
    public static Stage4BoundCheckEvidence FromConnectivity(ConnectivityCheck check,string requiredScopeFingerprint)
    {
        ArgumentNullException.ThrowIfNull(check);ArgumentException.ThrowIfNullOrWhiteSpace(requiredScopeFingerprint);
        return new()
        {
            CheckId=check.RequirementId,
            Status=check.Status switch {ConnectivityStatus.Passed=>RequirementCheckStatus.Passed,ConnectivityStatus.Failed=>RequirementCheckStatus.Failed,_=>RequirementCheckStatus.Unverifiable},
            EvidenceId="T08:"+check.RequirementId+":"+check.RequirementFingerprint,
            SourceSha256=check.SourceSha256??string.Empty,SourceRevisionId=check.SourceRevisionId,ModelSha256=check.ActualModelSha256,
            RequiredScopeFingerprint=requiredScopeFingerprint,RequirementFingerprint=check.RequirementFingerprint,
            ModelReopened=check.ModelReopened,CaptureComplete=check.ModelReopened&&check.ObservationComplete&&check.Status is ConnectivityStatus.Passed or ConnectivityStatus.Failed,
            ProducerScopePassed=check.Status==ConnectivityStatus.Passed
        };
    }

    public static Stage4BoundCheckEvidence FromProjection(ProjectionReport report,string requirementId,string requiredScopeFingerprint)
    {
        ArgumentNullException.ThrowIfNull(report);ArgumentException.ThrowIfNullOrWhiteSpace(requirementId);ArgumentException.ThrowIfNullOrWhiteSpace(requiredScopeFingerprint);
        var matches=report.Requirements.Where(item=>item.RequirementId.Equals(requirementId,StringComparison.Ordinal)).ToArray();
        if(matches.Length!=1)throw new ArgumentException("Projection evidence must contain exactly one producer-owned requirement identity.",nameof(requirementId));
        var result=matches[0];
        var complete=report.ModelReopened&&report.CaptureLimitations.Count==0&&report.RequiredPrimitiveCount>0&&
            report.CheckedRequiredPrimitiveCount==report.RequiredPrimitiveCount&&result.Status is ProjectionRequirementStatus.Passed or ProjectionRequirementStatus.Failed;
        var localStatus=result.Status switch {ProjectionRequirementStatus.Passed=>RequirementCheckStatus.Passed,ProjectionRequirementStatus.Failed=>RequirementCheckStatus.Failed,_=>RequirementCheckStatus.Unverifiable};
        var status=!complete?RequirementCheckStatus.Unverifiable:!report.Passed?RequirementCheckStatus.Failed:localStatus;
        return new()
        {
            CheckId=requirementId,
            Status=status,
            EvidenceId="T09:"+requirementId+":"+(result.RequirementFingerprint??"missing"),
            SourceSha256=report.SourceSha256,SourceRevisionId=report.SourceRevisionId??string.Empty,ModelSha256=report.ActualModelSha256,
            RequiredScopeFingerprint=requiredScopeFingerprint,RequirementFingerprint=result.RequirementFingerprint??string.Empty,
            ModelReopened=report.ModelReopened,CaptureComplete=complete,ProducerScopePassed=report.Passed
        };
    }
}
