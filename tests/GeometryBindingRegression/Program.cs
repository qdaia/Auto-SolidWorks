using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CadModeling.Core;
using CadModeling.Drawing.Ingestion;
using CadModeling.Drawing.Providers.Abstractions;
using CadModeling.Ir;

if(args[0]=="--observe"){
    var native=Path.GetFullPath(args[1]);var outputDirectory=Path.GetFullPath(args[4]);Directory.CreateDirectory(outputDirectory);
    using var executor=new AutoStartingNamedPipeModelingExecutor(new(){PipeName="support-observation-"+Guid.NewGuid().ToString("N"),ExecutablePath=Path.GetFullPath(args[2]),LogDirectory=outputDirectory,StartupTimeoutMilliseconds=60000});
    string Sha(string path)=>Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));
    foreach(var view in new[]{ObservationViewKind.OrthographicFront,ObservationViewKind.OrthographicTop,ObservationViewKind.OrthographicRight,ObservationViewKind.Isometric}){
        var result=await executor.CaptureObservationAsync(new(){NativePath=native,OutputDirectory=outputDirectory,Observation=new(){RequestId=Guid.NewGuid().ToString("N"),Question="Compare source support topology and ear orientation with actual saved model",SourceRevisionId="support-regression",SourceSha256=Sha(args[3]),ModelSha256=Sha(native),RequestedView=view}});
        File.WriteAllText(Path.Combine(outputDirectory,view+".json"),JsonSerializer.Serialize(result,ModelingIrJson.Options));Console.WriteLine(JsonSerializer.Serialize(result,ModelingIrJson.Options));
        if(!result.Success){Environment.ExitCode=1;break;}
    }return;
}
if(args[0]=="--compile"){
    var input=SourceWorkflowInputs.Prepare(JsonNode.Parse(File.ReadAllText(args[1]))!).Input;var draft=CompactModelInputs.Decode<GenericModelDraft>(input);var compiled=new GenericPlanCompiler().Compile(draft,args[3]);
    File.WriteAllText(args[2],JsonSerializer.Serialize(compiled,ModelingIrJson.Options));Console.WriteLine(JsonSerializer.Serialize(compiled.Diagnostics,ModelingIrJson.Options));Environment.ExitCode=compiled.Success?0:1;return;
}

if(args[0]=="--validate-context"){
    var c=JsonSerializer.Deserialize<DrawingPlanContext>(File.ReadAllText(args[1]),ModelingIrJson.Options)!;
    var errors=DrawingDimensionTraceValidation.Validate(c);
    File.WriteAllText(args[2],JsonSerializer.Serialize(new{success=errors.Count==0,dimensions=c.Dimensions.Count,anchors=c.GeometryAnchors.Count,diagnostics=errors,scope="source dimension ink and attachment validation only, not complete compilation"},ModelingIrJson.Options));
    Console.WriteLine(JsonSerializer.Serialize(errors,ModelingIrJson.Options));Environment.ExitCode=errors.Count==0?0:1;return;
}

