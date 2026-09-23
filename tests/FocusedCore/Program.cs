using CadModeling.Core;
using CadModeling.Ir;

var count=0;
void Check(bool condition,string name) {if(!condition)throw new Exception(name);count++;Console.WriteLine("PASS "+name);}
var c=new CylinderGroupCheck{Id="hole",SourceLiteral="Diameter 10, axis x=20, depth 10",DiameterMm=10,LengthMm=10,AxisStartsMm=[new(20,0,0)]};
var spec=new ModelVerificationSpec{CylinderGroups=[c]};
var wall=new MeasuredCylinder(new(20,0,0),new(20,0,10),new(0,0,1),5,100*Math.PI,true,"hole");
bool Pass(params MeasuredCylinder[] walls)=>ModelVerification.Evaluate(spec,walls,new Dictionary<string,double>(),null).Passed;
Check(Pass(wall),"exact cylinder passes");
Check(Pass(wall with {AxisStartMm=wall.AxisEndMm,AxisEndMm=wall.AxisStartMm,Direction=new(0,0,-1)}),"surface parameter direction does not reverse hole intent");
Check(!Pass(),"missing hole rejected");
Check(!Pass(wall with {RadiusMm=4}),"wrong diameter rejected");
Check(!Pass(wall with {AxisStartMm=new(-20,0,0),AxisEndMm=new(-20,0,10)}),"mirrored position rejected");
Check(!Pass(wall with {AxisStartMm=new(20,0,1),AxisEndMm=new(20,0,11)}),"correct length at wrong axial station rejected");
Check(!Pass(wall with {AxisEndMm=new(20,0,8)}),"wrong depth rejected");
Check(!Pass(wall with {Interior=false}),"boss cannot satisfy a hole requirement");
Check(!Pass(wall with {AreaMm2=50*Math.PI}),"half hole is not a complete hole");
Check(!Pass(wall,wall with {AxisStartMm=new(30,0,0),AxisEndMm=new(30,0,10)}),"extra hole rejected");
Check(Pass(wall with {AreaMm2=50*Math.PI},wall with {AreaMm2=50*Math.PI}),"seam split cylinder counted once");
Check(Pass(wall with {AxisEndMm=new(20,0,5),AreaMm2=50*Math.PI},wall with {AxisStartMm=new(20,0,5),AreaMm2=50*Math.PI}),"axially split contiguous wall counted once");
Check(!Pass(wall with {AxisEndMm=new(20,0,4),AreaMm2=40*Math.PI},wall with {AxisStartMm=new(20,0,6),AreaMm2=40*Math.PI}),"coaxial blind holes with a material gap not merged");
Check(!Pass(wall with {AreaMm2=double.NaN}),"invalid native measurement is unverifiable");
var diagonal=ModelVerification.Unit(new(1,1,1));
var start=new Vector3(10,20,30);
var tilted=c with {AxisStartsMm=[start],Direction=diagonal};
var tiltedWall=wall with {AxisStartMm=start,AxisEndMm=ModelVerification.Add(start,ModelVerification.Scale(diagonal,10)),Direction=diagonal};
Check(ModelVerification.Evaluate(new(){CylinderGroups=[tilted]},[tiltedWall],new Dictionary<string,double>(),null).Passed,"arbitrary axis direction measured in model coordinates");
Check(ModelVerification.Validate(new(){CylinderGroups=[c with {AxisStartsMm=[new(0,0,0),new(0,0,0)]}]},null).Any(),"duplicate expected axes rejected");
Check(ModelVerification.Validate(new(){CylinderGroups=[c with {Direction=new(0,0,0)}]},null).Any(),"zero direction rejected");
Check(ModelVerification.Validate(new(){CylinderGroups=[c with {ToleranceMm=double.PositiveInfinity}]},null).Any(),"infinite tolerance rejected");
Check(ModelVerification.Validate(new(){CylinderGroups=[c,c]},null).Any(),"duplicate check ID rejected");
var native=new ModelVerificationSpec{NativeDimensions=[new(){Id="angle",SourceLiteral="90 degrees",DimensionName="D1@revolve",Unit=DrawingValueUnit.Degree,Value=90,Tolerance=.01}]};
Check(ModelVerification.Evaluate(native,[],new Dictionary<string,double>{{"D1@revolve",Math.PI/2}},null).Passed,"native radians converted to degrees");
Check(!ModelVerification.Evaluate(native,[],new Dictionary<string,double>(),null).Passed,"absent native dimension unverifiable");
var sample=new SurfaceSampleCheck{Id="local",SourceLiteral="Hole wall at (5,0,5)",SurfaceKind=LocalSurfaceKind.Cylinder,PointsMm=[new(5,0,5)],OutwardNormals=[new(-1,0,0)],DiameterMm=10};
var local=new ModelVerificationSpec{SurfaceSamples=[sample]};
var face=new LocalFaceMeasurement(new(5,0,5),new(-1,0,0),LocalSurfaceKind.Cylinder,10);
ModelVerificationResult Local(ModelVerificationSpec checks, LocalPointMeasurement measured)=>ModelVerification.Evaluate(checks,[],new Dictionary<string,double>(),null,new Dictionary<string,IReadOnlyList<LocalPointMeasurement>>{{"local",new[]{measured}}});
Check(!ModelVerification.Validate(local,null).Any(),"valid local cylinder contract");
Check(Local(local,new([face],true)).Passed,"actual partial wall sample passes");
Check(!Local(local,new([face with {ClosestPointMm=new(7,0,5)}],true)).Passed,"trimmed-away analytic extension rejected");
Check(!Local(local,new([face with {OutwardNormal=new(1,0,0)}],true)).Passed,"opposite material side rejected");
Check(!Local(local,new([face with {DiameterMm=9}],true)).Passed,"same point wrong cylinder radius rejected");
Check(!Local(local,new([face with {Kind=LocalSurfaceKind.Plane}],true)).Passed,"wrong surface kind rejected");
Check(!Local(local,new([face],false,"COM failed")).Passed,"partial face inventory fails closed");
Check(!Local(local,new([],true)).Passed,"empty solid inventory fails closed");
Check(!ModelVerification.Evaluate(local,[],new Dictionary<string,double>(),null).Passed,"missing local measurements fail closed");
Check(!Local(local,new([face with {OutwardNormal=new(double.NaN,0,0)}],true)).Passed,"invalid measured normal fails closed");
var cone=local with {SurfaceSamples=[sample with {SurfaceKind=LocalSurfaceKind.Cone,DiameterMm=null,ConeHalfAngleDegrees=59}]};
Check(Local(cone,new([face with {Kind=LocalSurfaceKind.Cone,DiameterMm=null,ConeHalfAngleDegrees=59}],true)).Passed,"cone half angle accepted");
Check(!Local(cone,new([face with {Kind=LocalSurfaceKind.Cone,DiameterMm=null,ConeHalfAngleDegrees=45}],true)).Passed,"wrong drill tip angle rejected");
var gap=new ModelVerificationSpec{BoundaryClearances=[new(){Id="local",SourceLiteral="No wall within 2 mm",PointsMm=[new(5,0,5)],MinimumDistanceMm=2}]};
Check(Local(gap,new([face with {ClosestPointMm=new(8,0,5)}],true)).Passed,"boundary clearance passes");
Check(!Local(gap,new([face],true)).Passed,"uncut wall violates clearance");
Check(!Local(gap,new([face with {ClosestPointMm=new(8,0,5)}],false)).Passed,"unread face cannot certify clearance");
Check(ModelVerification.Validate(local with {SurfaceSamples=[sample with {OutwardNormals=[]}]},null).Any(),"missing normals rejected");
Check(ModelVerification.Validate(local with {SurfaceSamples=[sample with {SurfaceKind=LocalSurfaceKind.Plane}]},null).Any(),"irrelevant cylinder parameter rejected");
Check(ModelVerification.Validate(gap with {BoundaryClearances=[gap.BoundaryClearances[0] with {MinimumDistanceMm=.001}]},null).Any(),"vacuous clearance tolerance rejected");
var angleFact=new DrawingDimensionFact{Id="source_angle",OperationId="sink",ParameterPath="feature.countersink_angle_degrees",Value=118,Unit=DrawingValueUnit.Degree,SourceLiteral="118 degrees"};
var angleContext=new DrawingPlanContext{SourcePath="unused-in-validator",Dimensions=[angleFact]};
var boundCone=cone with {SurfaceSamples=[cone.SurfaceSamples[0] with {SourceDimensionIds=["source_angle"]}],Bindings=[new("source_angle","local","cone_included_angle_degrees")]};
Check(!ModelVerification.Validate(boundCone,angleContext).Any(),"included source angle binds to twice measured half angle");
Check(ModelVerification.Validate(boundCone,angleContext with {Dimensions=[angleFact with {Value=90}]}).Any(),"included source angle mismatch rejected");
Check(ModelVerification.Validate(boundCone,angleContext with {Dimensions=[angleFact with {Unit=DrawingValueUnit.Millimeter}]}).Any(),"cone angle cannot bind a length");
Check(ModelVerification.Validate(local with {SurfaceSamples=[sample with {PointsMm=Enumerable.Repeat(new Vector3(5,0,5),513).ToArray(),OutwardNormals=Enumerable.Repeat(new Vector3(-1,0,0),513).ToArray()}]},null).Any(),"unbounded local workload rejected");
var areaSpec=local with {SurfaceSamples=[sample with {ExpectedAreaMm2=50*Math.PI}]};
var areaFace=face with {FaceIndex=7,AreaMm2=50*Math.PI};
Check(Local(areaSpec,new([areaFace],true)).Passed,"partial wall area matches analytic source");
Check(!Local(areaSpec,new([areaFace with {AreaMm2=40*Math.PI}],true)).Passed,"correct sample with missing remote area rejected");
Check(!Local(areaSpec,new([areaFace with {AreaMm2=60*Math.PI}],true)).Passed,"extra wall area rejected");
Check(!Local(areaSpec,new([face],true)).Passed,"missing face area fails closed");
var repeated=areaSpec with {SurfaceSamples=[areaSpec.SurfaceSamples[0] with {PointsMm=[new(5,0,5),new(5,0,5)],OutwardNormals=[new(-1,0,0),new(-1,0,0)]}]};
Check(ModelVerification.Evaluate(repeated,[],new Dictionary<string,double>(),null,new Dictionary<string,IReadOnlyList<LocalPointMeasurement>>{{"local",new[]{new LocalPointMeasurement([areaFace],true),new LocalPointMeasurement([areaFace],true)}}}).Passed,"same face sampled twice is counted once");
Check(!Local(areaSpec,new([areaFace with {AreaErrorMm2=.2}],true)).Passed,"area uncertainty cannot exceed acceptance tolerance");
Check(Local(areaSpec,new([areaFace with {AreaErrorMm2=.01}],true)).Passed,"converged area retains uncertainty margin");
Check(!Local(areaSpec,new([areaFace with {AreaMm2=50*Math.PI+.095,AreaErrorMm2=.01}],true)).Passed,"near-threshold area remains unverifiable");
Check(!Local(areaSpec,new([areaFace with {AreaErrorMm2=double.NaN}],true)).Passed,"invalid numerical uncertainty fails closed");

// T06: one versioned GeometryRef is shared by inspection/measurement; native refs never override geometry identity.
var t6ShaA=new string('A',64);var t6ShaB=new string('B',64);
GeometrySignature T6Sig(double? radius=5,double x=20)=>new(){EntityKind=EntityKind.Face,GeometryKind=GeometryKind.Cylinder,
    AnchorMm=new(x,0,0),Direction=new(0,0,1),RadiusMm=radius,AreaMm2=100};
GeometryRef T6Ref(string? persistent="persist-1",double? radius=5)=>new(){RefId="ref-hole",DocumentId="DOC-A",ModelSha256=t6ShaA,
    NativePersistentReference=persistent,EntityKind=EntityKind.Face,GeometryKind=GeometryKind.Cylinder,SourceFactIds=["fact-hole"],SourceRevisionId="rev-1",Signature=T6Sig(radius)};
GeometryCandidate T6Candidate(string id,string? persistent="persist-1",double? radius=5,double x=20)=>new(){CandidateId=id,NativePersistentReference=persistent,Signature=T6Sig(radius,x)};
var t6Doc=new GeometryDocumentIdentity{DocumentId="DOC-A",ModelSha256=t6ShaA,SourceRevisionId="rev-1"};
var t6Exact=GeometryRefResolver.Resolve(T6Ref(),t6Doc,[T6Candidate("face-1")]);
Check(t6Exact.Status==GeometryRefResolutionStatus.Resolved&&t6Exact.NativeReferenceRecovered,"T06 persistent ref plus matching geometry signature resolves");
Check(GeometryRefResolver.Resolve(T6Ref(),t6Doc with{DocumentId="DOC-B"},[T6Candidate("face-1")]).Status==GeometryRefResolutionStatus.WrongDocument,"T06 wrong document rejected");
Check(GeometryRefResolver.Resolve(T6Ref(),t6Doc,[]).Status==GeometryRefResolutionStatus.Missing,"T06 deleted face is missing instead of silently retargeted");
var t6Ambiguous=GeometryRefResolver.Resolve(T6Ref(null),t6Doc,[T6Candidate("face-1","a"),T6Candidate("face-2","b")]);
Check(t6Ambiguous.Status==GeometryRefResolutionStatus.Ambiguous&&t6Ambiguous.CandidateIds.Count==2,"T06 identical cylinder candidates are ambiguous");
Check(GeometryRefResolver.Resolve(T6Ref(),t6Doc,[T6Candidate("wrong-native","persist-1",6)]).Status==GeometryRefResolutionStatus.Stale,"T06 recovered native ref with changed geometry signature is stale");
var t6Rebound=GeometryRefResolver.Resolve(T6Ref(),t6Doc with{ModelSha256=t6ShaB},[T6Candidate("unique-new","persist-new")]);
Check(t6Rebound.Status==GeometryRefResolutionStatus.Resolved&&t6Rebound.Rebound&&t6Rebound.ResolvedModelSha256==t6ShaB,"T06 changed model permits only evidenced unique signature rebind");
Check(GeometryRefResolver.Resolve(T6Ref(),t6Doc with{SourceRevisionId="rev-2"},[T6Candidate("face-1")]).Status==GeometryRefResolutionStatus.Stale,"T06 source revision change invalidates geometry ref");
Check(GeometryRefResolver.Resolve(T6Ref(),t6Doc,[T6Candidate("nan") with{Signature=T6Sig(double.NaN,double.NaN) with{AreaMm2=double.NaN}}]).Status!=GeometryRefResolutionStatus.Resolved,"T06 non-finite candidate geometry can never resolve");

