using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CadModeling.Core;
using CadModeling.Ir;
using CadModeling.Drawing.Ingestion;
using CadModeling.Drawing.Providers.Abstractions;

var output=Path.GetFullPath(args[0]);Directory.CreateDirectory(output);var checks=new List<string>();
void Check(bool ok,string name){if(!ok)throw new Exception(name);checks.Add(name);Console.WriteLine("PASS "+name);}
JsonElement J<T>(T x)=>JsonSerializer.SerializeToElement(x,ModelingIrJson.Options);
JsonObject N(object x)=>JsonSerializer.SerializeToNode(x,ModelingIrJson.Options)!.AsObject();
void Reject(Action action,string name){try{action();}catch(Exception e)when(e is ArgumentException or InvalidOperationException or JsonException){Check(true,name);return;}throw new Exception("Did not reject: "+name);}
var pixels=Enumerable.Repeat((byte)255,400*300*4).ToArray();
void Pixel(int x,int y){int i=(y*400+x)*4;pixels[i]=pixels[i+1]=pixels[i+2]=160;}
for(int y=80;y<=180;y++){Pixel(100,y);Pixel(200,y);}
for(int x=100;x<=200;x++){Pixel(x,80);Pixel(x,160);}
var source=Path.Combine(output,"fixture.png");BindingWorkspaceImages.Save(source,BitmapSource.Create(400,300,96,96,PixelFormats.Bgra32,null,pixels,400*4));
var read=await new EngineeringDrawingIngestionService().IngestAsync(new DrawingIngestionRequest{InputPath=source,ArtifactRoot=Path.Combine(output,"ingestion")});
var store=new CompactArtifactStore(Path.Combine(output,"store"),"binding-sheet-regression");
var prepared=N(SourceBindingTools.Prepare(store,read.OmissionInventoryPath!,read.OmissionInventorySha256!,Path.Combine(output,"workspace"),J(new[]{new BindingCrop("front",1,new(.2,.2,.6,.7))})));
var wid=prepared["workspace_id"]!.GetValue<string>();var workspace=store.LoadResult(wid).Deserialize<BindingWorkspace>(ModelingIrJson.Options)!;
var inventory=DrawingOmissionValidation.Load(read.OmissionInventoryPath!,read.OmissionInventorySha256);
var evidenceIds=inventory.Candidates.Where(c=>c.Kind==DrawingCandidateKind.ResidualInk).Select(c=>c.Id).ToArray();
var context=new DrawingPlanContext{SourcePath=source,WorkflowRevision=SourceWorkflow.Revision,RequireCompleteBindings=true,RequireGeometryAnchors=true,RequireDimensionEvidence=true,RequireInterpretationReview=true,RequireShapeDecomposition=true,
    Views=[new(){Id="front",Kind=ModelDrawingView.Front,PageNumber=1}],Features=[new(){Id="plate",SourceLiteral="source plate",ViewIds=["front"],OperationIds=["sk"]}],
    OmissionReview=new(){InventoryPath=read.OmissionInventoryPath!,InventorySha256=read.OmissionInventorySha256!},
    AnnotationLedger=new(){SourceSha256=BindingWorkspaceImages.Hash(source),Sweeps=[new(){RegionId=inventory.Regions[0].Id,Pass=1,State=DrawingReviewState.Reviewed,AnnotationIds=["aw"],Evidence="actual first census"},new(){RegionId=inventory.Regions[0].Id,Pass=2,State=DrawingReviewState.Reviewed,AnnotationIds=["aw"],Evidence="old reverse evidence"}]}};