var output=Path.GetFullPath(args[0]);Directory.CreateDirectory(output);var checks=new List<string>();
void Check(bool ok,string name){if(!ok)throw new Exception(name);checks.Add(name);Console.WriteLine("PASS "+name);}
void Reject(Action action,string name){try{action();}catch(Exception e)when(e is ArgumentException or InvalidOperationException or IOException or InvalidDataException or JsonException){Check(true,name);return;}throw new Exception("Accepted: "+name);}
JsonElement J<T>(T x)=>JsonSerializer.SerializeToElement(x,ModelingIrJson.Options);
JsonObject N(object x)=>JsonSerializer.SerializeToNode(x,ModelingIrJson.Options)!.AsObject();
string Id(object x)=>N(x)["input_id"]!.GetValue<string>();
void Save(string name,object x)=>File.WriteAllText(Path.Combine(output,name+".json"),JsonSerializer.Serialize(x,ModelingIrJson.Options));
var source=Path.Combine(output,"fixture.png");var pixels=Enumerable.Repeat((byte)255,640*480*4).ToArray();
for(int y=0;y<480;y++)for(int x=0;x<640;x++){
    bool ink=x is >=79 and <=81&&y is >=50 and <=240||x is >=219 and <=221&&y is >=50 and <=240||y is >=69 and <=71&&x is >=80 and <=220;
    ink|=y is >=49 and <=51&&x is >=40 and <=260&&x is not(>=130 and <=170);
    double radius=double.Hypot(x-420,y-150),angle=(Math.Atan2(y-150,x-420)*180/Math.PI+360)%360;
    ink|=Math.Abs(radius-65)<=1.2;
    double r2=double.Hypot(x-420,y-350),a2=(Math.Atan2(y-350,x-420)*180/Math.PI+360)%360;
    ink|=Math.Abs(r2-65)<=1.2&&a2%30<21;
    if(ink){int i=(y*640+x)*4;pixels[i]=pixels[i+1]=pixels[i+2]=0;}
}
BindingWorkspaceImages.Save(source,BitmapSource.Create(640,480,96,96,PixelFormats.Bgra32,null,pixels,640*4));
var ingestion=await new EngineeringDrawingIngestionService().IngestAsync(new DrawingIngestionRequest{InputPath=source,ArtifactRoot=Path.Combine(output,"ingestion")});
var inventory=DrawingOmissionValidation.Load(ingestion.OmissionInventoryPath!,ingestion.OmissionInventorySha256);
var store=new CompactArtifactStore(Path.Combine(output,"store"));
var prepared=N(SourceBindingTools.Prepare(store,ingestion.OmissionInventoryPath!,ingestion.OmissionInventorySha256!,Path.Combine(output,"workspace"),J(new[]{new BindingCrop("focus",1,new(.05,.05,.95,.95))})));
string wid=prepared["workspace_id"]!.GetValue<string>();var w=store.LoadResult(wid).Deserialize<BindingWorkspace>(ModelingIrJson.Options)!;var f=w.Frames.Single(x=>x.Id=="focus");
var context=new DrawingPlanContext{SourcePath=source,WorkflowRevision=SourceWorkflow.FourStageRevision,RequireCompleteBindings=true,RequireGeometryAnchors=true,RequireDimensionEvidence=true,Views=[new(){Id="front",Kind=ModelDrawingView.Front}],Features=[new(){Id="plate",SourceLiteral="source plate"}],OmissionReview=new(){InventoryPath=ingestion.OmissionInventoryPath!,InventorySha256=ingestion.OmissionInventorySha256!}};
string baseId=Id(CompactModelInputs.Store(store,"drawing_context",J(context),null,null));
var lines=N(SourceGeometryTools.Find(store,wid,"focus"));var circles=N(SourceCircleTools.Find(store,wid,"focus"));Save("lines",lines);Save("circles",circles);
string lineArtifact=lines["artifact_id"]!.GetValue<string>(),circleArtifact=circles["artifact_id"]!.GetValue<string>();
var line=lines["result"]!["lines"]!.AsArray().First(x=>x!["frame_path"]!.AsArray().All(p=>Math.Abs(p!["x"]!.GetValue<double>()+f.Left-80)<2))!;
var circle=circles["result"]!["circles"]!.AsArray().First(x=>Math.Abs(x!["frame_center"]!["y"]!.GetValue<double>()+f.Top-150)<2)!;
var dash=circles["result"]!["circles"]!.AsArray().First(x=>Math.Abs(x!["frame_center"]!["y"]!.GetValue<double>()+f.Top-350)<2)!;
SourceGeometrySelection S(JsonNode p,string id)=>new(){ProposalId=p["id"]!.GetValue<string>(),Id=id,PhysicalEntityId=id,Evidence="Explicit fixture source stroke selected"};
object Bind(string artifact,SourceGeometrySelection[] selected,string? input=null,string? workspace=null)=>SourceGeometryBindingTools.Bind(store,input??baseId,"drawing_context",workspace??wid,artifact,"front","plate","source left edge",J(selected));
var bound=N(Bind(lineArtifact,[S(line,"left")]));string boundId=Id(bound);var actual=CompactModelInputs.Decode<DrawingPlanContext>(store.LoadInput(boundId,"context"));var anchor=actual.GeometryAnchors.Single();
Check(anchor.State==DrawingReviewState.Unresolved,"review state defaults to unresolved");
Check(anchor.SourcePath.All(p=>Math.Abs(p.X*640-80)<2),"crop path maps to original pixels");
Check(anchor.CandidateIds.Count>0&&anchor.CandidateIds.All(id=>inventory.Candidates.Any(c=>c.Id==id&&c.PageNumber==1&&c.Kind is DrawingCandidateKind.Geometry or DrawingCandidateKind.ResidualInk)),"only same-page geometric evidence selected");
Check(store.LoadInput(baseId,"context")["geometry_anchors"]!.AsArray().Count==0,"base input immutable");
Check(Id(Bind(lineArtifact,[S(line,"left")]))==boundId,"selection deterministic");
Check(bound["input_path"] is not null&&bound["input_sha256"] is not null&&!bound["compiled"]!.GetValue<bool>(),"complete stored receipt without compile claim");
var right=lines["result"]!["lines"]!.AsArray().First(x=>x!["frame_path"]!.AsArray().All(p=>Math.Abs(p!["x"]!.GetValue<double>()+f.Left-220)<2))!;
string pairId=Id(Bind(lineArtifact,[S(line,"left") with{State=DrawingReviewState.Reviewed},S(right,"right") with{State=DrawingReviewState.Reviewed}]));
var pair=CompactModelInputs.Decode<DrawingPlanContext>(store.LoadInput(pairId,"context"));
var measured=pair with{Dimensions=[new(){Id="fixture-width",OperationId="fixture",ParameterPath="fixture.width",Value=140,SourceLiteral="fixture width",ViewIds=["front"],BindingEvidence=new(){FeatureId="plate",ViewId="front",MeasurementId="fixture-width",QuantityKind=DrawingQuantityKind.Linear,TextBounds=new(.2,.07,.3,.1),TargetBounds=new(.1,.1,.4,.6),Datum="source edges",AttachmentExplanation="fixture horizontal stroke with vertical supports",StartAnchorId="left",EndAnchorId="right",StartReference="left",EndReference="right",Trace=new(){Kind=DrawingDimensionTraceKind.LinearDimension,MeasurementMode=DrawingMeasurementMode.Horizontal,ModelDirection=new(1,0,0),Path=[new(80/640d,70/480d),new(220/640d,70/480d)],StartExtension=[new(80/640d,100/480d),new(80/640d,70/480d)],EndExtension=[new(220/640d,100/480d),new(220/640d,70/480d)],Evidence="fixture source strokes"}}}]};
var diagnostics=DrawingDimensionTraceValidation.Validate(measured);Save("trace-diagnostics",diagnostics);Check(diagnostics.Count==0,"selected anchors accepted by existing dimension ink and endpoint validator");
var moved=measured with{GeometryAnchors=measured.GeometryAnchors.Select(a=>a.Id=="left"?a with{SourcePath=a.SourcePath.Select(p=>p with{X=p.X+.05}).ToArray()}:a).ToArray()};
Check(DrawingDimensionTraceValidation.Validate(moved).Any(d=>d.Code is "DIMENSION_TRACE_ANCHOR_PATH" or "DIMENSION_TRACE_INK"),"existing validator rejects displaced selected anchor");
var outside=measured with{Dimensions=[measured.Dimensions[0] with{BindingEvidence=measured.Dimensions[0].BindingEvidence! with{TextBounds=new(128/640d,40/480d,172/640d,60/480d),Trace=measured.Dimensions[0].BindingEvidence!.Trace! with{Path=[new(40/640d,50/480d),new(260/640d,50/480d)],StartArrowPoint=new(80/640d,50/480d),EndArrowPoint=new(220/640d,50/480d),StartExtension=[new(80/640d,70/480d),new(80/640d,50/480d)],EndExtension=[new(220/640d,70/480d),new(220/640d,50/480d)]}}}]};
Check(DrawingDimensionTraceValidation.Validate(outside).Count==0,"outside-arrow tips preserve actual extension contacts and longer visible line");
DrawingPlanContext ArrowTrace(DrawingDimensionTrace t)=>outside with{Dimensions=[outside.Dimensions[0] with{BindingEvidence=outside.Dimensions[0].BindingEvidence! with{Trace=t}}]};
var ot=outside.Dimensions[0].BindingEvidence!.Trace!;
Check(DrawingDimensionTraceValidation.Validate(ArrowTrace(ot with{StartArrowPoint=null,EndArrowPoint=null})).Any(d=>d.Code=="DIMENSION_TRACE_ATTACHMENT"),"extended line endpoints do not silently replace arrow tips");
Check(DrawingDimensionTraceValidation.Validate(ArrowTrace(ot with{EndArrowPoint=null})).Any(d=>d.Code=="DIMENSION_ARROW_TIPS"),"one outside arrow tip rejected");
Check(DrawingDimensionTraceValidation.Validate(ArrowTrace(ot with{StartArrowPoint=new(140/640d,50/480d)})).Any(d=>d.Code=="DIMENSION_ARROW_TIPS"),"arrow tip on blank text gap rejected");
Check(DrawingDimensionTraceValidation.Validate(ArrowTrace(ot with{StartArrowPoint=new(80/640d,70/480d)})).Any(d=>d.Code=="DIMENSION_ARROW_TIPS"),"arrow tip on other source line outside dimension path rejected");
var extendedAnchor=anchor with{Id="left-plane",Kind=DrawingAnchorKind.Plane,PhysicalEntityId="left-plane",State=DrawingReviewState.Reviewed,Bounds=new(77/640d,28/480d,83/640d,243/480d),SourcePath=[new(80/640d,30/480d),new(80/640d,240/480d)],Construction=new(){Kind=DrawingAnchorConstructionKind.LineExtension,SupportAnchorIds=["left"],Evidence="bounded extension of physical planar silhouette"}};
var extendedContext=outside with{GeometryAnchors=outside.GeometryAnchors.Concat([extendedAnchor]).ToArray(),Dimensions=[outside.Dimensions[0] with{BindingEvidence=outside.Dimensions[0].BindingEvidence! with{StartAnchorId="left-plane"}}]};
Check(DrawingDimensionTraceValidation.Validate(extendedContext).Count==0,"explicit bounded source plane extension accepted");
Check(DrawingDimensionTraceValidation.Validate(extendedContext with{GeometryAnchors=extendedContext.GeometryAnchors.Select(a=>a.Id=="left-plane"?a with{SourcePath=[new(80/640d,0),new(80/640d,240/480d)],Bounds=new(77/640d,0,83/640d,243/480d)}:a).ToArray()}).Any(d=>d.Code=="DIMENSION_ANCHOR_CONSTRUCTION"),"excessive source plane extension rejected");
Check(DrawingDimensionTraceValidation.Validate(extendedContext with{GeometryAnchors=extendedContext.GeometryAnchors.Select(a=>a.Id=="left-plane"?a with{SourcePath=[new(90/640d,30/480d),new(90/640d,240/480d)],Bounds=new(87/640d,28/480d,93/640d,243/480d)}:a).ToArray()}).Any(d=>d.Code=="DIMENSION_ANCHOR_CONSTRUCTION"),"offset parallel plane cannot masquerade as source extension");
var closedId=Id(Bind(circleArtifact,[S(circle,"circle") with{State=DrawingReviewState.Reviewed}]));var closed=CompactModelInputs.Decode<DrawingPlanContext>(store.LoadInput(closedId,"context")).GeometryAnchors.Single();
Check(closed.Kind==DrawingAnchorKind.Circle&&closed.SourcePath.Count<=128&&closed.SourcePath[0]==closed.SourcePath[^1],"closed circle keeps bounded closed path");
Check(closed.State==DrawingReviewState.Reviewed,"explicit review state retained");
var projected=pair with{Views=[new(){Id="front",Kind=ModelDrawingView.Front},new(){Id="plan",Kind=ModelDrawingView.Top}],Features=[new(){Id="plate",SourceLiteral="fixture cylinder",ViewIds=["front","plan"],OperationIds=["fixture"]}],GeometryAnchors=pair.GeometryAnchors.Concat([closed with{ViewId="plan",PhysicalEntityId="cylinder-rim"}]).ToArray(),CircularProjections=[new(){Id="cylinder-projection",CircleAnchorId=closed.Id,SilhouetteAnchorIds=["left","right"],AxisDirection=new(0,0,1),State=DrawingReviewState.Reviewed,Evidence="fixture axial outlines correspond to circular end view"}]};
var projectedDimension=new DrawingDimensionFact{Id="side-diameter",OperationId="fixture",ParameterPath="feature.diameter_mm",Value=130,SourceLiteral="Ø130",ViewIds=["front"],BindingEvidence=new(){FeatureId="plate",ViewId="front",MeasurementId="diameter",QuantityKind=DrawingQuantityKind.Diameter,TextBounds=new(.2,.1,.3,.2),TargetBounds=projected.GeometryAnchors.First(a=>a.Id=="right").Bounds,Datum="cylinder axis",AttachmentExplanation="leader to silhouette of circular cylinder",TargetAnchorId="right"}};
projected=projected with{Dimensions=[projectedDimension]};
Check(DrawingGeometryAnchorValidation.Validate(projected).Count==0,"explicit circle-to-two-lines correspondence accepts projected diameter without changing line kind");
Check(DrawingGeometryAnchorValidation.Validate(projected with{CircularProjections=[]}).Any(d=>d.Code=="ANCHOR_CIRCULAR"),"diameter-to-line without correspondence remains rejected");
var projection=projected.CircularProjections.Single();
Check(DrawingCircularProjectionValidation.Validate(projected with{CircularProjections=[projection with{State=DrawingReviewState.Unresolved}]}).Count>0,"unreviewed correspondence rejected");
Check(DrawingCircularProjectionValidation.Validate(projected with{CircularProjections=[projection with{SilhouetteAnchorIds=["left","left"]}]}).Count>0,"duplicate silhouette cannot prove cylinder");
Check(DrawingCircularProjectionValidation.Validate(projected with{GeometryAnchors=projected.GeometryAnchors.Select(a=>a.Id==closed.Id?a with{ViewId="front"}:a).ToArray()}).Count>0,"same-view circle is not cross-view evidence");
Check(DrawingCircularProjectionValidation.Validate(projected with{GeometryAnchors=projected.GeometryAnchors.Select(a=>a.Id==closed.Id?a with{FeatureId="other"}:a).ToArray()}).Count>0,"other feature circle cannot establish identity");
Check(DrawingCircularProjectionValidation.Validate(projected with{GeometryAnchors=projected.GeometryAnchors.Select(a=>a.Id=="right"?a with{SourcePath=a.SourcePath.Select(p=>p with{X=p.X+.1}).ToArray(),Bounds=new(.4,.1,.5,.6)}:a).ToArray()}).Count>0,"projected silhouette on blank pixels rejected");
Check(DrawingCircularProjectionValidation.Validate(projected with{CircularProjections=[projection with{AxisDirection=new(0,0,0)}]}).Count>0,"zero model axis rejected");
var otherDiameter=projectedDimension with{Id="plan-diameter",Value=131,SourceLiteral="Ø131",ViewIds=["plan"],BindingEvidence=projectedDimension.BindingEvidence! with{ViewId="plan",TargetAnchorId=closed.Id,TargetBounds=closed.Bounds,MeasurementId="renamed-diameter"}};
Check(DrawingGeometryAnchorValidation.Validate(projected with{Dimensions=[projectedDimension,otherDiameter]}).Any(d=>d.Code=="ANCHOR_CIRCULAR_CONFLICT"),"renaming projected measurement cannot hide conflicting diameter");
Check(DrawingGeometryAnchorValidation.Validate(projected with{Dimensions=[projectedDimension,otherDiameter with{Value=130,SourceLiteral="Ø130"}]}).Count==0,"consistent circle and silhouette dimensions accepted");
var oppositeAnchors=pair.GeometryAnchors.Select(a=>a with{Id=a.Id+"-plan",ViewId="plan"}).ToArray();
var linear1=measured.Dimensions.Single();var linear2=linear1 with{Id="plan-width",Value=141,SourceLiteral="141",ViewIds=["plan"],BindingEvidence=linear1.BindingEvidence! with{ViewId="plan",MeasurementId="renamed-width",StartAnchorId="right-plan",EndAnchorId="left-plan",Trace=linear1.BindingEvidence!.Trace! with{ModelDirection=new(-1,0,0)}}};
var linearContext=projected with{CircularProjections=[],GeometryAnchors=pair.GeometryAnchors.Concat(oppositeAnchors).ToArray(),Dimensions=[linear1,linear2]};
Check(DrawingMeasurementIdentityValidation.Validate(linearContext).Any(d=>d.Code=="ANCHOR_LINEAR_CONFLICT"),"renamed linear measurement with reversed endpoints and direction cannot hide conflict");
Check(DrawingMeasurementIdentityValidation.Validate(linearContext with{Dimensions=[linear1,linear2 with{Value=140,SourceLiteral="140"}]}).Count==0,"reversed equal linear measurement accepted");
Check(DrawingMeasurementIdentityValidation.Validate(linearContext with{Dimensions=[linear1 with{Value=25.4,SourceLiteral="25.4"},linear2 with{Value=1,SourceLiteral="1in",Unit=DrawingValueUnit.Inch}]}).Count==0,"physical linear identity converts units");
Check(DrawingMeasurementIdentityValidation.Validate(linearContext with{Dimensions=[linear1,linear2 with{BindingEvidence=linear2.BindingEvidence! with{Trace=linear2.BindingEvidence!.Trace! with{ModelDirection=new(0,1,0)}}}]}).Count==0,"different model directions are not merged by endpoint pair alone");
var wrongOwnership=linearContext with{Dimensions=[linear1,linear2 with{Value=140,SourceLiteral="140"}],GeometryAnchors=linearContext.GeometryAnchors.Select(a=>a.Id=="left-plan"?a with{PhysicalEntityId="unrelated-edge"}:a).ToArray()};
Check(DrawingMeasurementIdentityValidation.Validate(wrongOwnership).Any(d=>d.Code=="DIMENSION_CONSUMER_OWNERSHIP"),"same-value wrong physical ownership of one parameter across views rejected");
var corroborated=linearContext with{Dimensions=[linear1,linear2 with{Value=140,SourceLiteral="140",CorroboratesDimensionId=linear1.Id}]};
Check(DrawingMeasurementIdentityValidation.Validate(corroborated).Count==0,"explicit primary and cross-view corroboration accepted");
DrawingPlanContext Corroboration(DrawingDimensionFact f)=>corroborated with{Dimensions=[linear1,f]};
var cf=corroborated.Dimensions[1];
Check(DrawingMeasurementIdentityValidation.Validate(Corroboration(cf with{Value=141,SourceLiteral="141"})).Any(d=>d.Code=="DIMENSION_CORROBORATION_INVALID"),"conflicting corroboration rejected");
Check(DrawingMeasurementIdentityValidation.Validate(Corroboration(cf with{CorroboratesDimensionId=cf.Id})).Any(d=>d.Code=="DIMENSION_CORROBORATION_INVALID"),"self corroboration rejected");
Check(DrawingMeasurementIdentityValidation.Validate(Corroboration(cf with{ParameterPath="other"})).Any(d=>d.Code=="DIMENSION_CORROBORATION_INVALID"),"unrelated parameter corroboration rejected");
Check(DrawingMeasurementIdentityValidation.Validate(corroborated with{GeometryAnchors=wrongOwnership.GeometryAnchors}).Any(d=>d.Code=="DIMENSION_CORROBORATION_INVALID"),"same-value unrelated entity corroboration rejected");
var adoptionInput=N(new{drawing_context=corroborated,operations=new[]{new{id="fixture",fixture=new{width=140}}}});
Check(SourceWorkflowInputs.Prepare(adoptionInput).Input["operations"]![0]!["fixture"]!["width"]!["$dimension"]!.GetValue<string>()==linear1.Id,"public preparation selects explicit primary dimension");
var ambiguous=adoptionInput.DeepClone();ambiguous["drawing_context"]!["dimensions"]![1]!.AsObject().Remove("corroborates_dimension_id");
Reject(()=>SourceWorkflowInputs.Prepare(ambiguous),"public preparation still rejects undeclared duplicate consumers");
Reject(()=>Bind(circleArtifact,[S(dash,"dash")]),"multiple observed arcs require interval choice");
Reject(()=>Bind(circleArtifact,[S(dash,"dash") with{ArcIndex=0,Kind=DrawingAnchorKind.Circle}]),"dash interval cannot become circle");
var arcId=Id(Bind(circleArtifact,[S(dash,"dash") with{ArcIndex=0}]));var arc=CompactModelInputs.Decode<DrawingPlanContext>(store.LoadInput(arcId,"context")).GeometryAnchors.Single();
Check(arc.Kind==DrawingAnchorKind.Arc&&arc.SourcePath[0]!=arc.SourcePath[^1],"selected dash retains open observed interval");
Reject(()=>Bind(lineArtifact,[S(line,"bad") with{ProposalId="missing"}]),"unknown proposal rejected");
Reject(()=>Bind(lineArtifact,[S(line,"bad") with{Kind=DrawingAnchorKind.Circle}]),"line cannot become circle");
Reject(()=>Bind(lineArtifact,[S(line,"bad") with{ArcIndex=0}]),"line interval rejected");
Reject(()=>Bind(circleArtifact,[S(circle,"bad") with{ArcIndex=999}]),"invalid interval rejected");
Reject(()=>Bind(lineArtifact,[S(line,"dup"),S(line,"dup")]),"duplicate selection IDs rejected");
Reject(()=>Bind(lineArtifact,[S(line,"left")],boundId),"existing anchor IDs rejected");
Reject(()=>Bind(lineArtifact,[S(line,"bad") with{Evidence=""}]),"missing source evidence rejected");
Reject(()=>Bind(lineArtifact,[]),"empty selection rejected");
var other=N(SourceBindingTools.AddCrops(store,wid,J(new[]{new BindingCrop("extra",1,new(.1,.1,.9,.9))})));
Reject(()=>Bind(lineArtifact,[S(line,"bad")],workspace:other["workspace_id"]!.GetValue<string>()),"different workspace revision rejected");
var disabled=Id(CompactModelInputs.Store(store,"drawing_context",J(context with{RequireDimensionEvidence=false}),null,null));Reject(()=>Bind(lineArtifact,[S(line,"bad")],disabled),"disabled source gates rejected");
var mask=inventory.Regions.Single(r=>r.Purpose=="overview").InkMaskPath!;var bytes=File.ReadAllBytes(mask);
try{var modified=(byte[])bytes.Clone();modified[0]^=1;File.WriteAllBytes(mask,modified);Reject(()=>Bind(lineArtifact,[S(line,"bad")]),"modified source mask rejected");}finally{File.WriteAllBytes(mask,bytes);}
var proposalPath=lines["artifact_path"]!.GetValue<string>();var original=File.ReadAllBytes(proposalPath);
try{File.AppendAllText(proposalPath," ");Reject(()=>Bind(lineArtifact,[S(line,"bad")]),"modified proposal receipt rejected");}finally{File.WriteAllBytes(proposalPath,original);}
Save("result",new{success=true,count=checks.Count,checks});