// T07: actual analytic geometry measurement is separate from ref resolution and source requirement comparison.
var t7Ref=T6Ref("persist-measure",null) with{RefId="measure-hole",Signature=T6Sig(null)};
var t7Candidate=T6Candidate("measure-face","persist-measure",5);
var t7Resolution=GeometryRefResolver.Resolve(t7Ref,t6Doc,[t7Candidate]);
var t7Query=new MeasurementQuery{QueryId="diameter",Geometry=t7Ref,Kind=MeasurementKind.CylinderDiameter,Unit=DrawingValueUnit.Millimeter,NumericalTolerance=.001};
var t7Measured=DirectionalMeasurementEngine.Measure(t7Query,t7Resolution,null,t6ShaA,true);
var t7Requirement=new MeasurementRequirement{RequirementId="diameter-source",QueryId="diameter",SourceFactId="fact-hole",SourceRevisionId="rev-1",SourceSha256=t6ShaA,SourceFactFingerprint=CoverageReviewer.SourceFactIdentity(t6ShaA,"rev-1","fact-hole"),ExpectedScalar=10,Unit=DrawingValueUnit.Millimeter,Tolerance=.05};
Check(DirectionalMeasurementVerifier.Compare(t7Query,t7Measured,t7Requirement).Status==RequirementCheckStatus.Passed,"T07 actual resolved cylinder diameter satisfies source requirement");
Check(DirectionalMeasurementVerifier.Compare(t7Query,t7Measured,t7Requirement with{ExpectedScalar=8}).Status==RequirementCheckStatus.Failed,"T07 wrong saved B-Rep diameter fails independent source requirement");
var t7NotReopened=DirectionalMeasurementEngine.Measure(t7Query,t7Resolution,null,t6ShaA,false);
Check(DirectionalMeasurementVerifier.Compare(t7Query,t7NotReopened,t7Requirement).Status==RequirementCheckStatus.Unverifiable,"T07 unsaved/not-reopened measurement cannot pass");
var t7InchQuery=t7Query with{QueryId="diameter-inch",Unit=DrawingValueUnit.Inch};
var t7Inch=DirectionalMeasurementEngine.Measure(t7InchQuery,t7Resolution,null,t6ShaA,true);
var t7InchReq=t7Requirement with{RequirementId="diameter-inch-source",QueryId="diameter-inch",ExpectedScalar=10/25.4,Unit=DrawingValueUnit.Inch,Tolerance=.002};
Check(DirectionalMeasurementVerifier.Compare(t7InchQuery,t7Inch,t7InchReq).Status==RequirementCheckStatus.Passed,"T07 millimeter B-Rep converts to inches without 25.4x error");
Check(DirectionalMeasurementEngine.Measure(t7Query,t7Resolution,null,t6ShaB,true).Status==GeometryMeasurementStatus.WrongModel,"T07 measurement refuses model SHA different from ref resolution model");
Check(DirectionalMeasurementVerifier.Compare(t7Query,t7Measured,t7Requirement with{SourceRevisionId="rev-2"}).Status==RequirementCheckStatus.Stale,"T07 source requirement revision mismatch is stale, not passed");
Check(DirectionalMeasurementVerifier.Compare(t7Query with{Geometry=t7Ref with{ModelSha256=t6ShaB}},t7Measured,t7Requirement).Status!=RequirementCheckStatus.Passed,"T07 old model measurement cannot be replayed against a changed query");
var t7PlaneRef=t7Ref with{RefId="plane-a",GeometryKind=GeometryKind.Plane,Signature=new(){EntityKind=EntityKind.Face,GeometryKind=GeometryKind.Plane,AnchorMm=new(0,0,0),Direction=new(0,0,1)}};
var t7PlaneRef2=t7PlaneRef with{RefId="plane-b",Signature=t7PlaneRef.Signature with{AnchorMm=new(0,0,10)}};
var t7PlaneRes=GeometryRefResolver.Resolve(t7PlaneRef,t6Doc,[new(){CandidateId="plane-a",NativePersistentReference=t7PlaneRef.NativePersistentReference,Signature=t7PlaneRef.Signature}]);
var t7PlaneRes2=GeometryRefResolver.Resolve(t7PlaneRef2,t6Doc,[new(){CandidateId="plane-b",NativePersistentReference=t7PlaneRef2.NativePersistentReference,Signature=t7PlaneRef2.Signature}]);
var t7PlaneQuery=t7Query with{QueryId="plane-distance",Geometry=t7PlaneRef,SecondaryGeometry=t7PlaneRef2,Kind=MeasurementKind.PlaneSeparation};
var t7PlaneMeasured=DirectionalMeasurementEngine.Measure(t7PlaneQuery,t7PlaneRes,t7PlaneRes2,t6ShaA,true);
var t7PlaneReq=t7Requirement with{RequirementId="plane-distance-source",QueryId="plane-distance",ExpectedScalar=10};
Check(DirectionalMeasurementVerifier.Compare(t7PlaneQuery,t7PlaneMeasured with{SecondaryReferenceResolution=null},t7PlaneReq).Status!=RequirementCheckStatus.Passed,"T07 two-reference measurement cannot pass when secondary resolution evidence is missing");

// T08: bounded hole/cavity/material connectivity checks use multi-point/opening evidence and fail closed.
AxialOpeningEvidence T8Open(string id)=>new(){EndId=id,SampleCount=3,VoidSampleCount=3,Complete=true,ProbeOffsetMm=.02,NumericalUncertaintyMm=.001,Method="bounded_multi_point_probe"};
AxialOpeningEvidence T8Closed(string id,double thickness=.5)=>new(){EndId=id,SampleCount=3,MaterialSampleCount=3,Complete=true,ProbeOffsetMm=.02,MaterialThicknessBeyondMm=thickness,NumericalUncertaintyMm=.001,Method="bounded_multi_point_probe"};
AxialPassageEvidence T8Passage()=>new(){SampleCount=3,Complete=true,CoverageStartMm=0,CoverageEndMm=10,BodyScopeComplete=true,LateralOutletExcluded=true,BodyIds=["body-1"],NumericalUncertaintyMm=.001,Method="bounded_axial_passage_probe"};
AxialSegmentEvidence T8Segment(string id,double start,double end,double radius)=>new(){SegmentId=id,StartMm=start,EndMm=end,RadiusMm=radius,BodyId="body-1",LateralBoundaryExcluded=true};
ConnectivityObservation T8Obs(string id)=>new(){CheckId=id,ActualModelSha256=t6ShaA,ModelReopened=true,GeometryResolutions=[t7Resolution],Axis=new(0,0,1),AxialInterval=new(0,10),AxialSegmentDetails=[T8Segment("s1",0,10,5)],PassageEvidence=T8Passage(),StartEvidence=T8Open("start"),EndEvidence=T8Open("end"),Complete=true,Method="synthetic_brep_evidence"};
ConnectivityRequirement T8Req(string id,ConnectivityKind kind)=>new(){RequirementId=id,SourceFactId="fact-"+id,SourceRevisionId="rev-1",SourceSha256=t6ShaA,SourceFactFingerprint=CoverageReviewer.SourceFactIdentity(t6ShaA,"rev-1","fact-"+id),Kind=kind};
Check(ConnectivityVerifier.Evaluate(T8Req("through",ConnectivityKind.ThroughHole),T8Obs("through")).Status==ConnectivityStatus.Passed,"T08 true through hole has two independently probed openings");
Check(ConnectivityVerifier.Evaluate(T8Req("through",ConnectivityKind.ThroughHole),T8Obs("thin-seal") with{EndEvidence=T8Closed("end",.01)}).Status==ConnectivityStatus.Failed,"T08 thin residual seal fails through-hole requirement");
Check(ConnectivityVerifier.Evaluate(T8Req("blind",ConnectivityKind.BlindHole),T8Obs("blind") with{EndEvidence=T8Closed("bottom")}).Status==ConnectivityStatus.Passed,"T08 blind hole requires one opening and one measured bottom");
Check(ConnectivityVerifier.Evaluate(T8Req("step",ConnectivityKind.SteppedHole) with{MinimumAxialSegments=2},T8Obs("step") with{AxialSegmentDetails=[T8Segment("s1",0,5,5),T8Segment("s2",5,10,3)],EndEvidence=T8Closed("bottom")}).Status==ConnectivityStatus.Passed,"T08 simple contiguous stepped hole is supported");
Check(ConnectivityVerifier.Evaluate(T8Req("cavity",ConnectivityKind.InternalCavity),T8Obs("cavity") with{VoidGroupIds=["void-1"],StartEvidence=T8Closed("start"),EndEvidence=T8Closed("end")}).Status==ConnectivityStatus.Passed,"T08 bounded internal cavity group is recognized");
Check(ConnectivityVerifier.Evaluate(T8Req("rib",ConnectivityKind.SolidConnection),T8Obs("rib") with{MaterialGroupIds=["solid-1"]}).Status==ConnectivityStatus.Passed,"T08 connected rib and plate share one material group");
Check(ConnectivityVerifier.Evaluate(T8Req("rib",ConnectivityKind.SolidConnection),T8Obs("rib-gap") with{MaterialGroupIds=["solid-1","solid-2"],MinimumGapMm=.02}).Status==ConnectivityStatus.Failed,"T08 rib with real micro-gap is disconnected");
Check(ConnectivityVerifier.Evaluate(T8Req("multi",ConnectivityKind.SolidConnection) with{RequireConnectedMaterial=false,AllowMultipleBodies=true},T8Obs("multi") with{MaterialGroupIds=["solid-1","solid-2"]}).Status==ConnectivityStatus.Passed,"T08 explicitly allowed multi-body is not rejected by body count alone");
Check(ConnectivityVerifier.Evaluate(T8Req("through",ConnectivityKind.ThroughHole),T8Obs("wrong-model") with{ActualModelSha256=t6ShaB}).Status==ConnectivityStatus.Unverifiable,"T08 connectivity evidence cannot mix model fingerprints");
Check(ConnectivityVerifier.Evaluate(T8Req("through",ConnectivityKind.ThroughHole),T8Obs("middle-gap") with{AxialSegmentDetails=[T8Segment("s1",0,4,5),T8Segment("s2",6,10,5)]}).Status!=ConnectivityStatus.Passed,"T08 open ends cannot hide a material gap in the middle of the hole passage");
Check(ConnectivityVerifier.Evaluate(T8Req("through",ConnectivityKind.ThroughHole) with{SourceRevisionId="rev-2"},T8Obs("stale-source")).Status!=ConnectivityStatus.Passed,"T08 source revision change invalidates prior geometry evidence");
Check(ConnectivityVerifier.Evaluate(T8Req("step",ConnectivityKind.SteppedHole) with{MinimumAxialSegments=2},T8Obs("duplicate-step") with{AxialSegmentDetails=[T8Segment("s1",0,10,5),T8Segment("s2",0,10,5)]}).Status!=ConnectivityStatus.Passed,"T08 duplicate equal-radius intervals cannot prove a stepped hole");
Check(ConnectivityVerifier.Evaluate(T8Req("step",ConnectivityKind.SteppedHole) with{MinimumAxialSegments=2},T8Obs("nested-step") with{AxialSegmentDetails=[T8Segment("outer",0,10,5),T8Segment("inner",0,10,3)]}).Status!=ConnectivityStatus.Passed,"T08 coextensive different-radius walls cannot masquerade as an axial step");

// T09: projection v2 preserves arcs and line classes in the same fixed-mm verifier used by T05.
ProjectionPrimitive T9Arc(string id,double radius,double sweep,string req,string style="visible")=>new(){Id=id,Kind=ProjectionPrimitiveKind.Arc,Center=new(0,0),Start=new(radius,0),
    End=new(radius*Math.Cos(sweep*Math.PI/180),radius*Math.Sin(sweep*Math.PI/180)),RadiusMm=radius,SweepDegrees=sweep,RequirementId=req,LineStyle=style};
