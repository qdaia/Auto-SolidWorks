using System.Text.Json;
using System.Text.Json.Nodes;
using CadModeling.Core;
using CadModeling.Ir;

var output = Path.GetFullPath(args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "shape-regression-" + Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(output);
var passed = new List<string>();
void Check(bool condition, string label) { if (!condition) throw new Exception(label); passed.Add(label); Console.WriteLine("PASS " + label); }
var operationIds = new[] { "base", "half", "back" };
var verification = new ModelVerificationSpec { Bounds = [new() { Id = "wall-check", SourceLiteral = "source fixture", SizeMm = new(40,56,40) }] };
var regions = new[] {
    Region("base", DrawingMaterialState.Material,"base"), Region("half", DrawingMaterialState.Material,"half"),
    Region("back", DrawingMaterialState.Material,"back"), Region("opening", DrawingMaterialState.Void,"half") };
DrawingMaterialRegion Region(string id, DrawingMaterialState material, string op) => new() {
    Id=id, MaterialState=material, Description=id + " independently expected from source",
    SourceContourIds=["front", "top"], OperationIds=[op], VerificationCheckIds=["wall-check"],
    Realization="Profile/solid operation realizes only the declared material or opening; this is not a native occupancy claim" };
DrawingShapeContour Contour(string view) => new() { Id=view, ViewId=view, Bounds=new(.1,.1,.9,.9), Role=DrawingContourRole.Step,
    Evidence="Source contour: independently reviewed stepped annular wall", RegionIds=regions.Select(r=>r.Id).ToArray() };
DrawingAngularSector Sector(double start,double end,string region) => new() { StartDegrees=start, EndDegrees=end, RegionId=region };
DrawingAxialLayer Layer(string id,double start,double end, params DrawingAngularSector[] sectors) => new() {
    Id=id,MinimumMm=start,MaximumMm=end,BoundaryEvidence="source height transition " + id,Sectors=sectors };
var layers = new[] {
    Layer("bottom",0,20,Sector(0,360,"base")),
    Layer("middle",20,36,Sector(0,90,"back"),Sector(90,270,"half"),Sector(270,360,"opening")),
    Layer("upper",36,56,Sector(0,90,"opening"),Sector(90,270,"half"),Sector(270,360,"opening")) };
var map = new DrawingAxialMaterialMap { Id="wall-band",OriginMm=new(0,0,0),AxisDirection=new(0,1,0),ZeroAngleDirection=new(1,0,0),
    InnerRadiusMm=10,OuterRadiusMm=20,MinimumMm=0,MaximumMm=56,DatumEvidence="source center, bottom datum and right hand rotation about Y",Layers=layers };
var item = new DrawingFeatureDecomposition { FeatureId="body",Kind=DrawingShapeKind.AxialAngular,State=DrawingReviewState.Reviewed,
    TopologyReview="Compare half-only, full-height ring and a local back quarter; source step and opening require the local quarter only",
    Contours=[Contour("front"),Contour("top")],Regions=regions,AxialMaps=[map] };
var context = new DrawingPlanContext { SourcePath="independent-source-fixture",WorkflowRevision=SourceWorkflow.Revision,
    RequireCompleteBindings=true,RequireInterpretationReview=true,RequireDimensionEvidence=true,RequireGeometryAnchors=true,RequireShapeDecomposition=true,
    Views=[new(){Id="front",Kind=ModelDrawingView.Front},new(){Id="top",Kind=ModelDrawingView.Top}],
    Features=[new(){Id="body",SourceLiteral="stepped partial annulus",ViewIds=["front","top"],OperationIds=operationIds,VerificationCheckIds=["wall-check"]}],
    ShapeDecomposition=new(){State=DrawingReviewState.Reviewed,Features=[item]} };
IReadOnlyList<ModelingDiagnostic> Validate(DrawingPlanContext c) => DrawingShapeDecompositionValidation.Validate(c,operationIds,verification);
DrawingPlanContext With(DrawingFeatureDecomposition f) => context with { ShapeDecomposition=context.ShapeDecomposition! with { Features=[f] } };
void Reject(DrawingPlanContext c,string code,string label) => Check(Validate(c).Any(d=>d.Code==code && d.Severity==DiagnosticSeverity.Error),label);
Check(Validate(context).Count==0,"source-derived 20/36/56 stepped partial annulus accepted as a declared decomposition");
Reject(context with{ShapeDecomposition=null},"SOURCE_SHAPE_REQUIRED","old half-plus-base plan cannot enter new workflow without shape decomposition");
Reject(context with{ShapeDecomposition=context.ShapeDecomposition! with{Features=null!}},"SOURCE_SHAPE_INVENTORY","null feature inventory fails closed without throwing");
Reject(With(item with{AxialMaps=[map with{Layers=null!}]}),"SOURCE_SHAPE_INVENTORY","null height layer list fails closed without throwing");
Reject(context with{RequireShapeDecomposition=false,ShapeDecomposition=null},"SOURCE_SHAPE_REQUIRED","disabling flag cannot bypass current workflow shape inventory");
Check(SourceWorkflow.Validate(context with{RequireShapeDecomposition=false}).Any(d=>d.Code=="SOURCE_WORKFLOW_GATES"),"current workflow reports disabled shape gate");
Reject(With(item with{State=DrawingReviewState.Unresolved}),"SOURCE_SHAPE_UNREVIEWED","pending source review cannot be promoted by numeric checks");
Reject(context with{ShapeDecomposition=context.ShapeDecomposition! with{Features=[]}},"SOURCE_SHAPE_FEATURE_MISSING","critical body cannot disappear from source decomposition");
Reject(With(item with{Contours=[item.Contours[0]]}),"SOURCE_SHAPE_VIEW_MISSING","missing top-view contour review rejected");
Reject(With(item with{Contours=[item.Contours[0] with{RegionIds=[]},item.Contours[1]]}),"SOURCE_SHAPE_CONTOUR_UNACCOUNTED","unaccounted source step contour rejected");
Reject(With(item with{Contours=[item.Contours[0] with{Bounds=new(-.1,0,1,1)},item.Contours[1]]}),"SOURCE_SHAPE_CONTOUR","invalid source crop coordinates rejected");
Reject(With(item with{Regions=regions.Select(r=>r.Id=="back"?r with{OperationIds=[]}:r).ToArray()}),"SOURCE_SHAPE_REGION_OPERATION","declared back quarter without a modeling operation rejected");
Reject(With(item with{Regions=regions.Select(r=>r.Id=="back"?r with{OperationIds=["missing-back"]}:r).ToArray()}),"SOURCE_SHAPE_REGION_OPERATION","link to nonexistent back-quarter operation rejected");
Reject(With(item with{Regions=regions.Select(r=>r.Id=="back"?r with{VerificationCheckIds=["missing-check"]}:r).ToArray()}),"SOURCE_SHAPE_REGION_CHECK","invented inspection handle rejected");
Reject(With(item with{AxialMaps=[]}),"SOURCE_SHAPE_AXIAL_MAP_REQUIRED","partial cylinder cannot omit height/angle table");
Reject(With(item with{AxialMaps=[map with{Layers=[layers[0],layers[2]]}]}),"SOURCE_SHAPE_LAYER_GAP","omitted 20..36 middle height detected");
Reject(With(item with{AxialMaps=[map with{Layers=[layers[0] with{MaximumMm=21},layers[1],layers[2]]}]}),"SOURCE_SHAPE_LAYER_GAP","overlapping height intervals rejected");
Reject(With(item with{AxialMaps=[map with{Layers=[layers[0],layers[1] with{Sectors=[Sector(90,270,"half"),Sector(270,360,"opening")]},layers[2]]}]}),"SOURCE_SHAPE_ANGULAR_GAP","omitted back 90-degree sector detected");
Reject(With(item with{AxialMaps=[map with{Layers=[layers[0],layers[1] with{Sectors=[Sector(0,100,"back"),Sector(90,270,"half"),Sector(270,360,"opening")]},layers[2]]}]}),"SOURCE_SHAPE_ANGULAR_GAP","overlapping angular sectors rejected");
Reject(With(item with{AxialMaps=[map with{Layers=[layers[0],layers[1] with{Sectors=[Sector(0,90,"missing"),Sector(90,270,"half"),Sector(270,360,"opening")]},layers[2]]}]}),"SOURCE_SHAPE_SECTOR_REGION","sector with unknown material region rejected");
Reject(With(item with{AxialMaps=[map with{Layers=layers.Select(l=>l with{Sectors=l.Sectors.Select(s=>s.RegionId=="back"?s with{RegionId="opening"}:s).ToArray()}).ToArray()}]}),"SOURCE_SHAPE_REGION_UNMAPPED","declared back region cannot be left outside material table");
Reject(With(item with{AxialMaps=[map,map with{Id="overlap"}]}),"SOURCE_SHAPE_RADIAL_OVERLAP","overlapping radial bands rejected");
Reject(With(item with{AxialMaps=[map with{AxisDirection=new(1,0,0)}]}),"SOURCE_SHAPE_AXIAL_MAP","nonorthogonal axis and zero direction rejected");
Reject(With(item with{AxialMaps=[map with{OriginMm=null!}]}),"SOURCE_SHAPE_AXIAL_MAP","null source frame diagnosed without exception");
var plate = item with {Kind=DrawingShapeKind.Prismatic,AxialMaps=[]};
Check(Validate(With(plate)).Count==0,"prismatic parts are not forced to invent an axial map");
Check(Validate(context with{WorkflowRevision=SourceWorkflow.LegacyRevision,RequireShapeDecomposition=false,ShapeDecomposition=null}).Count==0,"legacy contexts keep their recorded policy and identity");
Check(Validate(new(){SourcePath="legacy"}).Count==0,"legacy context without new decomposition remains readable");
var oldWire=JsonSerializer.Serialize(new DrawingPlanContext{SourcePath="legacy"},ModelingIrJson.Options);
Check(!oldWire.Contains("shape_decomposition")&&!oldWire.Contains("source_evidence"),"new default fields do not alter legacy serialized context digests");
var localRingDraft=new GenericModelDraft{SourceText="source contour fixture",DrawingContext=With(plate),Operations=[new(){Id="half",Type=GenericOperationKind.ProfileSketch,
    DrivenProfile=new(){Kind=DrivenProfileKind.HalfAnnulus,RadiusMm=20,InnerRadiusMm=10}}],Verification=verification};
Check(DrawingPlanValidation.Validate(localRingDraft).Any(d=>d.Code=="SOURCE_SHAPE_KIND"),"known partial annulus cannot be mislabeled General or Prismatic");
var input=JsonNode.Parse("""{"drawing_context":{"source_path":"source.png"},"operations":[]}""")!;
var prepared=SourceWorkflowInputs.Prepare(input);
Check(prepared.Input["drawing_context"]!["require_shape_decomposition"]?.GetValue<bool>()!=true &&
    prepared.Input["drawing_context"]!["workflow_revision"]!.GetValue<string>()==SourceWorkflow.DefaultRevision,"four-stage default removes mandatory decomposition");
Check(prepared.Input["drawing_context"]!["shape_decomposition"] is null,"preparation does not fabricate reviewed contours or regions");
Check(CompactModelingTools.GetContract("shape_decomposition").ToJsonString().Contains("axial_maps"),"public schema exposes full decomposition contract");
var wire=JsonSerializer.Serialize(context,ModelingIrJson.Options);
Check(Validate(JsonSerializer.Deserialize<DrawingPlanContext>(wire,ModelingIrJson.Options)!).Count==0,"shape decomposition survives strict JSON roundtrip");
Check(DrawingPlanValidation.ValidateCoverage(context with{ShapeDecomposition=null},operationIds,verification).Any(d=>d.Code=="SOURCE_SHAPE_REQUIRED"),"compiler coverage gate includes shape decomposition");
File.WriteAllText(Path.Combine(output,"fixture.json"),JsonSerializer.Serialize(context,ModelingIrJson.Options));
File.WriteAllText(Path.Combine(output,"summary.json"),JsonSerializer.Serialize(new{passed=true,checks=passed.Count,native_execution=false,source_interpretation="controlled_fixture_not_blind_accuracy",results=passed},ModelingIrJson.Options));