var draft=JsonNode.Parse("""{"name":"sheet fixture","source_text":"source 25 mm width","operations":[{"id":"sk","type":"ProfileSketch","plane":"Front","primitives":[{"type":"CenteredRectangle","width_mm":25,"height_mm":20,"center_xmm":12.5,"center_ymm":10}],"require_editable":true,"require_fully_defined":true}],"verification":{"bounds":[{"id":"bounds","source_literal":"independent source envelope 25 by 20 by 4","size_mm":{"x":25,"y":20,"z":4},"tolerance_mm":0.01}]}}""")!.AsObject();
draft["drawing_context"]=JsonSerializer.SerializeToNode(context,ModelingIrJson.Options);
var attachment=new BindingAttachment{TargetBounds=new(18,18,122,122),StartAnchorId="left",EndAnchorId="right",StartReference="outer left",EndReference="outer right",Explanation="Actual arrow extensions between the source edges",ObservationIds=[],
    Trace=new(){Kind=DrawingDimensionTraceKind.LinearDimension,MeasurementMode=DrawingMeasurementMode.Horizontal,ModelDirection=new(1,0,0),Path=[new(20,100),new(120,100)],StartExtension=[new(20,20),new(20,100)],EndExtension=[new(120,20),new(120,100)],Evidence="actual gray strokes"}};