ProjectionSnapshot T9Model(params ProjectionPrimitive[] primitives)=>new(){ViewId="t9-view",CoordinateFrameId="t9-mm",SourceSha256=t6ShaA,NativeModelSha256=t6ShaA,NativeModelReopened=true,Primitives=primitives};
var t9Source=new ProjectionSnapshot{ViewId="t9-view",CoordinateFrameId="t9-mm",SourceSha256=t6ShaA,SourceRevisionId="rev-1",Primitives=[T9Arc("arc-source",5,90,"arc-r") with{SourceFactId="fact-arc",SourceFactFingerprint=CoverageReviewer.SourceFactIdentity(t6ShaA,"rev-1","fact-arc"),RequirementFingerprint=new string('B',64)}]};
Check(ProjectionVerifier.Compare(t9Source,T9Model(T9Arc("arc-model",5,90,"unused")),new(){CompareVisibleOnly=false,MirrorAxisXmm=0}).Passed,"T09 correct circular arc radius/endpoints/sweep passes");
var t9WrongRadius=ProjectionVerifier.Compare(t9Source,T9Model(T9Arc("arc-model",6,90,"unused")),new(){CompareVisibleOnly=false,PositionToleranceMm=2,RadiusToleranceMm=.1,MirrorAxisXmm=0});
Check(!t9WrongRadius.Passed&&t9WrongRadius.Differences.Any(d=>d.Kind==ProjectionDifferenceKind.SizeMismatch),"T09 wrong arc radius fails size check");
var t9WrongSweep=ProjectionVerifier.Compare(t9Source,T9Model(T9Arc("arc-model",5,100,"unused")),new(){CompareVisibleOnly=false,PositionToleranceMm=2,ArcSweepToleranceDegrees=.5,MirrorAxisXmm=0});
Check(!t9WrongSweep.Passed&&t9WrongSweep.Differences.Any(d=>d.Kind==ProjectionDifferenceKind.ParameterMismatch),"T09 wrong arc sweep fails parameter check");
var t9HiddenSource=new ProjectionSnapshot{ViewId="t9-view",CoordinateFrameId="t9-mm",SourceSha256=t6ShaA,Primitives=[new(){Id="hidden",Kind=ProjectionPrimitiveKind.Line,Start=new(0,0),End=new(10,0),RequirementId="hidden-r",LineStyle="hidden"}]};
Check(!ProjectionVerifier.Compare(t9HiddenSource,T9Model(),new(){CompareVisibleOnly=false,MirrorAxisXmm=0}).Passed,"T09 missing required hidden line cannot pass");
var t9VisibleInstead=T9Model(new ProjectionPrimitive{Id="visible",Kind=ProjectionPrimitiveKind.Line,Start=new(0,0),End=new(10,0),LineStyle="visible"});
Check(!ProjectionVerifier.Compare(t9HiddenSource,t9VisibleInstead,new(){CompareVisibleOnly=false,MirrorAxisXmm=0}).Passed,"T09 visible edge cannot satisfy hidden-line requirement");
var t9Skipped=ProjectionVerifier.Compare(t9HiddenSource,T9Model(),new(){CompareVisibleOnly=true,MirrorAxisXmm=0});
Check(!t9Skipped.Passed&&t9Skipped.Requirements.Single().Status==ProjectionRequirementStatus.Unverifiable,"T09 required hidden line skipped by scope is explicitly unverifiable");
var t9CenterSource=t9HiddenSource with{Primitives=[new(){Id="center",Kind=ProjectionPrimitiveKind.Line,Start=new(0,0),End=new(10,0),RequirementId="center-r",LineStyle="center"}]};
Check(!ProjectionVerifier.Compare(t9CenterSource,t9VisibleInstead,new(){CompareVisibleOnly=false,MirrorAxisXmm=0}).Passed,"T09 center line is never matched as a solid visible edge");
var t9StyleLookupCalls=0;var t9ManualLookupCalls=0;
var t9NormalStyle=ProjectionLineStyleResolver.Resolve(7,3,token=>{t9StyleLookupCalls++;return token==7?"Hidden":null;},token=>{t9ManualLookupCalls++;return "Visible";});
Check(t9NormalStyle=="hidden"&&t9StyleLookupCalls==1&&t9ManualLookupCalls==0,"T09 normal GetPolylines7 LineStyle uses only GetLineFontName path");
t9StyleLookupCalls=0;t9ManualLookupCalls=0;
var t9ManualStyle=ProjectionLineStyleResolver.Resolve(-1,4,token=>{t9StyleLookupCalls++;return "Visible";},token=>{t9ManualLookupCalls++;return token==4?"Centerline":null;});
Check(t9ManualStyle=="center"&&t9StyleLookupCalls==0&&t9ManualLookupCalls==1,"T09 manual line override uses LineFont/GetLineFontName2 path only");
Check(ProjectionLineStyleResolver.Resolve(-1,4,_=>null,_=>"custom-user-font") is null,"T09 unknown display font fails closed instead of guessing a line class");
double[] T9RawLine(double style,double font,double x0,double y0,double x1,double y1)=>[0,0,0,style,font,0,0,0,2,x0,y0,0,x1,y1,0];
var t9Raw=T9RawLine(1,0,0,0,5,0).Concat(T9RawLine(2,0,5,0,10,0)).ToArray();
Check(ProjectionDisplayPolylineParser.Parse(t9Raw).Count==2,"T09 raw display parser preserves separately emitted partial/obscured line segments");
var t9TruncatedRejected=false;try{ProjectionDisplayPolylineParser.Parse(t9Raw[..^1]);}catch(InvalidDataException){t9TruncatedRejected=true;}
Check(t9TruncatedRejected,"T09 truncated raw GetPolylines7 record fails closed offline");

// T10: only a source-defined single full plane can validate section boundaries and T08 connectivity.
var t10Boundary=new ProjectionPrimitive[]{
    new(){Id="top",Kind=ProjectionPrimitiveKind.Line,Start=new(-10,10),End=new(10,10),RequirementId="outer"},
    new(){Id="right",Kind=ProjectionPrimitiveKind.Line,Start=new(10,10),End=new(10,-10),RequirementId="outer"},
    new(){Id="bottom",Kind=ProjectionPrimitiveKind.Line,Start=new(10,-10),End=new(-10,-10),RequirementId="outer"},
    new(){Id="left",Kind=ProjectionPrimitiveKind.Line,Start=new(-10,-10),End=new(-10,10),RequirementId="outer"},
    new(){Id="hole",Kind=ProjectionPrimitiveKind.Circle,Center=new(0,0),RadiusMm=3,RequirementId="void"}};
var t10Loops=new SectionLoop[]{new(){LoopId="material",PrimitiveIds=["top","right","bottom","left"],AreaMm2=400},new(){LoopId="void",IsVoid=true,PrimitiveIds=["hole"],AreaMm2=9*Math.PI}};
var t10ConnRequirement=T8Req("conn-hole",ConnectivityKind.ThroughHole);
var t10Dependency=new SectionConnectivityRequirement{RequirementId=t10ConnRequirement.RequirementId,SourceFactId=t10ConnRequirement.SourceFactId,
    SourceRevisionId=t10ConnRequirement.SourceRevisionId,SourceFactFingerprint=t10ConnRequirement.SourceFactFingerprint,RequirementFingerprint=ConnectivityVerifier.Fingerprint(t10ConnRequirement)};
var t10Spec=new SectionSpec{SectionId="A-A",SourceFactId="section-fact",SourceRevisionId="rev-1",SourceRegionIds=["section-marker"],SourceSha256=t6ShaA,
    ViewMapId="view-map-1",ViewId="section-view",CoordinateFrameId="section-mm",PlaneOriginMm=new(0,0,0),PlaneNormal=new(1,0,0),ViewingDirection=new(1,0,0),InPlaneXDirection=new(0,1,0),RequiredConnectivity=[t10Dependency]};
SectionSnapshot T10Source()=>new(){SectionId="A-A",SourceSha256=t6ShaA,ViewId="section-view",CoordinateFrameId="section-mm",PlaneOriginMm=new(0,0,0),PlaneNormal=new(1,0,0),ViewingDirection=new(1,0,0),InPlaneXDirection=new(0,1,0),BoundaryPrimitives=t10Boundary,Loops=t10Loops};
SectionSnapshot T10Model(IReadOnlyList<ProjectionPrimitive>? boundary=null,IReadOnlyList<SectionLoop>? loops=null)=>new(){SectionId="A-A",SourceSha256=t6ShaA,ViewId="section-view",CoordinateFrameId="section-mm",PlaneOriginMm=new(0,0,0),PlaneNormal=new(1,0,0),ViewingDirection=new(1,0,0),InPlaneXDirection=new(0,1,0),NativeModelSha256=t6ShaA,ModelReopened=true,BoundaryPrimitives=boundary??t10Boundary,Loops=loops??t10Loops};
var t10Connectivity=ConnectivityVerifier.Evaluate(t10ConnRequirement,T8Obs("conn-hole"));
Check(SectionVerifier.Compare(t10Spec,T10Source(),T10Model(),[t10Connectivity],new(){CompareVisibleOnly=false,MirrorAxisXmm=0}).Status==SectionStatus.Passed,"T10 fixed single-plane section with matching material/void loops passes");
Check(SectionVerifier.Compare(t10Spec,T10Source(),T10Model() with{PlaneOriginMm=new(0,0,1)},[t10Connectivity],new(){CompareVisibleOnly=false,MirrorAxisXmm=0}).Status==SectionStatus.Failed,"T10 offset section plane fails without free translation");
Check(SectionVerifier.Compare(t10Spec,T10Source(),T10Model() with{ViewingDirection=new(-1,0,0)},[t10Connectivity],new(){CompareVisibleOnly=false,MirrorAxisXmm=0}).Status==SectionStatus.Failed,"T10 reversed viewing direction fails");
var t10NoHoleBoundary=t10Boundary.Where(p=>p.Id!="hole").ToArray();var t10NoHoleLoops=t10Loops.Where(l=>!l.IsVoid).ToArray();
Check(SectionVerifier.Compare(t10Spec,T10Source(),T10Model(t10NoHoleBoundary,t10NoHoleLoops),[t10Connectivity],new(){CompareVisibleOnly=false,MirrorAxisXmm=0}).Status==SectionStatus.Failed,"T10 missing internal hole fails section boundary/topology check");
Check(SectionVerifier.Compare(t10Spec with{Type=SectionType.Stepped},T10Source(),T10Model(),[t10Connectivity]).Status==SectionStatus.Unsupported,"T10 stepped section is explicitly unsupported rather than flattened to one plane");
Check(SectionVerifier.Compare(t10Spec,T10Source(),T10Model(),[],new(){CompareVisibleOnly=false,MirrorAxisXmm=0}).Status==SectionStatus.Unverifiable,"T10 missing required 3D connectivity evidence remains unverifiable");
var t10Hatch=t10Boundary.Append(new ProjectionPrimitive{Id="hatch-as-boundary",Kind=ProjectionPrimitiveKind.Line,Start=new(-5,-5),End=new(5,5),Required=false}).ToArray();
Check(SectionVerifier.Compare(t10Spec,T10Source(),T10Model(t10Hatch),[t10Connectivity],new(){CompareVisibleOnly=false,MirrorAxisXmm=0}).Status==SectionStatus.Failed,"T10 hatch-like extra stroke cannot masquerade as a material boundary");
var t10SwappedLoops=new SectionLoop[]{t10Loops[0] with{PrimitiveIds=["hole"]},t10Loops[1] with{PrimitiveIds=["top","right","bottom","left"]}};
Check(SectionVerifier.Compare(t10Spec,T10Source(),T10Model(loops:t10SwappedLoops),[t10Connectivity],new(){CompareVisibleOnly=false,MirrorAxisXmm=0}).Status!=SectionStatus.Passed,"T10 material/void loops cannot swap boundary ownership while retaining the same areas");
Check(SectionVerifier.Compare(t10Spec,T10Source(),T10Model(),[t10Connectivity with{SourceRevisionId="rev-old"}],new(){CompareVisibleOnly=false,MirrorAxisXmm=0}).Status!=SectionStatus.Passed,"T10 rejects stale T08 connectivity result when source revision identity changes");
var t10IslandBoundary=t10Boundary.Append(new ProjectionPrimitive{Id="island",Kind=ProjectionPrimitiveKind.Circle,Center=new(0,0),RadiusMm=1,RequirementId="island-r"}).ToArray();
var t10IslandLoops=t10Loops.Append(new SectionLoop{LoopId="island-material",PrimitiveIds=["island"],AreaMm2=Math.PI,IsVoid=false}).ToArray();
Check(SectionVerifier.Compare(t10Spec with{RequiredConnectivity=[]},T10Source() with{BoundaryPrimitives=t10IslandBoundary,Loops=t10IslandLoops},T10Model(t10IslandBoundary,t10IslandLoops),[],new(){CompareVisibleOnly=false,MirrorAxisXmm=0}).Status==SectionStatus.Passed,"T10 identical section with a material island uses direct-parent containment instead of hole-centroid counting");

// T11: question-driven observation is identity-bound, capability-gated and only proposes deterministic follow-up checks.
var t11Session=new ObservationSession();var t11RenderCalls=0;
var t11Directory=Path.Combine(Path.GetTempPath(),"auto-sw-t11-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(t11Directory);
var t11Request=ObservationSession.HolePositionQuestion("obs-hole-position","Is the hole axis at the source-defined position?","rev-1",t6ShaA,t6ShaA,t7Ref);
ObservationRenderArtifact T11Rendered(ObservationRequest request){t11RenderCalls++;var path=Path.Combine(t11Directory,"obs-hole.bin");File.WriteAllText(path,"render-"+t11RenderCalls);var sha=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));return new(){Success=true,OutputPath=path,OutputSha256=sha,ActualView=request.RequestedView,CameraIdentity="front:target-hole",CoverageIds=request.TargetGeometry is null?[]:[request.TargetGeometry.RefId]};}
var t11First=t11Session.Observe(t11Request,new(),T11Rendered);
Check(t11First.Status==ObservationStatus.Completed&&t11First.DerivedMeasurementRequests.Count==2&&t11First.DerivedMeasurementRequests.All(q=>q.Kind is MeasurementKind.AxisPointX or MeasurementKind.AxisPointY),"T11 hole-position doubt produces orthographic observation plus targeted T07 measurement queries");
var t11Cached=t11Session.Observe(t11Request with{RequestId="obs-hole-position-retry"},new(),_=>throw new Exception("cache miss"));
Check(t11Cached.Status==ObservationStatus.Reused&&t11Cached.Reused&&t11Cached.RequestId=="obs-hole-position-retry"&&t11RenderCalls==1&&t11Cached.Artifact?.OutputSha256==t11First.Artifact?.OutputSha256,"T11 same model/question/view with a new trace request id reuses the exact identity-bound artifact");
File.WriteAllText(t11First.Artifact!.OutputPath!,"tampered-after-cache");
var t11Recaptured=t11Session.Observe(t11Request with{RequestId="obs-hole-position-after-tamper"},new(),T11Rendered);
Check(t11Recaptured.Status==ObservationStatus.Completed&&!t11Recaptured.Reused&&t11RenderCalls==2&&t11Recaptured.Artifact?.OutputSha256!=t11First.Artifact.OutputSha256,"T11 changed cached artifact bytes invalidate reuse and force fresh evidence");
var t11Changed=t11Request with{RequestId="obs-hole-position-new-model",ModelSha256=t6ShaB,TargetGeometry=t7Ref with{ModelSha256=t6ShaB}};
var t11ChangedResult=t11Session.Observe(t11Changed,new(),_=>new(){Success=false,ActualView=ObservationViewKind.OrthographicFront,Message="injected renderer failure"});
Check(t11ChangedResult.Status==ObservationStatus.Failed&&!t11ChangedResult.Reused&&t11ChangedResult.Artifact is not {Success:true},"T11 changed model SHA cannot reuse an old screenshot when the current render fails");
var t11Unsupported=t11Session.Observe(new ObservationRequest{RequestId="obs-stepped",Question="Need a non-supported section",SourceRevisionId="rev-1",SourceSha256=t6ShaA,ModelSha256=t6ShaA,RequestedView=ObservationViewKind.FullPlaneSection,Section=t10Spec},new(),_=>throw new Exception("unsupported renderer should not run"));
Check(t11Unsupported.Status==ObservationStatus.Unsupported,"T11 unsupported section capability fails explicitly instead of substituting a normal screenshot");
var t11SectionOutput=Path.Combine(t11Directory,"wrong-section.json");File.WriteAllText(t11SectionOutput,"{}");
var t11SectionHash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(t11SectionOutput)));
var t11WrongSection=new ObservationSession().Observe(new ObservationRequest{RequestId="obs-section-mismatch",Question="Inspect A-A",SourceRevisionId="rev-1",SourceSha256=t6ShaA,ModelSha256=t6ShaA,RequestedView=ObservationViewKind.FullPlaneSection,Section=t10Spec},
    new(){SupportsFullPlaneSection=true},_=>new(){Success=true,OutputPath=t11SectionOutput,OutputSha256=t11SectionHash,ActualView=ObservationViewKind.FullPlaneSection,SectionPlaneFingerprint=new string('F',64)});
Check(t11WrongSection.Status!=ObservationStatus.Completed,"T11 renderer cannot certify a different fixed section plane with a merely nonempty fingerprint");
var t11Budget=new ObservationSession();var t11BudgetSpec=new ObservationBudget{MaxAttempts=1,MaxUniqueRequests=1,MaxRepeatsPerIdentity=1};
_ = t11Budget.Observe(t11Request with{RequestId="budget-a",Budget=t11BudgetSpec},new(),_=>new(){Success=false,ActualView=ObservationViewKind.OrthographicFront,Message="failure consumes budget"});
Check(t11Budget.Observe(t11Request with{RequestId="budget-b",Budget=t11BudgetSpec},new(),T11Rendered).Status==ObservationStatus.BudgetExceeded,"T11 observation attempts terminate at the explicit budget");

// T13: failures are localized only with source/operation lineage, and topology-token churn is not shape drift.
var t13Plan=new ModelingPlan{PlanId="t13-plan",Name="T13 synthetic",Operations=[
    new ProfileSketchOperation{Id="sketch",Name="source sketch",Primitives=[new CircleProfile{DiameterMm=10}]},
    new ExtrudeCutOperation{Id="hole-cut",Name="source hole",SketchId="sketch",EndCondition=ExtrudeEndCondition.ThroughAll,DependsOn=["sketch"]},
    new NativeFeatureOperation{Id="fillet",Name="downstream fillet",DependsOn=["hole-cut"],Options=new(){Kind=NativeFeatureKind.Fillet,RadiusMm=3}},
    new NativeFeatureOperation{Id="pattern",Name="downstream pattern",DependsOn=["fillet"],Options=new(){Kind=NativeFeatureKind.LinearPattern,Count=2,SpacingMm=20}},
    new ProfileSketchOperation{Id="other-sketch",Name="independent sketch",Primitives=[new CircleProfile{DiameterMm=4}]},
    new ExtrudeCutOperation{Id="other-cut",Name="independent cut",SketchId="other-sketch",EndCondition=ExtrudeEndCondition.ThroughAll,DependsOn=["other-sketch"]}
]};
var t13Ref=t7Ref with{OperationId="hole-cut"};
var t13Located=FailureLocator.Locate(t13Plan,new(){FailureId="wrong-dia",FailureClass=FailureClass.DimensionError,GeometryReferences=[t13Ref],EvidenceIds=["measurement:diameter"]});
Check(t13Located.Located&&t13Located.EarliestAffectedOperationId=="hole-cut"&&t13Located.DownstreamOperationIds.SequenceEqual(new[]{"hole-cut","fillet","pattern"}),"T13 located failure expands affected scope through downstream fillet/pattern consumers");
var t13Unlocated=FailureLocator.Locate(t13Plan,new(){FailureId="no-lineage",FailureClass=FailureClass.DimensionError,OperationIds=["hole-cut"]});
Check(!t13Unlocated.Located&&t13Unlocated.WhyUnlocated.Contains("source lineage",StringComparison.OrdinalIgnoreCase),"T13 missing source lineage stays unlocated instead of guessing a repair point");
var t13MultiRoot=FailureLocator.Locate(t13Plan,new(){FailureId="two-roots",FailureClass=FailureClass.DimensionError,SourceFactIds=["fact-hole","fact-other"],OperationIds=["hole-cut","other-cut"],EvidenceIds=["measurement-a","measurement-b"]});
Check(t13MultiRoot.Located&&t13MultiRoot.DownstreamOperationIds.SequenceEqual(new[]{"hole-cut","fillet","pattern","other-cut"}),"T13 multiple evidenced failure roots preserve the union of every downstream affected branch");
GeometryShapeRecord T13Shape(string id,string semantic,double radius,string token)=>new(){RecordId=id,SemanticId=semantic,TopologyToken=token,OperationId=semantic.StartsWith("hole",StringComparison.Ordinal)?"hole-cut":"sketch",SourceFactIds=["fact-"+semantic],Signature=T6Sig(radius,semantic=="hole-2"?40:20)};
var t13Base=new[]{T13Shape("b1","hole-1",5,"face-7"),T13Shape("b2","hole-2",5,"face-8"),T13Shape("b3","outer",20,"face-1")};
ModelDiffInput T13Diff(IReadOnlyList<GeometryShapeRecord> baseline,IReadOnlyList<GeometryShapeRecord> candidate,IReadOnlyList<string>? intended=null,IReadOnlyList<RequirementRecheck>? rechecks=null)=>new(){BaselineModelSha256=t6ShaA,CandidateModelSha256=t6ShaB,BaselineModelReopened=true,CandidateModelReopened=true,BaselineCaptureComplete=true,CandidateCaptureComplete=true,BaselineCaptureScopeIds=["supported-part-brep"],CandidateCaptureScopeIds=["supported-part-brep"],IntendedTargetIds=intended??[],BaselineGeometry=baseline,CandidateGeometry=candidate,UnaffectedRequirementRechecks=rechecks??[]};
var t13Local=ModelDiffAnalyzer.Compare(T13Diff(t13Base,[T13Shape("c1","hole-1",6,"face-17"),T13Shape("c2","hole-2",5,"face-18"),T13Shape("c3","outer",20,"face-11")],["hole-1"],[new("hole-2",RequirementCheckStatus.Passed,"recheck-2"),new("outer",RequirementCheckStatus.Passed,"recheck-o")]));
Check(t13Local.Status==ModelDiffStatus.Comparable&&!t13Local.HasUnexpectedImpact&&t13Local.Changes.Count(c=>c.Kind==GeometryShapeChangeKind.Changed)==1,"T13 one intended hole-diameter edit remains local while equivalent topology identity churn is ignored as shape failure");
var t13Collateral=ModelDiffAnalyzer.Compare(T13Diff(t13Base,[T13Shape("c1","hole-1",6,"face-17"),T13Shape("c2","hole-2",6,"face-18"),T13Shape("c3","outer",20,"face-11")],["hole-1"]));
Check(t13Collateral.HasUnexpectedImpact&&t13Collateral.UnexpectedChanges.Any(c=>c.SemanticId=="hole-2"),"T13 accidental change to every same-name/similar hole is reported as unexpected impact");
var t13InflatedTolerance=ModelDiffAnalyzer.Compare(T13Diff([T13Shape("b","hole-1",5,"face-7")],[T13Shape("c","hole-1",6,"face-8") with{Signature=T6Sig(6,20) with{RadiusToleranceMm=100}}]));
Check(t13InflatedTolerance.Changes.Single().Kind==GeometryShapeChangeKind.Changed,"T13 candidate-side tolerance inflation cannot hide a real shape change");
var t13IdentityOnly=ModelDiffAnalyzer.Compare(T13Diff([T13Shape("old","hole-1",5,"face-7")],[T13Shape("new","hole-1",5,"face-99")]));
Check(!t13IdentityOnly.HasUnexpectedImpact&&t13IdentityOnly.Changes.Single().Kind==GeometryShapeChangeKind.IdentityOnly,"T13 persistent/topology index change with equivalent bounded geometry is identity-only, not a shape failure");
var t13EmptyCapture=ModelDiffAnalyzer.Compare(new(){BaselineModelSha256=t6ShaA,CandidateModelSha256=t6ShaB,BaselineModelReopened=true,CandidateModelReopened=true,BaselineCaptureComplete=true,CandidateCaptureComplete=true,BaselineCaptureScopeIds=["supported-part-brep"],CandidateCaptureScopeIds=["supported-part-brep"]});
Check(t13EmptyCapture.Status==ModelDiffStatus.Incomparable,"T13 empty geometry inventories cannot certify a comparable no-collateral-change result");