var anchors=new BindingAnchorInput[]{new(){Id="left",PhysicalEntityId="left",Kind=DrawingAnchorKind.Line,Bounds=new(18,18,22,122),SourcePath=[new(20,20),new(20,120)],State=DrawingReviewState.Reviewed,CandidateIds=evidenceIds,Evidence="left stroke"},new(){Id="right",PhysicalEntityId="right",Kind=DrawingAnchorKind.Line,Bounds=new(118,18,122,122),SourcePath=[new(120,20),new(120,120)],State=DrawingReviewState.Reviewed,CandidateIds=evidenceIds,Evidence="right stroke"}};
var dimension=new BindingSheetValue{Id="width",ComponentKey="linear",ParameterPath="primitives[0].width_mm",Checks=[new("bounds","size_mm.x")]};
var annotation=new BindingSheetAnnotation{Id="aw",SourceLiteral="25",OperationId="sk",TextBounds=new(55,90,80,110),Evidence="Read source literal and traced its exact endpoints",Attachment=attachment,Dimensions=[dimension]};
var group=new BindingSheetGroup{FrameId="front",ViewId="front",FeatureId="plate",Datum="left side",Anchors=anchors,Annotations=[annotation]};
var sheet=new BindingSheet{Groups=[group]};var original=draft.ToJsonString();
var assembled=BindingSheetAssembler.Prepare(draft,J(sheet),workspace);var c=CompactModelInputs.Decode<DrawingPlanContext>(assembled.Draft["drawing_context"]!);
Check(draft.ToJsonString()==original,"base draft remains byte-equivalent after assembly");
Check(assembled.Annotations==1&&assembled.Dimensions==1&&assembled.Anchors==2,"one transcript expands once into original contracts");
Check(assembled.NormalizedPaths.Count==1&&assembled.NormalizedPaths[0].Original=="primitives[0].width_mm"&&assembled.NormalizedPaths[0].Canonical=="primitives.0.width_mm","bracket index canonicalization retains explicit audit receipt");
Check(c.Dimensions[0].Value==25&&c.Dimensions[0].Unit==DrawingValueUnit.Millimeter&&c.Dimensions[0].BindingEvidence!.QuantityKind==DrawingQuantityKind.Linear,"value unit and quantity derive from exact original parser");
Check(c.AnnotationLedger!.Annotations[0].State==DrawingReviewState.Unresolved,"omitted review state is never auto-approved");
Check(c.AnnotationLedger.Sweeps[0].State==DrawingReviewState.Reviewed&&c.AnnotationLedger.Sweeps[1].State==DrawingReviewState.Unresolved&&c.AnnotationLedger.Sweeps[1].Evidence=="old reverse evidence","first census preserved and reverse pass invalidated with evidence retained");
Check(c.Features[0].CriticalParameters.Single().DimensionId=="width"&&c.Features[0].VerificationCheckIds.SequenceEqual(new[]{"bounds"}),"critical consumer and explicit independent check link are generated");
Check(assembled.Draft["verification"]!["bindings"]![0]!["dimension_id"]!.GetValue<string>()=="width"&&assembled.Draft["verification"]!["bounds"]![0]!["source_dimension_ids"]![0]!.GetValue<string>()=="width","check binding and source dimension reference agree");
Check(JsonNode.DeepEquals(draft["operations"],assembled.Draft["operations"])&&JsonNode.DeepEquals(draft["verification"]!["bounds"]![0]!["size_mm"],assembled.Draft["verification"]!["bounds"]![0]!["size_mm"]),"operation and independent check values are not inferred or rewritten");
Check(DrawingDimensionTraceValidation.Validate(c).Count==0,"assembled pixels still pass original source-ink and endpoint validator");
Check(c.RequireCompleteBindings&&c.RequireDimensionEvidence&&c.RequireGeometryAnchors&&c.RequireInterpretationReview&&c.RequireShapeDecomposition,"all original source gates remain true");
var receipt=N(SourceBindingTools.Assemble(store,wid,J(sheet),J(draft)));
Check(receipt["success"]!.GetValue<bool>()&&!receipt["compiled"]!.GetValue<bool>()&&receipt["status"]!.GetValue<string>()=="stored_not_validated","public assembler stores once without claiming compile acceptance");
var inputId=receipt["input_id"]!.GetValue<string>();
Check(N(SourceBindingTools.Assemble(store,wid,J(sheet),J(draft)))["input_id"]!.GetValue<string>()==inputId,"same complete sheet creates same immutable artifact");
var missing=draft.DeepClone().AsObject();missing.Remove("source_text");var bad=N(SourceBindingTools.Assemble(store,wid,J(sheet),J(missing)));
Check(!bad["success"]!.GetValue<bool>()&&!bad["input_stored"]!.GetValue<bool>()&&bad["diagnostics"]![0]!["path"]!.GetValue<string>()=="/draft/source_text","missing source_text reports exact field before storage");
var countsBefore=Directory.GetFiles(Path.Combine(output,"store"),"*",SearchOption.AllDirectories).Length;
var late=N(SourceBindingTools.Assemble(store,wid,J(new BindingSheet{Groups=[group,group]}),J(draft)));
Check(!late["success"]!.GetValue<bool>()&&Directory.GetFiles(Path.Combine(output,"store"),"*",SearchOption.AllDirectories).Length==countsBefore&&draft.ToJsonString()==original,"late duplicate group fails atomically without storing partial inputs");
var typo=N(sheet);typo["groups"]![0]!["invented_field"]=true;bad=N(SourceBindingTools.Assemble(store,wid,J(typo),J(draft)));
Check(!bad["success"]!.GetValue<bool>()&&bad["diagnostics"]![0]!["message"]!.GetValue<string>().Contains("invented_field"),"unknown sheet member rejected with actual field name");
foreach(var gate in new[]{"require_complete_bindings","require_dimension_evidence","require_geometry_anchors","require_interpretation_review","require_shape_decomposition"})
{var x=draft.DeepClone();x["drawing_context"]![gate]=false;bad=N(SourceBindingTools.Assemble(store,wid,J(sheet),J(x)));Check(!bad["success"]!.GetValue<bool>(),"disabled "+gate+" rejected");}
BindingSheet Modify(BindingSheetValue value)=>sheet with{Groups=[group with{Annotations=[annotation with{Dimensions=[value]}]}]};
Reject(()=>BindingSheetAssembler.Prepare(draft,J(Modify(dimension with{ComponentKey="radius"})),workspace),"wrong literal component cannot masquerade as radius");
Reject(()=>BindingSheetAssembler.Prepare(draft,J(Modify(dimension with{Checks=[]})),workspace),"missing independent check reference rejected");
Reject(()=>BindingSheetAssembler.Prepare(draft,J(Modify(dimension with{ParameterPath="primitives[-1].width_mm"})),workspace),"negative array index rejected");
Reject(()=>BindingSheetAssembler.Prepare(draft,J(Modify(dimension with{ParameterPath="primitives[00].width_mm"})),workspace),"leading-zero index ambiguity rejected");
Reject(()=>BindingSheetAssembler.Prepare(draft,J(Modify(dimension with{ParameterPath="primitives[4].width_mm"})),workspace),"nonexistent operation slot localized before compile");
Reject(()=>BindingSheetAssembler.Prepare(draft,J(Modify(dimension with{Checks=[new("bounds","size_mm.missing")]})),workspace),"nonexistent check slot localized before compile");
Reject(()=>BindingSheetAssembler.Prepare(draft,J(sheet with{Groups=[group with{Annotations=[annotation with{SourceLiteral="25 UNKNOWN"}]}]}),workspace),"unknown literal suffix cannot be discarded");
var conflict=draft.DeepClone();conflict["verification"]!["bindings"]=JsonNode.Parse("""[{"dimension_id":"unrelated","check_id":"bounds","parameter_path":"size_mm.x"}]""");
Reject(()=>BindingSheetAssembler.Prepare(conflict,J(sheet),workspace),"same-valued different source fact cannot share a check slot by inference");
var future=draft.DeepClone();future["operations"]![0]!["primitives"]![0]!["width_mm"]=JsonNode.Parse("""{"$dimension":"width","unit":"Millimeter"}""");
Check(N(SourceBindingTools.Assemble(store,wid,J(sheet),J(future)))["success"]!.GetValue<bool>(),"future explicit dimension references resolve after single atomic sheet assembly");
foreach(var pointer in new[]{"groups","attachment","dimensions","text_bounds"})
{
    var x=N(sheet);
    if(pointer=="groups")x["groups"]=null;else x["groups"]![0]!["annotations"]![0]![pointer]=null;
    bad=N(SourceBindingTools.Assemble(store,wid,J(x),J(draft)));
    Check(!bad["success"]!.GetValue<bool>()&&!bad["input_stored"]!.GetValue<bool>(),"null "+pointer+" returns input diagnostic without partial storage");
}
// Compound assembly checks transcript identity and independent targets only; these
// circle declarations are not native/source-ink acceptance fixtures.
var hd=draft.DeepClone().AsObject();
hd["operations"]=JsonNode.Parse("""[{"id":"holes","type":"NativeFeature","feature":{"kind":"Hole","hole_kind":"Counterbore","hole_center_count":4,"diameter_mm":6,"counterbore_diameter_mm":10,"counterbore_depth_mm":3,"through_all":true}}]""");
hd["verification"]=JsonNode.Parse("""{"cylinder_groups":[{"id":"bore-check","source_literal":"independently transcribed four diameter 6 bores","diameter_mm":6,"expected_count":4},{"id":"cb-check","source_literal":"independent outer diameter 10 depth 3","diameter_mm":10,"length_mm":3}]}""");
hd["drawing_context"]!["features"]=JsonSerializer.SerializeToNode(new[]{new DrawingFeatureRequirement{Id="bores",SourceLiteral="4 bores",ViewIds=["front"],OperationIds=["holes"]}},ModelingIrJson.Options);
var circleAnchor=new BindingAnchorInput{Id="bore",PhysicalEntityId="bore",Kind=DrawingAnchorKind.Circle,Bounds=new(65,65,85,85),SourcePath=[new(65,75),new(75,65),new(85,75),new(75,85),new(65,75)],CandidateIds=evidenceIds,Evidence="transcribed bore circle"};
var circleTarget=new BindingAttachment{TargetBounds=circleAnchor.Bounds,TargetAnchorId="bore",Explanation="Original compound callout refers to these distinct inner and counterbore circles",Trace=new(){Kind=DrawingDimensionTraceKind.Leader,Path=[new(100,50),new(85,75)],Evidence="transcribed leader"}};
var compound=new BindingSheetAnnotation{Id="hole-ann",SourceLiteral="4×Ø6 THRU CBORE Ø10 DEPTH 3",OperationId="holes",TextBounds=new(90,30,150,50),Evidence="transcribed complete callout; not auto-reviewed",Attachment=circleTarget,LeaderTrace=circleTarget.Trace,LeaderTargetAnchorId="bore",Dimensions=[
    new(){Id="count",ComponentKey="count",ParameterPath="feature.hole_center_count",Checks=[new("bore-check","expected_count")]},
    new(){Id="diameter",ComponentKey="bore_diameter",ParameterPath="feature.diameter_mm",Checks=[new("bore-check","diameter_mm")]},
    new(){Id="counterdiameter",ComponentKey="counterbore_diameter",ParameterPath="feature.counterbore_diameter_mm",Attachment=new(){TargetAnchorId="outer",TargetBounds=new(55,55,95,95)},Checks=[new("cb-check","diameter_mm")]},
    new(){Id="depth",ComponentKey="counterbore_depth",ParameterPath="feature.counterbore_depth_mm",Attachment=new(){TargetAnchorId="outer",TargetBounds=new(55,55,95,95)},Checks=[new("cb-check","length_mm")]}]};