// T12: review starts from the immutable required source-fact inventory and aggregates fail-closed evidence.
ReviewPacket T12Packet(params RequiredFact[] facts)=>new(){SourceSha256=t6ShaA,SourceRevisionId="rev-1",CandidateModelSha256=t6ShaA,RequiredFacts=facts};
var t12Empty=CoverageReviewer.Review(T12Packet(),[]);
Check(!t12Empty.FullPass&&t12Empty.Conclusion==CoverageConclusion.Unverifiable,"T12 empty required scope cannot pass");
var t12DiameterFact=new RequiredFact{FactId="fact-hole",SourceRevisionId="rev-1",SourceFactFingerprint=t7Requirement.SourceFactFingerprint!,RequirementFingerprint=DirectionalMeasurementVerifier.Fingerprint(t7Requirement),RequiredEvidenceKinds=[ReviewEvidenceKind.Measurement],Description="source hole diameter"};
var t12GoodEvidence=CoverageReviewer.FromMeasurement(DirectionalMeasurementVerifier.Compare(t7Query,t7Measured,t7Requirement),"measurement-good");
Check(CoverageReviewer.Review(T12Packet(t12DiameterFact),[t12GoodEvidence]).FullPass,"T12 correct fully supported source scope passes without an artificial approval gate");
var t12MissingSmallHole=CoverageReviewer.Review(T12Packet(t12DiameterFact,new(){FactId="fact-small-hole",SourceRevisionId="rev-1",SourceFactFingerprint=CoverageReviewer.SourceFactIdentity(t6ShaA,"rev-1","fact-small-hole"),RequirementFingerprint=new string('9',64),RequiredEvidenceKinds=[ReviewEvidenceKind.Measurement]}),[t12GoodEvidence]);
Check(!t12MissingSmallHole.FullPass&&t12MissingSmallHole.Items.Single(i=>i.FactId=="fact-small-hole").Status==RequirementCheckStatus.Unverifiable,"T12 source small hole omitted by the plan remains uncovered");
var t12SectionFact=new RequiredFact{FactId=t10Spec.SourceFactId,SourceRevisionId="rev-1",SourceFactFingerprint=CoverageReviewer.SourceFactIdentity(t6ShaA,"rev-1",t10Spec.SourceFactId),RequirementFingerprint=SectionVerifier.Fingerprint(t10Spec),RequiredEvidenceKinds=[ReviewEvidenceKind.Section]};
Check(!CoverageReviewer.Review(T12Packet(t12DiameterFact,t12SectionFact),[t12GoodEvidence]).FullPass,"T12 all executed checks passing cannot hide an unexecuted required section");
var t12Wrong=DirectionalMeasurementVerifier.Compare(t7Query,t7Measured with{ScalarValue=8},t7Requirement);
var t12ObservationEvidence=CoverageReviewer.FromObservation(t11First,"fact-hole","observation-hole");
var t12SelfConsistentWrong=CoverageReviewer.Review(T12Packet(t12DiameterFact),[CoverageReviewer.FromMeasurement(t12Wrong,"measurement-wrong-source"),t12ObservationEvidence]);
Check(t12SelfConsistentWrong.Conclusion==CoverageConclusion.Failed,"T12 visual/reviewer agreement cannot override a deterministic source-vs-model diameter failure");
var t12StaleEvidence=t12GoodEvidence with{EvidenceId="old-measurement",SourceRevisionId="rev-old"};
Check(!CoverageReviewer.Review(T12Packet(t12DiameterFact),[t12StaleEvidence]).FullPass,"T12 source revision change invalidates old evidence");
var t12SourceOnlyFact=new RequiredFact{FactId="fact-source-only",SourceRevisionId="rev-1",SourceFactFingerprint=CoverageReviewer.SourceFactIdentity(t6ShaA,"rev-1","fact-source-only"),RequirementFingerprint=new string('A',64),RequiredEvidenceKinds=[ReviewEvidenceKind.SourceFact]};
var t12SourceOnlyEvidence=new CoverageEvidence{EvidenceId="source-only",FactId="fact-source-only",SourceSha256=t6ShaA,SourceRevisionId="rev-1",SourceFactFingerprint=t12SourceOnlyFact.SourceFactFingerprint,RequirementFingerprint=t12SourceOnlyFact.RequirementFingerprint,Kind=ReviewEvidenceKind.SourceFact,Status=RequirementCheckStatus.Passed};
Check(!CoverageReviewer.Review(T12Packet(t12SourceOnlyFact),[t12SourceOnlyEvidence]).FullPass,"T12 source-fact existence alone cannot certify candidate-model compliance");
var t12WrongSourceEvidence=t12GoodEvidence with{EvidenceId="measurement-other-source",SourceSha256=t6ShaB};
Check(!CoverageReviewer.Review(T12Packet(t12DiameterFact),[t12WrongSourceEvidence]).FullPass,"T12 evidence from another source drawing SHA cannot be relabeled onto the current packet");
var t12ProjectionModel=T9Model(T9Arc("arc-model",5,90,"unused"),new(){Id="extra-line",Kind=ProjectionPrimitiveKind.Line,Start=new(20,0),End=new(30,0),LineStyle="visible"});
var t12ProjectionReport=ProjectionVerifier.Compare(t9Source,t12ProjectionModel,new(){CompareVisibleOnly=false,MirrorAxisXmm=0});
var t12ProjectionFact=new RequiredFact{FactId="fact-arc",SourceRevisionId="rev-1",SourceFactFingerprint=CoverageReviewer.SourceFactIdentity(t6ShaA,"rev-1","fact-arc"),RequirementFingerprint=CoverageReviewer.ProjectionRequirementIdentity(t12ProjectionReport,"rev-1","arc-r"),RequiredEvidenceKinds=[ReviewEvidenceKind.Projection]};
var t12ProjectionEvidence=CoverageReviewer.FromProjection(t12ProjectionReport,"rev-1",new Dictionary<string,string>{{"arc-r","fact-arc"}},"projection-extra");
Check(!t12ProjectionReport.Passed&&!CoverageReviewer.Review(T12Packet(t12ProjectionFact),t12ProjectionEvidence).FullPass,"T12 failed projection report with extra geometry remains globally blocking after per-requirement adaptation");
var t12ProjectionRevisionRelabelRejected=false;try{_ = CoverageReviewer.FromProjection(t12ProjectionReport,"rev-old",new Dictionary<string,string>{{"arc-r","fact-arc"}},"projection-relabel");}catch(ArgumentException){t12ProjectionRevisionRelabelRejected=true;}
Check(t12ProjectionRevisionRelabelRejected,"T12 projection adapter rejects caller attempts to relabel a producer-owned source revision");
var t12ProjectionFactRelabelRejected=false;try{_ = CoverageReviewer.FromProjection(t12ProjectionReport,"rev-1",new Dictionary<string,string>{{"arc-r","fact-other"}},"projection-relabel");}catch(ArgumentException){t12ProjectionFactRelabelRejected=true;}
Check(t12ProjectionFactRelabelRejected,"T12 projection adapter rejects caller attempts to remap a producer-owned fact identity");

// T14: local repair changes only permitted modeling expression and requires T12+T13 evidence before execution.
var t14EdgeSig=new GeometrySignature{EntityKind=EntityKind.Edge,GeometryKind=GeometryKind.Line,AnchorMm=new(0,5,0),Direction=new(1,0,0)};
var t14EdgeRef=new GeometryRef{RefId="fillet-edge",DocumentId="DOC-A",ModelSha256=t6ShaA,EntityKind=EntityKind.Edge,GeometryKind=GeometryKind.Line,OperationId="fillet",SourceFactIds=["fact-fillet"],SourceRevisionId="rev-1",Signature=t14EdgeSig};
var t14EdgeResolution=GeometryRefResolver.Resolve(t14EdgeRef,t6Doc,[new(){CandidateId="correct-edge",FeatureId="base",NativePersistentReference="edge-new",Signature=t14EdgeSig}]);
var t14Plan=new ModelingPlan{PlanId="t14-plan",Name="T14 synthetic",SourceText="R3 fillet and through hole",DrawingSourceSha256=t6ShaA,Operations=[
    new ProfileSketchOperation{Id="sketch",Name="source sketch",Primitives=[new CircleProfile{DiameterMm=10}]},
    new ExtrudeCutOperation{Id="through-cut",Name="through source",SketchId="sketch",EndCondition=ExtrudeEndCondition.ThroughAll,ReverseDirection=true,DependsOn=["sketch"]},
    new NativeFeatureOperation{Id="fillet",Name="R3 source fillet",DependsOn=["through-cut"],Options=new(){Kind=NativeFeatureKind.Fillet,RadiusMm=3,Selections=[new(){Kind=EntityKind.Edge,Geometry=GeometryKind.Line,PositionMm=new(0,.001,0)}]}}
]};
var t14ScopeFingerprint=new string('D',64);
CoverageReport T14CoverageFor(ModelingPlan plan)=>new(){SourceSha256=t6ShaA,SourceRevisionId="rev-1",CandidateModelSha256=t6ShaA,CandidatePlanFingerprint=ModelingPlanIdentity.Fingerprint(plan),RequiredScopeFingerprint=t14ScopeFingerprint,RequiredFactIds=["fact-fillet","fact-through"],Items=[new(){FactId="fact-fillet",Status=RequirementCheckStatus.Failed},new(){FactId="fact-through",Status=RequirementCheckStatus.Failed}],Conclusion=CoverageConclusion.Failed,ConclusionReason="repair input"};
var t14Coverage=T14CoverageFor(t14Plan);
var t14FilletLocation=FailureLocator.Locate(t14Plan,new(){FailureId="fillet-selection",FailureClass=FailureClass.NativeKernelFailure,SourceFactIds=["fact-fillet"],OperationIds=["fillet"],EvidenceIds=["native-failure"]});
var t14Session=new ConstrainedRepairSession();var t14FailHash=new string('C',64);var t14EvidenceHash=new string('D',64);
var t14FilletAttempt=t14Session.Prepare(t14Plan,new(){RequestId="repair-fillet",FailureId="fillet-selection",Kind=RepairKind.FilletSelection,SourceRevisionId="rev-1",FailureFingerprint=t14FailHash,NewEvidenceFingerprint=t14EvidenceHash,TargetOperationId="fillet",GeometryResolutions=[t14EdgeResolution]},t14Coverage,t14FilletLocation);
var t14FixedFillet=(NativeFeatureOperation)t14FilletAttempt.CandidatePlan!.Operations.Single(o=>o.Id=="fillet");
Check(t14FilletAttempt.Status==RepairAttemptStatus.Prepared&&t14FixedFillet.Options.RadiusMm==3&&t14FixedFillet.Options.Selections.Single().PersistentReference=="edge-new","T14 fillet wrong-edge repair reselects uniquely proven edge while locking R3 radius");
var t14OriginalFillet=(NativeFeatureOperation)t14Plan.Operations.Single(o=>o.Id=="fillet");var t14IllegalRadius=t14FixedFillet with{Options=t14FixedFillet.Options with{RadiusMm=1}};
Check(!RepairPolicy.IsAllowedOperationMutation(t14OriginalFillet,t14IllegalRadius,RepairKind.FilletSelection),"T14 R3 cannot be weakened to R1 even if that would make the feature build");
Check(t14Session.Prepare(t14Plan,new(){RequestId="repair-fillet-repeat",FailureId="fillet-selection",Kind=RepairKind.FilletSelection,SourceRevisionId="rev-1",FailureFingerprint=t14FailHash,NewEvidenceFingerprint=t14EvidenceHash,TargetOperationId="fillet",GeometryResolutions=[t14EdgeResolution]},t14Coverage,t14FilletLocation).Status==RepairAttemptStatus.DuplicateFailure,"T14 repeated identical failure without new evidence stops inside the repair budget");
var t14CutLocation=FailureLocator.Locate(t14Plan,new(){FailureId="reverse-through",FailureClass=FailureClass.TopologyConnectivityError,SourceFactIds=["fact-through"],OperationIds=["through-cut"],EvidenceIds=["T08:failed"]});
var t14Direction=new ConstrainedRepairSession().Prepare(t14Plan,new(){RequestId="repair-direction",FailureId="reverse-through",Kind=RepairKind.ThroughDirection,SourceRevisionId="rev-1",FailureFingerprint=new string('E',64),NewEvidenceFingerprint=new string('F',64),TargetOperationId="through-cut",DesiredReverseDirection=false},t14Coverage,t14CutLocation);
var t14FixedCut=(ExtrudeCutOperation)t14Direction.CandidatePlan!.Operations.Single(o=>o.Id=="through-cut");
Check(t14Direction.Status==RepairAttemptStatus.Prepared&&!t14FixedCut.ReverseDirection&&t14FixedCut.EndCondition==ExtrudeEndCondition.ThroughAll&&t14Direction.RequiredPostChecks.Contains("T08:connectivity"),"T14 reverse through-cut repair changes direction only and mandates T08 connectivity recheck");
var t14Ambiguous=t14EdgeResolution with{Status=GeometryRefResolutionStatus.Ambiguous,CandidateIds=["a","b"]};
var t14AmbiguousAttempt=new ConstrainedRepairSession().Prepare(t14Plan,new(){RequestId="repair-ambiguous",FailureId="fillet-selection",Kind=RepairKind.GeometryRefRebind,SourceRevisionId="rev-1",FailureFingerprint=new string('1',64),NewEvidenceFingerprint=new string('2',64),TargetOperationId="fillet",GeometryResolutions=[t14Ambiguous]},t14Coverage,t14FilletLocation);
Check(t14AmbiguousAttempt.Status==RepairAttemptStatus.Rejected&&t14AmbiguousAttempt.CandidatePlan is null,"T14 ambiguous stale GeometryRef is never rebound by arbitrary first match");
var t14RebindAttempt=new ConstrainedRepairSession().Prepare(t14Plan,new(){RequestId="repair-rebind",FailureId="fillet-selection",Kind=RepairKind.GeometryRefRebind,SourceRevisionId="rev-1",FailureFingerprint=new string('5',64),NewEvidenceFingerprint=new string('6',64),TargetOperationId="fillet",GeometryResolutions=[t14EdgeResolution]},t14Coverage,t14FilletLocation);
Check(t14RebindAttempt.Status==RepairAttemptStatus.Prepared&&((NativeFeatureOperation)t14RebindAttempt.CandidatePlan!.Operations.Single(o=>o.Id=="fillet")).Options.Selections.Single().PersistentReference=="edge-new","T14 unique T06 GeometryRef proof supports a selection-only rebind with all feature parameters locked");
var t14BlindPlan=t14Plan with{Operations=t14Plan.Operations.Select(o=>o.Id=="through-cut"?((ExtrudeCutOperation)o) with{EndCondition=ExtrudeEndCondition.Blind,DepthMm=10}:o).ToArray()};
var t14BlindLocation=FailureLocator.Locate(t14BlindPlan,new(){FailureId="blind-direction",FailureClass=FailureClass.TopologyConnectivityError,SourceFactIds=["fact-through"],OperationIds=["through-cut"]});
var t14BlindAttempt=new ConstrainedRepairSession().Prepare(t14BlindPlan,new(){RequestId="repair-blind",FailureId="blind-direction",Kind=RepairKind.ThroughDirection,SourceRevisionId="rev-1",FailureFingerprint=new string('7',64),NewEvidenceFingerprint=new string('8',64),TargetOperationId="through-cut",DesiredReverseDirection=false},T14CoverageFor(t14BlindPlan),t14BlindLocation);
Check(t14BlindAttempt.Status==RepairAttemptStatus.Rejected,"T14 direction rule cannot silently convert or reinterpret a blind cut as ThroughAll");
var t14OldSourceAttempt=new ConstrainedRepairSession().Prepare(t14Plan,new(){RequestId="repair-old-source",FailureId="fillet-selection",Kind=RepairKind.FilletSelection,SourceRevisionId="rev-old",FailureFingerprint=new string('3',64),NewEvidenceFingerprint=new string('4',64),TargetOperationId="fillet",GeometryResolutions=[t14EdgeResolution]},t14Coverage,t14FilletLocation);
Check(t14OldSourceAttempt.Status==RepairAttemptStatus.Rejected,"T14 source revision change prevents reuse of an old repair attempt/evidence chain");
CoverageReport T14PostCoverage(RepairAttempt attempt,string modelSha)=>new(){SourceSha256=attempt.SourceSha256,SourceRevisionId=attempt.SourceRevisionId,CandidateModelSha256=modelSha,CandidatePlanFingerprint=attempt.AfterPlanFingerprint,RequiredScopeFingerprint=attempt.FrozenRequiredScopeFingerprint,RequiredFactIds=attempt.FrozenRequiredFactIds,Items=attempt.FrozenRequiredFactIds.Select(id=>new CoverageItem{FactId=id,Status=RequirementCheckStatus.Passed,EvidenceIds=["post:"+id]}).ToArray(),Conclusion=CoverageConclusion.Passed,ConclusionReason="identity-bound post repair source coverage"};
RepairExecutionEvidence T14Receipt(RepairAttempt attempt,string candidateSha)=>new(){ExecutionId="fixture:"+attempt.RequestId,AttemptRequestId=attempt.RequestId,AttemptIndex=attempt.AttemptIndex,BeforePlanFingerprint=attempt.BeforePlanFingerprint,AfterPlanFingerprint=attempt.AfterPlanFingerprint!,SourceSha256=attempt.SourceSha256,SourceRevisionId=attempt.SourceRevisionId,BaselineModelSha256=attempt.BaselineModelSha256,CandidateModelSha256=candidateSha,CandidateModelReopened=true,FrozenRequiredScopeFingerprint=attempt.FrozenRequiredScopeFingerprint,FrozenRequiredFactIds=attempt.FrozenRequiredFactIds,FrozenAffectedFactIds=attempt.FrozenAffectedFactIds,EvidenceMode="synthetic_fixture"};
RepairCheckResult T14Check(RepairAttempt attempt,RepairExecutionEvidence receipt,string id,RequirementCheckStatus status,string evidence)=>new(id,status,evidence,receipt.SourceSha256,receipt.SourceRevisionId,receipt.CandidateModelSha256,receipt.AfterPlanFingerprint,attempt.RequestId);
var t14PostDiff=ModelDiffAnalyzer.Compare(T13Diff([T13Shape("post-base","outer",20,"face-1")],[T13Shape("post-candidate","outer",20,"face-2")]));
var t14FilletReceipt=T14Receipt(t14FilletAttempt,t6ShaB);var t14PostCoverage=T14PostCoverage(t14FilletAttempt,t6ShaB);
var t14PostValid=ConstrainedRepairSession.ValidatePostRepair(t14FilletAttempt,t14FilletReceipt,t14PostCoverage,t14PostDiff,[T14Check(t14FilletAttempt,t14FilletReceipt,"unaffected-requirement-sample",RequirementCheckStatus.Passed,"sample-pass")]);
Check(t14PostValid.Status==RepairValidationStatus.Passed,"T14 prepared candidate is accepted only after T12 coverage, T13 collateral diff and unaffected-requirement sampling pass");
Check(ConstrainedRepairSession.ValidatePostRepair(t14FilletAttempt,t14PostCoverage,t14PostDiff,[]).Status==RepairValidationStatus.Unverifiable,"T14 post-repair acceptance without an execution receipt is never passed");
var t14ShrinkCoverage=new CoverageReport{SourceSha256=t6ShaA,SourceRevisionId="rev-1",CandidateModelSha256=t6ShaB,CandidatePlanFingerprint=t14FilletAttempt.AfterPlanFingerprint,RequiredScopeFingerprint=t14FilletAttempt.FrozenRequiredScopeFingerprint,RequiredFactIds=["fact-unrelated"],Items=[new(){FactId="fact-unrelated",Status=RequirementCheckStatus.Passed}],Conclusion=CoverageConclusion.Passed};
Check(ConstrainedRepairSession.ValidatePostRepair(t14FilletAttempt,t14FilletReceipt,t14ShrinkCoverage,t14PostDiff,[T14Check(t14FilletAttempt,t14FilletReceipt,"unaffected-requirement-sample",RequirementCheckStatus.Passed,"sample-pass")]).Status!=RepairValidationStatus.Passed,"T14 post-repair coverage cannot shrink away the frozen affected/source fact scope");
var t14DuplicateCoverage=t14PostCoverage with{Items=[new(){FactId="fact-fillet",Status=RequirementCheckStatus.Passed,EvidenceIds=["dup-a"]},new(){FactId="fact-fillet",Status=RequirementCheckStatus.Passed,EvidenceIds=["dup-b"]}]};
Check(ConstrainedRepairSession.ValidatePostRepair(t14FilletAttempt,t14FilletReceipt,t14DuplicateCoverage,t14PostDiff,[T14Check(t14FilletAttempt,t14FilletReceipt,"unaffected-requirement-sample",RequirementCheckStatus.Passed,"sample-pass")]).Status==RepairValidationStatus.Unverifiable,"T14 duplicate coverage item cannot hide a missing frozen required fact");
var t14MissingCoverage=t14PostCoverage with{Items=t14PostCoverage.Items.Where(item=>item.FactId!="fact-through").ToArray()};
Check(ConstrainedRepairSession.ValidatePostRepair(t14FilletAttempt,t14FilletReceipt,t14MissingCoverage,t14PostDiff,[T14Check(t14FilletAttempt,t14FilletReceipt,"unaffected-requirement-sample",RequirementCheckStatus.Passed,"sample-pass")]).Status==RepairValidationStatus.Unverifiable,"T14 missing coverage item is rejected even when the coverage header still declares the full frozen scope");
var t14UnknownCoverage=t14PostCoverage with{Items=[new(){FactId="fact-fillet",Status=RequirementCheckStatus.Passed,EvidenceIds=["known"]},new(){FactId="fact-unknown",Status=RequirementCheckStatus.Passed,EvidenceIds=["unknown"]}]};
Check(ConstrainedRepairSession.ValidatePostRepair(t14FilletAttempt,t14FilletReceipt,t14UnknownCoverage,t14PostDiff,[T14Check(t14FilletAttempt,t14FilletReceipt,"unaffected-requirement-sample",RequirementCheckStatus.Passed,"sample-pass")]).Status==RepairValidationStatus.Unverifiable,"T14 unknown coverage item cannot substitute for a frozen required fact");
var t14ReorderedCoverage=t14PostCoverage with{Items=t14PostCoverage.Items.Reverse().ToArray()};
Check(ConstrainedRepairSession.ValidatePostRepair(t14FilletAttempt,t14FilletReceipt,t14ReorderedCoverage,t14PostDiff,[T14Check(t14FilletAttempt,t14FilletReceipt,"unaffected-requirement-sample",RequirementCheckStatus.Passed,"sample-pass")]).Status==RepairValidationStatus.Passed,"T14 complete coverage items may be reordered without weakening set completeness");
Check(ConstrainedRepairSession.ValidatePostRepair(t14FilletAttempt,t14FilletReceipt,t14PostCoverage with{RequiredScopeFingerprint=new string('E',64)},t14PostDiff,[T14Check(t14FilletAttempt,t14FilletReceipt,"unaffected-requirement-sample",RequirementCheckStatus.Passed,"sample-pass")]).Status!=RepairValidationStatus.Passed,"T14 post-repair coverage cannot keep fact IDs while changing the frozen requirement semantics");
var t14WrongExecution=T14Receipt(t14FilletAttempt,new string('B',64)) with{BaselineModelSha256=new string('9',64)};
Check(ConstrainedRepairSession.ValidatePostRepair(t14FilletAttempt,t14WrongExecution,t14PostCoverage,t14PostDiff,[T14Check(t14FilletAttempt,t14WrongExecution,"unaffected-requirement-sample",RequirementCheckStatus.Passed,"wrong-execution")]).Status!=RepairValidationStatus.Passed,"T14 coverage and diff from another baseline/candidate execution cannot be rebound to this attempt");
var t14ExtraFailure=ConstrainedRepairSession.ValidatePostRepair(t14FilletAttempt,t14FilletReceipt,t14PostCoverage,t14PostDiff,[T14Check(t14FilletAttempt,t14FilletReceipt,"unaffected-requirement-sample",RequirementCheckStatus.Passed,"sample-pass"),T14Check(t14FilletAttempt,t14FilletReceipt,"extra-actual-measurement",RequirementCheckStatus.Failed,"extra-failure")]);
Check(t14ExtraFailure.Status==RepairValidationStatus.Failed,"T14 any additional identity-bound deterministic failure blocks success even when minimum named checks pass");
var t14DuplicatePassedChecks=ConstrainedRepairSession.ValidatePostRepair(t14FilletAttempt,t14FilletReceipt,t14PostCoverage,t14PostDiff,[T14Check(t14FilletAttempt,t14FilletReceipt,"unaffected-requirement-sample",RequirementCheckStatus.Passed,"dup-check-a"),T14Check(t14FilletAttempt,t14FilletReceipt,"unaffected-requirement-sample",RequirementCheckStatus.Passed,"dup-check-b")]);
Check(t14DuplicatePassedChecks.Status==RepairValidationStatus.Unverifiable&&t14DuplicatePassedChecks.MissingOrFailedChecks.Contains("post-check-evidence-invalid"),"T14 duplicate required Passed checks return structured unverifiable instead of throwing or choosing one");
var t14DuplicateMixedChecks=ConstrainedRepairSession.ValidatePostRepair(t14FilletAttempt,t14FilletReceipt,t14PostCoverage,t14PostDiff,[T14Check(t14FilletAttempt,t14FilletReceipt,"unaffected-requirement-sample",RequirementCheckStatus.Passed,"dup-mixed-a"),T14Check(t14FilletAttempt,t14FilletReceipt,"unaffected-requirement-sample",RequirementCheckStatus.Failed,"dup-mixed-b")]);
Check(t14DuplicateMixedChecks.Status==RepairValidationStatus.Failed&&t14DuplicateMixedChecks.MissingOrFailedChecks.Contains("post-check-evidence-invalid"),"T14 duplicate Passed/Failed required checks return structured failure and preserve deterministic failure priority");
var t14DirectionReceipt=T14Receipt(t14Direction,t6ShaB);var t14DirectionCoverage=T14PostCoverage(t14Direction,t6ShaB);
var t14PostBad=ConstrainedRepairSession.ValidatePostRepair(t14Direction,t14DirectionReceipt,t14DirectionCoverage,t14PostDiff,[T14Check(t14Direction,t14DirectionReceipt,"T08:connectivity",RequirementCheckStatus.Failed,"through-still-bad"),T14Check(t14Direction,t14DirectionReceipt,"unaffected-requirement-sample",RequirementCheckStatus.Passed,"sample-pass")]);
Check(t14PostBad.Status==RepairValidationStatus.Failed,"T14 direction edit cannot be accepted when post-repair T08 connectivity still fails");

// T15: checkpoint reuse is source/prefix/evidence identity bound; pause never submits a new COM operation.
var t15Plan=t14Plan with{Recovery=new(){Enabled=true,SourceRevisionId="rev-1"}};
var t15Geometry=new GeometrySnapshot(1,6,12,1000,600,new SpatialPoint(0,0,0),new BoundingBoxSpec(10,10,10));
var t15Environment=new CheckpointEnvironmentIdentity{CompilerVersion="compiler-1",ExecutorVersion="executor-1",VerifierVersion="verifier-1",RulesFingerprint="rules-1",SolidWorksRevision="34.0"};
var t15Checkpoint=new ModelingCheckpointManifest
{
    NativePath="fixture.SLDPRT",NativeSha256=t6ShaA,OriginalPlan=t15Plan,SourceSha256=t6ShaA,SourceRevisionId="rev-1",
    TypedPlanFingerprint=ModelingRecovery.TypedPlanFingerprint(t15Plan),CompletedPrefixFingerprint=ModelingRecovery.PrefixFingerprint(t15Plan,2),
    CompletedOperationCount=2,CompletedOperationIds=t15Plan.Operations.Take(2).Select(o=>o.Id).ToArray(),FeatureReferences=[],Geometry=t15Geometry,
    ModelReopened=true,WriteState=CheckpointWriteState.Complete,Environment=t15Environment,
    EvidenceStates=[new(){EvidenceId="T12:coverage",Producer="T12",Status="passed",SourceSha256=t6ShaA,SourceRevisionId="rev-1",ModelSha256=t6ShaA,PlanFingerprint=ModelingRecovery.TypedPlanFingerprint(t15Plan),RequiredScopeFingerprint=t14ScopeFingerprint,ModelReopened=true}]
};
var t15SuffixChanged=t15Plan with{Operations=t15Plan.Operations.Select(o=>o.Id=="fillet"?((NativeFeatureOperation)o) with{Options=((NativeFeatureOperation)o).Options with{TangentPropagation=false}}:o).ToArray()};
var t15SuffixDecision=ModelingRecovery.DecideResume(t15SuffixChanged,t15Checkpoint);
Check(t15SuffixDecision.CanResume&&t15SuffixDecision.ReusedOperationIds.SequenceEqual(["sketch","through-cut"])&&t15SuffixDecision.ReplayedOperationIds.SequenceEqual(["fillet"])&&t15SuffixDecision.ReusedEvidenceIds.Count==0,"T15 later-plan change reuses the unchanged operation prefix but revalidates plan-bound evidence");
var t15PrefixChanged=t15Plan with{Operations=t15Plan.Operations.Select(o=>o.Id=="through-cut"?((ExtrudeCutOperation)o) with{ReverseDirection=false}:o).ToArray()};
var t15PrefixDecision=ModelingRecovery.DecideResume(t15PrefixChanged,t15Checkpoint);
Check(t15PrefixDecision.RequiresCleanRebuild&&t15PrefixDecision.ReusedOperationIds.Count==0&&t15PrefixDecision.InvalidReuseCount>0,"T15 changed operation inside the only saved native checkpoint cannot pretend the later file is an earlier snapshot");
var t15NewRevision=t15Plan with{Recovery=t15Plan.Recovery with{SourceRevisionId="rev-2"}};
Check(ModelingRecovery.DecideResume(t15NewRevision,t15Checkpoint).RequiresCleanRebuild,"T15 source revision change rejects the old checkpoint prefix");
var t15NewSource=t15Plan with{DrawingSourceSha256=t6ShaB};
Check(ModelingRecovery.DecideResume(t15NewSource,t15Checkpoint).RequiresCleanRebuild,"T15 source SHA change rejects the old checkpoint prefix");
Check(ModelingRecovery.DecideResume(t15Plan,t15Checkpoint,currentEnvironment:t15Environment with{ExecutorVersion="executor-2"}).RequiresCleanRebuild,"T15 executor/compiler/SolidWorks environment change invalidates native prefix reuse instead of assuming compatibility");
var t15VerifierChanged=ModelingRecovery.DecideResume(t15Plan,t15Checkpoint,currentEnvironment:t15Environment with{VerifierVersion="verifier-2"});
Check(t15VerifierChanged.CanResume&&t15VerifierChanged.ReusedOperationIds.Count==2&&t15VerifierChanged.ReusedEvidenceIds.Count==0&&t15VerifierChanged.ReverificationEvidenceIds.Contains("T12:coverage"),"T15 verifier-only version change can reuse native prefix but must rerun old validation evidence");
var t15Incomplete=t15Checkpoint with{WriteState=CheckpointWriteState.Writing};
Check(ModelingRecovery.DecideResume(t15Plan,t15Incomplete).RequiresCleanRebuild,"T15 half-written checkpoint never enters the reusable success path");
var t15Uncertain=ModelingRecovery.DecideResume(t15Plan,t15Checkpoint,InFlightOperationState.Uncertain,"fillet");
Check(t15Uncertain.RequiresCleanRebuild&&t15Uncertain.InvalidReuseCount>0&&t15Uncertain.InFlightHandling.Contains("probe",StringComparison.Ordinal),"T15 uncertain in-flight COM outcome requires actual-state probing and is never blindly replayed");
var t15Pause=new PauseExecutionGate();
Check(t15Pause.TryBeginOperation("fillet"),"T15 pause gate permits operation submission before pause");
t15Pause.RequestPause();
Check(t15Pause.Snapshot().InFlightState==InFlightOperationState.Uncertain,"T15 pause requested during COM marks the in-flight outcome uncertain until the call returns");
t15Pause.CompleteOperation("fillet",true);
Check(!t15Pause.TryBeginOperation("next")&&t15Pause.Snapshot().InFlightOperationId is null,"T15 after in-flight COM reaches a committed boundary pause blocks every subsequent operation submission");