var hs=new BindingSheet{Groups=[group with{FeatureId="bores",Anchors=[circleAnchor,circleAnchor with{Id="outer",PhysicalEntityId="outer",Bounds=new(55,55,95,95)}],Annotations=[compound]}]};
var hc=CompactModelInputs.Decode<DrawingPlanContext>(BindingSheetAssembler.Prepare(hd,J(hs),workspace).Draft["drawing_context"]!);
Check(hc.Dimensions.Select(x=>x.Value).SequenceEqual(new double[]{4,6,10,3})&&hc.Dimensions[0].Unit==DrawingValueUnit.Unitless,"compound count bore diameter counterbore diameter and depth remain distinct");
Check(hc.Dimensions[2].BindingEvidence!.TargetAnchorId=="outer"&&hc.Dimensions[1].BindingEvidence!.TargetAnchorId=="bore","per-component target override preserves distinct physical circles");
Check(hc.AnnotationLedger!.Annotations[0].Components.Where(x=>x.DimensionId is null).Select(x=>x.RetainedLiteral).SequenceEqual(new[]{"THRU","CBORE"}),"compound nonnumeric metadata retains exact original spans");
Reject(()=>BindingSheetAssembler.Prepare(hd,J(hs with{Groups=[hs.Groups[0] with{Annotations=[compound with{Dimensions=compound.Dimensions.Take(3).ToArray()}]}]}),workspace),"missing counterbore depth cannot be silently retained or discarded");
// The authoritative core permits same-view start/end datums from another feature.
var cross=c with{Features=c.Features.Concat(new[]{new DrawingFeatureRequirement{Id="other",SourceLiteral="other measured feature",ViewIds=["front"]}}).ToArray(),Dimensions=[]};
var crossId=N(CompactModelInputs.Store(store,"drawing_context",J(cross),null,null))["input_id"]!.GetValue<string>();
var oldValue=new BindingValueInput{Id="cross-width",ComponentKey="linear",Value=25,ParameterPath="primitives.0.width_mm",QuantityKind=DrawingQuantityKind.Linear,TargetBounds=attachment.TargetBounds!,StartAnchorId="left",EndAnchorId="right",StartReference="outer left",EndReference="outer right",Trace=attachment.Trace,AttachmentExplanation="same-view measured reference to plate",ObservationIds=[]};
var crossBatch=new BindingBatch{FrameId="front",ViewId="front",FeatureId="other",Datum="plate left",Annotations=[new(){Id="cross-ann",OperationId="other-sk",SourceLiteral="25",TextBounds=annotation.TextBounds,Evidence="same source datum",Dimensions=[oldValue]}]};
var crossBound=N(SourceBindingTools.Bind(store,crossId,"drawing_context",wid,J(crossBatch)));
var crossContext=CompactModelInputs.Decode<DrawingPlanContext>(store.LoadInput(crossBound["input_id"]!.GetValue<string>(),"context"));
Check(!DrawingGeometryAnchorValidation.Validate(crossContext).Any(e=>e.Code=="ANCHOR_BINDING"),"same-view cross-feature start/end supported exactly as core validator permits");
Reject(()=>SourceBindingTools.Bind(store,crossId,"drawing_context",wid,J(crossBatch with{Annotations=[crossBatch.Annotations[0] with{Dimensions=[oldValue with{TargetAnchorId="left"}]}]})),"cross-feature target remains forbidden");
// Contract names and concise card are stable, with no native execution claim.
Check(CompactModelingTools.GetContract("binding_sheet")["properties"]?["groups"] is not null,"new complete sheet schema available");
Check(N(SourceBindingTools.Guide())["non_model_categories"]!.AsArray().Count==7&&!N(SourceBindingTools.Guide())["native_execution"]!.GetValue<bool>(),"concise guide exposes supported classifications and correct scope");
File.WriteAllText(Path.Combine(output,"result.json"),JsonSerializer.Serialize(new{success=true,count=checks.Count,checks,native_execution=false},ModelingIrJson.Options));