// T16: cache freshness is declared-input based and artifact-type specific.
ArtifactDependency T16Dep(string name,string value)=>new(name,value);
var t16NativeDeps=new[]{T16Dep("source.sha256",t6ShaA),T16Dep("source.revision","rev-1"),T16Dep("plan.fingerprint","plan-a"),T16Dep("compiler.version","compiler-1"),T16Dep("executor.version","executor-1"),T16Dep("solidworks.version","sw-2026")};
var t16Native=new CachedArtifact{ArtifactId="native-a",Kind=CacheArtifactKind.NativeModel,SourceRevisionId="rev-1",RequestSequence=7,Dependencies=t16NativeDeps};
var t16NativeCurrent=new FreshnessContext{SourceRevisionId="rev-1",CurrentRequestSequence=7,Dependencies=[..t16NativeDeps,T16Dep("validator.version","validator-2")]};
Check(ArtifactFreshness.Evaluate(t16Native,t16NativeCurrent).CacheHit,"T16 validator-only change does not rebuild an unchanged native model whose declared inputs are unchanged");
var t16CoverageDeps=new[]{T16Dep("source.sha256",t6ShaA),T16Dep("source.revision","rev-1"),T16Dep("model.sha256",t6ShaB),T16Dep("plan.fingerprint","plan-a"),T16Dep("required_scope.fingerprint",t14ScopeFingerprint),T16Dep("validator.version","validator-1"),T16Dep("model.reopened","true")};
var t16Coverage=new CachedArtifact{ArtifactId="coverage-a",Kind=CacheArtifactKind.CoverageReport,SourceRevisionId="rev-1",RequestSequence=7,Dependencies=t16CoverageDeps};
var t16CoverageChangedValidator=new FreshnessContext{SourceRevisionId="rev-1",CurrentRequestSequence=7,Dependencies=t16CoverageDeps.Select(d=>d.Name=="validator.version"?d with{Fingerprint="validator-2"}:d).ToArray()};
var t16CoverageDecision=ArtifactFreshness.Evaluate(t16Coverage,t16CoverageChangedValidator);
Check(!t16CoverageDecision.CacheHit&&t16CoverageDecision.InvalidationReasons.Any(r=>r.Contains("validator.version",StringComparison.Ordinal)),"T16 validator version change invalidates old verification even when the model is unchanged");
var t16EditDeps=new[]{T16Dep("model.sha256",t6ShaB),T16Dep("feature_tree.fingerprint","tree-a"),T16Dep("executor.version","executor-1"),T16Dep("solidworks.version","sw-2026"),T16Dep("model.reopened","true")};
var t16Edit=new CachedArtifact{ArtifactId="editability-a",Kind=CacheArtifactKind.NativeEditability,SourceRevisionId="rev-1",RequestSequence=7,Dependencies=t16EditDeps};
var t16EditChangedTree=new FreshnessContext{SourceRevisionId="rev-1",CurrentRequestSequence=7,Dependencies=t16EditDeps.Select(d=>d.Name=="feature_tree.fingerprint"?d with{Fingerprint="tree-b"}:d).ToArray()};
Check(!ArtifactFreshness.Evaluate(t16Edit,t16EditChangedTree).CacheHit,"T16 identical geometry/model identity cannot reuse native-editability evidence after the feature tree identity changes");
var t16MissingScope=t16Coverage with{Dependencies=t16CoverageDeps.Where(d=>d.Name!="required_scope.fingerprint").ToArray()};
Check(!ArtifactFreshness.Evaluate(t16MissingScope,new(){SourceRevisionId="rev-1",CurrentRequestSequence=7,Dependencies=t16CoverageDeps}).CacheHit,"T16 artifact that never declared a mandatory dependency is not retroactively treated as fresh");
Check(!ArtifactFreshness.CanPublishCompletion(t16Native with{RequestSequence=6},"rev-1",7,out var t16LateReason)&&t16LateReason.Contains("older request",StringComparison.Ordinal),"T16 late completion from an older request cannot overwrite the current artifact");
Check(!ArtifactFreshness.CanPublishCompletion(t16Native,"rev-2",7,out _),"T16 completion from an older source revision cannot publish into the new revision");
Check(ArtifactFreshness.Evaluate(t16Native,t16NativeCurrent with{CurrentRequestSequence=8}).CacheHit,"T16 a later equivalent request reuses an already published artifact when every declared dependency is unchanged");
var t16Store=new ArtifactCacheStore();t16Store.MarkCurrent("native","rev-1",7);
Check(t16Store.TryPublish("native",t16Native,out _),"T16 current request publishes its artifact through the atomic store boundary");
t16Store.MarkCurrent("native","rev-1",8);
Check(!t16Store.TryPublish("native",t16Native with{RequestSequence=7,ArtifactId="late-native"},out _)&&
      t16Store.GetPublished("native")?.ArtifactId=="native-a","T16 older in-flight completion cannot overwrite the current slot after generation advances");

// T17: finite revolved families preserve axis/diameter/axial source semantics and native editability.
var t17AxisRef=new GeometryRef{RefId="shaft-axis",DocumentId="DOC-SHAFT",ModelSha256=t6ShaB,EntityKind=EntityKind.Axis,GeometryKind=GeometryKind.Line,
    SourceFactIds=["fact-axis"],SourceRevisionId="rev-1",Signature=new(){EntityKind=EntityKind.Axis,GeometryKind=GeometryKind.Line,AnchorMm=new(0,0,0),Direction=new(1,0,0)}};
var t17Scope=new string('7',64);var t17ViewFingerprint=new string('8',64);var t17ConnectivityFingerprint=new string('9',64);
Stage4BoundCheckEvidence T17Evidence(string id,string fingerprint)=>new(){CheckId=id,Status=RequirementCheckStatus.Passed,EvidenceId="evidence:"+id,SourceSha256=t6ShaA,SourceRevisionId="rev-1",ModelSha256=t6ShaB,RequiredScopeFingerprint=t17Scope,RequirementFingerprint=fingerprint,ModelReopened=true,CaptureComplete=true};
var t17Profile=new RevolvedFamilyProfile{ProfileId="shaft-basic",SourceSha256=t6ShaA,SourceRevisionId="rev-1",AxisReference=t17AxisRef,
    RequiredScopeFingerprint=t17Scope,CheckRequirementFingerprints=new Dictionary<string,string>{{"T09:front",t17ViewFingerprint}},
    EditabilityProbes=[new("drive-diameter","D1@Revolve",32)],
    Kind=RevolvedFamilyKind.SteppedShaft,AxisOriginMm=new(0,0,0),AxisDirection=new(1,0,0),
    Steps=[new(){SourceFactId="step-30",AxialStartMm=0,AxialLengthMm=20,RadialValueMm=30},new(){SourceFactId="step-r10",AxialStartMm=20,AxialLengthMm=40,RadialValueMm=10,RadialSemantic=RadialDimensionSemantic.Radius}],
    RequiredViewCheckIds=["T09:front"]};
var t17Actual=new RevolvedFamilyObservation{SourceSha256=t6ShaA,SourceRevisionId="rev-1",ModelSha256=t6ShaB,ModelReopened=true,
    NativeFeatureKind=NativeFeatureKind.RevolveBoss,NativeDrivingDimensionsEditable=true,RebuildSucceeded=true,AxisOriginMm=new(0,0,0),AxisDirection=new(-1,0,0),
    Steps=[new(0,20,30),new(20,40,20)],CheckEvidence=[T17Evidence("T09:front",t17ViewFingerprint)]};
Check(RevolvedFamilyVerifier.Evaluate(t17Profile,t17Actual).Passed,"T17 stepped shaft passes with source diameter/radius semantics, coaxial axis, required view and editable native revolve");
Check(!RevolvedFamilyVerifier.Evaluate(t17Profile,t17Actual with{Steps=[new(0,20,30),new(20,40,10)]}).Passed,"T17 source R10 interpreted as diameter 10 is detected as a factor-two radial error");
Check(!RevolvedFamilyVerifier.Evaluate(t17Profile,t17Actual with{Steps=[new(0,40,30),new(40,20,20)]}).Passed,"T17 reversed/wrong axial step order and lengths fail source stations");
var t17Driven=t17Profile with{Steps=[t17Profile.Steps[0] with{RadialValueMm=32},t17Profile.Steps[1]]};
Check(RevolvedFamilyVerifier.Evaluate(t17Driven,t17Actual with{Steps=[new(0,20,32),new(20,40,20)]}).Passed,"T17 changed driving diameter remains editable and revalidates against the changed legal source requirement");
var t17Axial=t17Profile with{ProfileId="shaft-axial-hole",Kind=RevolvedFamilyKind.SteppedShaftWithAxialHole,AxialHoleConnectivityCheckId="T08:axial-hole",
    CheckRequirementFingerprints=new Dictionary<string,string>{{"T09:front",t17ViewFingerprint},{"T08:axial-hole",t17ConnectivityFingerprint}}};
var t17AxialActual=t17Actual with{CheckEvidence=[T17Evidence("T09:front",t17ViewFingerprint),T17Evidence("T08:axial-hole",t17ConnectivityFingerprint)]};
Check(RevolvedFamilyVerifier.Evaluate(t17Axial,t17AxialActual).Passed,"T17 second supported family shape accepts an axial-hole revolved part only with explicit T08 connectivity evidence");
Check(!RevolvedFamilyVerifier.Evaluate(t17Axial,t17Actual).Passed,"T17 axial-hole subtype never passes by revolve geometry alone when T08 evidence is absent");
Check(!RevolvedFamilyVerifier.Evaluate(t17Profile,t17Actual with{ModelSha256=""}).Passed,"T17 missing candidate model identity cannot pass");
Check(!RevolvedFamilyVerifier.Evaluate(t17Profile with{Steps=[],RequiredViewCheckIds=[],CheckRequirementFingerprints=new Dictionary<string,string>()},t17Actual with{Steps=[],CheckEvidence=[]}).Passed,"T17 empty family requirements cannot vacuously pass");

// T18: every hole instance is bijectively measured; arrays require a native editable pattern feature.
var t18Centers=new[]{new ProfilePoint(0,0),new ProfilePoint(10,0),new ProfilePoint(20,0),new ProfilePoint(30,0)};
var t18Scope=new string('A',64);var t18ConnectivityFingerprint=new string('B',64);var t18ProjectionFingerprint=new string('C',64);var t18SourceFactFingerprint=new string('D',64);
Stage4BoundCheckEvidence T18Evidence(string id,string fingerprint)=>new(){CheckId=id,Status=RequirementCheckStatus.Passed,EvidenceId="evidence:"+id,SourceSha256=t6ShaA,SourceRevisionId="rev-1",ModelSha256=t6ShaB,RequiredScopeFingerprint=t18Scope,RequirementFingerprint=fingerprint,ModelReopened=true,CaptureComplete=true};
var t18Profile=new HoleGroupProfile{GroupId="linear-4",SourceSha256=t6ShaA,SourceRevisionId="rev-1",SourceFactId="fact-4x-hole",SourceFactFingerprint=t18SourceFactFingerprint,HoleKind=HoleKind.Simple,
    RequiredScopeFingerprint=t18Scope,CheckRequirementFingerprints=new Dictionary<string,string>{{"T08:holes",t18ConnectivityFingerprint},{"T09:holes",t18ProjectionFingerprint}},
    PatternEditabilityProbes=[new("pattern-count","D1@Pattern",3,DrawingValueUnit.Unitless)],
    PatternKind=HoleGroupPatternKind.Linear,ExpectedCenters=t18Centers,DiameterMm=5,ThroughAll=true,PatternSpacingMm=10,ConnectivityCheckId="T08:holes",ProjectionCheckId="T09:holes"};
string T18TopologyScope(ProfilePoint center)=>Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{center.Xmm:R}|{center.Ymm:R}")));
HoleInstanceMeasurement T18Hole(ProfilePoint center,double depth=0,bool reverse=false)=>new(){Center=center,DiameterMm=5,ThroughAll=true,DepthMm=depth,ReverseDirection=reverse,
    TopologyEvidenceId="T08:"+center.Xmm.ToString("R",System.Globalization.CultureInfo.InvariantCulture)+":"+center.Ymm.ToString("R",System.Globalization.CultureInfo.InvariantCulture),TopologyScopeFingerprint=T18TopologyScope(center),TopologySourceFactFingerprint=t18SourceFactFingerprint,TopologyModelSha256=t6ShaB};
var t18Actual=new HoleGroupObservation{SourceSha256=t6ShaA,SourceRevisionId="rev-1",ModelSha256=t6ShaB,ModelReopened=true,Instances=t18Centers.Select(p=>T18Hole(p)).ToArray(),
    Pattern=new(){Kind=NativeFeatureKind.LinearPattern,Count=4,SpacingMm=10,Editable=true,RebuildSucceeded=true},CheckEvidence=[T18Evidence("T08:holes",t18ConnectivityFingerprint),T18Evidence("T09:holes",t18ProjectionFingerprint)]};
Check(HoleGroupVerifier.Evaluate(t18Profile,t18Actual).Passed,"T18 correct four-hole native linear pattern passes every instance plus T08/T09 group checks");
Check(!HoleGroupVerifier.Evaluate(t18Profile,t18Actual with{Instances=t18Actual.Instances.Take(3).ToArray()}).Passed,"T18 declared 4-hole group with only 3 actual holes fails count/coverage");
Check(!HoleGroupVerifier.Evaluate(t18Profile,t18Actual with{Pattern=null}).Passed,"T18 four independent holes cannot masquerade as a supported native parameter array");
var t18Blind=t18Profile with{GroupId="blind-4",ThroughAll=false,DepthMm=8};
var t18BlindInstances=t18Centers.Select((p,i)=>T18Hole(p) with{ThroughAll=false,DepthMm=i==2?7:8,ReverseDirection=false}).ToArray();
Check(!HoleGroupVerifier.Evaluate(t18Blind,t18Actual with{Instances=t18BlindInstances}).Passed,"T18 one wrong-depth instance fails even when the seed/other holes are correct");
var t18Counter=t18Profile with{GroupId="counterbore-4",HoleKind=HoleKind.Counterbore,CounterboreDiameterMm=8,CounterboreDepthMm=2};
var t18CounterInstances=t18Centers.Select(p=>T18Hole(p) with{CounterboreDiameterMm=8,CounterboreDepthMm=2,CounterboreReverseDirection=false}).ToArray();
Check(HoleGroupVerifier.Evaluate(t18Counter,t18Actual with{Instances=t18CounterInstances}).Passed,"T18 counterbore group validates each layer diameter/depth and entry-side direction");
Check(!HoleGroupVerifier.Evaluate(t18Counter,t18Actual with{Instances=t18CounterInstances.Select((h,i)=>i==1?h with{CounterboreReverseDirection=true}:h).ToArray()}).Passed,"T18 one counterbore layer cut from the opposite face fails even when the main drill direction remains correct");
var t18Tapped=t18Profile with{GroupId="tapped-4",HoleKind=HoleKind.Tapped,ThreadMajorDiameterMm=6,ThreadDesignation="M6x1"};
var t18TappedInstances=t18Centers.Select(p=>T18Hole(p) with{ThreadMajorDiameterMm=6,ThreadDesignation="M6x1",ThreadThroughAll=true,ThreadReverseDirection=false}).ToArray();
Check(HoleGroupVerifier.Evaluate(t18Tapped,t18Actual with{Instances=t18TappedInstances}).Passed,"T18 tapped group preserves explicit drill diameter, major diameter and thread designation per actual instance");
var t18DrivenCenters=new[]{new ProfilePoint(0,0),new ProfilePoint(12,0),new ProfilePoint(24,0)};
var t18Driven=t18Profile with{GroupId="linear-3-driven",ExpectedCenters=t18DrivenCenters,PatternSpacingMm=12};
Check(HoleGroupVerifier.Evaluate(t18Driven,t18Actual with{Instances=t18DrivenCenters.Select(p=>T18Hole(p)).ToArray(),Pattern=new(){Kind=NativeFeatureKind.LinearPattern,Count=3,SpacingMm=12,Editable=true,RebuildSucceeded=true}}).Passed,"T18 changing native pattern count/spacing rebuilds and all regenerated instances are rechecked");
var t18CircularCenters=new[]{new ProfilePoint(10,0),new ProfilePoint(0,10),new ProfilePoint(-10,0),new ProfilePoint(0,-10)};
var t18Circular=t18Profile with{GroupId="circular-4",PatternKind=HoleGroupPatternKind.Circular,ExpectedCenters=t18CircularCenters,PatternAngleDegrees=360,PatternSpacingMm=0};
Check(HoleGroupVerifier.Evaluate(t18Circular,t18Actual with{Instances=t18CircularCenters.Select(p=>T18Hole(p)).ToArray(),Pattern=new(){Kind=NativeFeatureKind.CircularPattern,Count=4,AngleDegrees=360,Editable=true,RebuildSucceeded=true}}).Passed,"T18 second supported array type validates a native circular hole pattern per actual instance");
Check(!HoleGroupVerifier.Evaluate(t18Profile,t18Actual with{ModelSha256="",CheckEvidence=[]}).Passed,"T18 missing current-model identity and bound T08/T09 evidence cannot pass");

// T19: source semantics route profile arcs vs solid edge treatments; repairs may retarget edges but never weaken values.
var t19FilletIntent=new EdgeTreatmentIntent{IntentId="fillet-r3",SourceLiteral="R3",SourceSha256=t6ShaA,SourceRevisionId="rev-1",Route=EdgeTreatmentRoute.SolidFillet,RadiusMm=3,
    TargetEdges=[t14EdgeRef],TargetOperationId="fillet",EditabilityProbes=[new("fillet-radius","R@Fillet",4)],AllowedRepairStrategies=new HashSet<EdgeTreatmentRepairStrategy>{EdgeTreatmentRepairStrategy.ReselectEdges,EdgeTreatmentRepairStrategy.SplitEdgeGroup,EdgeTreatmentRepairStrategy.ReorderFeature}};
var t19FilletActual=new EdgeTreatmentObservation{SourceSha256=t6ShaA,SourceRevisionId="rev-1",ModelSha256=t6ShaA,ModelReopened=true,ActualRoute=EdgeTreatmentRoute.SolidFillet,RadiusMm=3,
    NativeEditable=true,RebuildSucceeded=true,EdgeResolutions=[t14EdgeResolution],NativeDrivingEdgePersistentReferences=["edge-new"]};
Check(EdgeTreatmentVerifier.Evaluate(t19FilletIntent,t19FilletActual).Passed,"T19 solid-edge R3 uses native fillet semantics, uniquely resolved edge group and measured locked radius");
Check(!EdgeTreatmentVerifier.Evaluate(t19FilletIntent,t19FilletActual with{RadiusMm=1}).Passed,"T19 R3 is never silently reduced to R1 merely to make a feature build");
var t19ProfileArc=new EdgeTreatmentIntent{IntentId="profile-r3",SourceLiteral="section profile R3",SourceSha256=t6ShaA,SourceRevisionId="rev-1",Route=EdgeTreatmentRoute.ProfileArc,RadiusMm=3,EditabilityProbes=[new("profile-radius","R@Sketch",4)]};
Check(EdgeTreatmentVerifier.Evaluate(t19ProfileArc,t19FilletActual with{ActualRoute=EdgeTreatmentRoute.ProfileArc,EdgeResolutions=[]}).Passed,"T19 section-profile R is accepted on the profile-arc path without pretending it is a solid edge fillet");
Check(!EdgeTreatmentVerifier.Evaluate(t19ProfileArc,t19FilletActual).Passed,"T19 a solid fillet cannot satisfy a source R that belongs to the revolved/sketch profile");
Check(!EdgeTreatmentVerifier.Evaluate(t19FilletIntent,t19FilletActual with{TopologyChanged=true,EdgeResolutions=[t14EdgeResolution with{Rebound=false}]}).Passed,"T19 topology change cannot reuse an old edge resolution without explicit unique rebind evidence");
var t19AdjacentRef=t14EdgeRef with{RefId="adjacent-edge"};
Check(!EdgeTreatmentVerifier.Evaluate(t19FilletIntent,t19FilletActual with{EdgeResolutions=[t14EdgeResolution,t14EdgeResolution with{Reference=t19AdjacentRef}]}).Passed,"T19 actual processed edge set must exactly equal the source target group; an adjacent extra edge is not ignored");
var t19Rebound=t14EdgeResolution with{Rebound=true,ResolvedModelSha256=t6ShaB};
var t19RepairChecks=new[]{T14Check(t14FilletAttempt,t14FilletReceipt,"unaffected-requirement-sample",RequirementCheckStatus.Passed,"t19-sample-pass")};
var t19Repaired=t19FilletActual with{ModelSha256=t6ShaB,TopologyChanged=true,EdgeResolutions=[t19Rebound],RepairStrategiesUsed=[EdgeTreatmentRepairStrategy.ReselectEdges],
    RepairAttempt=t14FilletAttempt,RepairExecution=t14FilletReceipt,RepairCoverage=t14PostCoverage,RepairChecks=t19RepairChecks,RepairValidation=t14PostValid,CollateralDiff=t14PostDiff};
Check(EdgeTreatmentVerifier.Evaluate(t19FilletIntent,t19Repaired).Passed,"T19 allowed edge reselection after topology change must uniquely rebind, pass T14 and show no T13 collateral impact");
Check(!EdgeTreatmentVerifier.Evaluate(t19FilletIntent,t19Repaired with{CollateralDiff=null}).Passed,"T19 repaired edge treatment cannot omit the explicit T13 collateral-diff gate");
Check(!EdgeTreatmentVerifier.Evaluate(t19FilletIntent,t19Repaired with{CollateralDiff=t14PostDiff with{FailedUnaffectedRequirementIds=["unrelated-hole"]}}).Passed,"T19 repair that accidentally changes an adjacent/unaffected requirement is blocked by the T13 collateral diff");
Check(!EdgeTreatmentVerifier.Evaluate(t19FilletIntent,t19Repaired with{CollateralDiff=t14PostDiff with{CandidateModelSha256=new string('E',64)}}).Passed,"T19 T13 evidence from another candidate model cannot be rebound to the current repaired model");
Check(!EdgeTreatmentVerifier.Evaluate(t19FilletIntent,t19Repaired with{CollateralDiff=t14PostDiff with{BaselineCaptureComplete=false}}).Passed,"T19 incomplete T13 baseline capture cannot pass the repair gate");
Check(!EdgeTreatmentVerifier.Evaluate(t19FilletIntent,t19Repaired with{RepairAttempt=null,RepairExecution=null,RepairCoverage=null,RepairChecks=[],RepairValidation=t14PostValid}).Passed,"T19 a real T14 Passed result from another/missing repair bundle is not portable acceptance evidence");
var t19ChamferIntent=new EdgeTreatmentIntent{IntentId="chamfer-2x45",SourceLiteral="2 x 45 deg",SourceSha256=t6ShaA,SourceRevisionId="rev-1",Route=EdgeTreatmentRoute.SolidChamfer,
    DistanceMm=2,AngleDegrees=45,ChamferMode=ChamferMode.DistanceAngle,TargetEdges=[t14EdgeRef],EditabilityProbes=[new("chamfer-distance","D1@Chamfer",2.5)]};
Check(EdgeTreatmentVerifier.Evaluate(t19ChamferIntent,t19FilletActual with{ActualRoute=EdgeTreatmentRoute.SolidChamfer,RadiusMm=0,DistanceMm=2,AngleDegrees=45,ChamferMode=ChamferMode.DistanceAngle}).Passed,"T19 explicit length-angle chamfer keeps its source distance/angle and native editability");
Check(!EdgeTreatmentVerifier.Evaluate(t19ChamferIntent,t19FilletActual with{ActualRoute=EdgeTreatmentRoute.SolidChamfer,RadiusMm=0,DistanceMm=double.NaN,AngleDegrees=45,ChamferMode=ChamferMode.DistanceAngle}).Passed,"T19 NaN measured chamfer distance fails closed");
Check(!EdgeTreatmentVerifier.Evaluate(t19FilletIntent with{ToleranceMm=double.PositiveInfinity},t19FilletActual with{RadiusMm=1}).Passed,"T19 infinite source tolerance cannot weaken R3 to R1");
Check(!EdgeTreatmentVerifier.Evaluate(t19FilletIntent with{ToleranceMm=0},t19FilletActual).Passed,"T19 zero source tolerance is invalid instead of becoming a special acceptance mode");

Console.WriteLine($"{count} focused core checks passed.");

