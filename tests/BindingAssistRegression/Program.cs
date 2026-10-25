using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CadModeling.Core;
using CadModeling.Drawing.Ingestion;
using CadModeling.Drawing.Providers.Abstractions;
using CadModeling.Ir;

var output=Path.GetFullPath(args[0]);Directory.CreateDirectory(output);
var checks=new List<string>();
void Check(bool ok,string name){if(!ok)throw new Exception(name);checks.Add(name);Console.WriteLine("PASS "+name);}
void Reject(Action action,string name){try{action();}catch(Exception e)when(e is ArgumentException or InvalidOperationException or IOException or InvalidDataException or JsonException){Check(true,name);return;}throw new Exception("Did not reject: "+name);}
JsonElement J<T>(T x)=>JsonSerializer.SerializeToElement(x,ModelingIrJson.Options);
JsonObject N(object x)=>JsonSerializer.SerializeToNode(x,ModelingIrJson.Options)!.AsObject();
string Id(object x)=>N(x)["input_id"]!.GetValue<string>();
void Save(string name,object x)=>File.WriteAllText(Path.Combine(output,name+".json"),JsonSerializer.Serialize(x,ModelingIrJson.Options));
var source=Path.Combine(output,"fixture.png");var pixels=Enumerable.Repeat((byte)255,400*300*4).ToArray();
void Pixel(int x,int y){int i=(y*400+x)*4;pixels[i]=pixels[i+1]=pixels[i+2]=160;}
for(int y=80;y<=180;y++){Pixel(100,y);Pixel(200,y);}
for(int x=100;x<=200;x++){Pixel(x,80);Pixel(x,160);}
BindingWorkspaceImages.Save(source,BitmapSource.Create(400,300,96,96,PixelFormats.Bgra32,null,pixels,400*4));
var ingestion=await new EngineeringDrawingIngestionService().IngestAsync(new DrawingIngestionRequest{InputPath=source,ArtifactRoot=Path.Combine(output,"ingestion")});
var inventory=DrawingOmissionValidation.Load(ingestion.OmissionInventoryPath!,ingestion.OmissionInventorySha256);
var sourceHash=BindingWorkspaceImages.Hash(source);var store=new CompactArtifactStore(Path.Combine(output,"store"),"binding-regression");
var crops=new[]{new BindingCrop("front",1,new(.2,.2,.6,.7))};
var prepared=N(SourceBindingTools.Prepare(store,ingestion.OmissionInventoryPath!,ingestion.OmissionInventorySha256!,Path.Combine(output,"workspace"),J(crops)));
var wid=prepared["workspace_id"]!.GetValue<string>();var w=store.LoadResult(wid).Deserialize<BindingWorkspace>(ModelingIrJson.Options)!;
var frame=w.Frames.Single(f=>f.Id=="front");
Check(w.Frames.Count==2&&frame.Left==80&&frame.Top==60&&frame.Width==160&&frame.Height==150,"overview retained and crop uses exact integer origin");
Check(BindingWorkspaceImages.Point(new(20,20),frame)==new DrawingPagePoint(.25,80/300d),"crop pixel -> original normalized coordinate");
Check(BindingWorkspaceImages.Point(new(160,150),frame)==new DrawingPagePoint(.6,.7),"far boundary coordinate maps without resize");
Check(BindingWorkspaceImages.Hash(source)==sourceHash,"original unchanged by crop/grid preparation");
var raw=BindingWorkspaceImages.Read(frame.ImagePath);var bytes=new byte[raw.PixelHeight*raw.PixelWidth*4];new FormatConvertedBitmap(raw,PixelFormats.Bgra32,null,0).CopyPixels(bytes,raw.PixelWidth*4,0);
Check(bytes[(20*160+20)*4]==160&&bytes[(21*160+21)*4]==255,"crop preserves original gray line and adjacent background");
var detailCrops=new[]{new BindingCrop("arrow-detail",1,new(.24,.25,.52,.62)),new BindingCrop("context-detail",1,new(.1,.1,.8,.8))};
var extended=N(SourceBindingTools.AddCrops(store,wid,J(detailCrops)));
var extendedId=extended["workspace_id"]!.GetValue<string>();
var extendedWorkspace=store.LoadResult(extendedId).Deserialize<BindingWorkspace>(ModelingIrJson.Options)!;
Check(extendedWorkspace.Frames.Count==4&&extended["frames"]!.AsArray().Count==2&&extended["reused_frame_count"]!.GetValue<int>()==2,"detail batch returns only new frames and reuses existing images");
Check(extendedWorkspace.Frames.Take(2).SequenceEqual(w.Frames)&&store.LoadResult(wid).Deserialize<BindingWorkspace>(ModelingIrJson.Options)!.Frames.Count==2,"extended workspace retains immutable original frames and handle");
var detail=extendedWorkspace.Frames.Single(f=>f.Id=="arrow-detail");
Check(BindingWorkspaceImages.Point(new(100-detail.Left,80-detail.Top),detail)==new DrawingPagePoint(.25,80/300d),"detail crop coordinates retain source pixel identity");
var detailImage=BindingWorkspaceImages.Read(detail.ImagePath);var detailBytes=new byte[detail.Width*detail.Height*4];new FormatConvertedBitmap(detailImage,PixelFormats.Bgra32,null,0).CopyPixels(detailBytes,detail.Width*4,0);
Check(detailBytes[((80-detail.Top)*detail.Width+100-detail.Left)*4]==160,"detail crop retains source pixel value without scaling");
Check(!extended["semantic_review_complete"]!.GetValue<bool>(),"detail rendering does not claim semantic review");
Reject(()=>SourceBindingTools.AddCrops(store,wid,J(Array.Empty<BindingCrop>())),"empty detail batch rejected");
Reject(()=>SourceBindingTools.AddCrops(store,wid,J(Enumerable.Range(0,13).Select(i=>new BindingCrop("d"+i,1,new(0,0,1,1))).ToArray())),"oversized detail batch rejected");
Reject(()=>SourceBindingTools.AddCrops(store,wid,J(crops)),"existing detail ID rejected");
Reject(()=>SourceBindingTools.AddCrops(store,wid,J(new[]{detailCrops[0],detailCrops[0]})),"duplicate IDs in detail batch rejected");
Reject(()=>SourceBindingTools.AddCrops(store,wid,J(new[]{new BindingCrop("bad-detail",2,new(0,0,1,1))})),"missing detail source page rejected");
Reject(()=>SourceBindingTools.AddCrops(store,wid,J(new[]{new BindingCrop("bad-detail",1,new(-.1,0,1,1))})),"out of page detail bounds rejected");
Reject(()=>SourceBindingTools.AddCrops(store,wid,J(new[]{new BindingCrop("../escape",1,new(0,0,1,1))})),"detail path traversal rejected");
Reject(()=>BindingWorkspaceImages.Point(new(-1,0),frame),"negative local coordinate rejected");
Reject(()=>BindingWorkspaceImages.Point(new(161,0),frame),"wrong resized-frame coordinate rejected");
Reject(()=>BindingWorkspaceImages.Point(new(double.NaN,0),frame),"nonfinite local coordinate rejected");
Reject(()=>BindingWorkspaceImages.Box(new(5,5,4,9),frame),"reversed bounds rejected");
Reject(()=>BindingWorkspaceImages.Prepare(ingestion.OmissionInventoryPath!,new string('0',64),Path.Combine(output,"bad-hash"),[]),"incorrect inventory hash rejected");
Reject(()=>BindingWorkspaceImages.Prepare(ingestion.OmissionInventoryPath!,ingestion.OmissionInventorySha256!,Path.Combine(output,"workspace"),[]),"existing output directory not overwritten");
Reject(()=>BindingWorkspaceImages.Prepare(ingestion.OmissionInventoryPath!,ingestion.OmissionInventorySha256!,Path.Combine(output,"bad-id"),[new("../escape",1,new(0,0,1,1))]),"crop path traversal rejected");
var context=new DrawingPlanContext{SourcePath=source,WorkflowRevision=SourceWorkflow.Revision,RequireCompleteBindings=true,RequireGeometryAnchors=true,RequireDimensionEvidence=true,RequireInterpretationReview=true,RequireShapeDecomposition=true,Views=[new(){Id="front",Kind=ModelDrawingView.Front}],
    Features=[new(){Id="plate",SourceLiteral="plate"}],OmissionReview=new(){InventoryPath=ingestion.OmissionInventoryPath!,InventorySha256=ingestion.OmissionInventorySha256!},
    AnnotationLedger=new(){SourceSha256=sourceHash,Sweeps=[new(){RegionId=inventory.Regions[0].Id,Pass=2,State=DrawingReviewState.Reviewed,AnnotationIds=[],Evidence="old reverse sweep"}]}};
var baseId=Id(CompactModelInputs.Store(store,"drawing_context",J(context),null,null));
var disabledId=Id(CompactModelInputs.Store(store,"drawing_context",J(context with{RequireDimensionEvidence=false}),null,null));
var batch=new BindingBatch{FrameId="front",ViewId="front",FeatureId="plate",Datum="left outer edge",Anchors=[
    new(){Id="left",PhysicalEntityId="left",Kind=DrawingAnchorKind.Line,Bounds=new(18,18,22,122),SourcePath=[new(20,20),new(20,120)],State=DrawingReviewState.Reviewed,Evidence="actual left source stroke"},
    new(){Id="right",PhysicalEntityId="right",Kind=DrawingAnchorKind.Line,Bounds=new(118,18,122,122),SourcePath=[new(120,20),new(120,120)],State=DrawingReviewState.Reviewed,Evidence="actual right source stroke"}],
    Annotations=[new(){Id="ann-width",OperationId="sk",SourceLiteral="25",TextBounds=new(55,90,80,110),Evidence="source literal 25",Dimensions=[new(){Id="width",ComponentKey="linear",Value=25,ParameterPath="primitives.0.width_mm",QuantityKind=DrawingQuantityKind.Linear,TargetBounds=new(18,18,122,122),StartAnchorId="left",EndAnchorId="right",StartReference="outer left",EndReference="outer right",AttachmentExplanation="dimension line touches extensions",Trace=new(){Kind=DrawingDimensionTraceKind.LinearDimension,MeasurementMode=DrawingMeasurementMode.Horizontal,ModelDirection=new(1,0,0),Path=[new(20,100),new(120,100)],StartExtension=[new(20,20),new(20,100)],EndExtension=[new(120,20),new(120,100)],Evidence="actual gray strokes"}}]}]};
var bound=N(SourceBindingTools.Bind(store,baseId,"drawing_context",wid,J(batch)));var boundId=Id(bound);var actual=CompactModelInputs.Decode<DrawingPlanContext>(store.LoadInput(boundId,"context"));
Reject(()=>SourceBindingTools.Bind(store,disabledId,"drawing_context",wid,J(batch)),"disabled accuracy gate rejected by assistant");
Check(!bound["compiled"]!.GetValue<bool>()&&bound["status"]!.GetValue<string>()=="stored_not_validated","binding assembly does not claim compilation");
Check(actual.AnnotationLedger!.Annotations[0].State==DrawingReviewState.Unresolved,"omitted review state remains Unresolved");
Check(actual.AnnotationLedger.Sweeps[0].State==DrawingReviewState.Unresolved&&actual.AnnotationLedger.Sweeps[0].Evidence=="old reverse sweep","new evidence invalidates reverse sweep without deleting evidence");
Check(actual.RequireCompleteBindings&&actual.RequireGeometryAnchors&&actual.RequireDimensionEvidence&&actual.RequireInterpretationReview&&actual.RequireShapeDecomposition,"all five source gates remain enabled");
Check(actual.Dimensions.Single().Value==25&&actual.Dimensions.Single().SourceLiteral=="25"&&actual.Dimensions.Single().ParameterPath=="primitives.0.width_mm","explicit nominal value literal and consumer are preserved");
Check(actual.Dimensions.Single().BindingEvidence!.Trace!.Path[1]==new DrawingPagePoint(.5,160/300d),"trace points transformed from crop rather than full-page guess");
Check(actual.AnnotationLedger.Annotations[0].Components[0].DimensionId=="width"&&actual.Dimensions[0].SourceAnnotationId=="ann-width","ledger component and dimension reverse reference generated consistently");
Check(store.LoadInput(baseId,"context")["dimensions"]!.AsArray().Count==0,"base input immutable");
Check(Id(SourceBindingTools.Bind(store,baseId,"drawing_context",wid,J(batch)))==boundId,"same binding batch produces same immutable input");
var traceErrors=DrawingDimensionTraceValidation.Validate(actual);Save("source-trace-diagnostics",traceErrors);
Check(traceErrors.Count==0,"existing source ink and endpoint validator accepts transformed geometry");
var displaced=actual with {Dimensions=[actual.Dimensions[0] with {BindingEvidence=actual.Dimensions[0].BindingEvidence! with{Trace=actual.Dimensions[0].BindingEvidence!.Trace! with{Path=[new(.25,.72),new(.5,.72)]}}}]};
var errors=DrawingDimensionTraceValidation.Validate(displaced);
Check(errors.Any(e=>e.Code=="DIMENSION_TRACE_INK"),"existing gate rejects displaced leader on white background");
var report=BindingErrorImages.Render(w,displaced,errors.ToArray(),Path.Combine(output,"errors"));Save("error-review",report);
Check(report.ImageCount==1&&report.Regions[0].ImagePath is {} ip&&File.Exists(ip)&&!report.SemanticReviewComplete,"actual diagnostic generates local overlay without review claim");
var unresolved=BindingErrorImages.Render(w,actual,[new("UNLOCATED",DiagnosticSeverity.Error,"global failure","verification")],Path.Combine(output,"unlocated"));
Check(unresolved.Diagnostics.Count==1&&unresolved.Regions[0].Status=="read_complete_diagnostics","unlocalized errors are retained");
Reject(()=>SourceBindingTools.Bind(store,boundId,"drawing_context",wid,J(batch)),"duplicate annotation/dimension/anchor IDs rejected");
Reject(()=>SourceBindingTools.Bind(store,baseId,"drawing_context",wid,J(batch with{ViewId="unknown"})),"unknown view rejected");
Reject(()=>SourceBindingTools.Bind(store,baseId,"drawing_context",wid,J(batch with{FeatureId="unknown"})),"unknown feature rejected");
Reject(()=>SourceBindingTools.Bind(store,baseId,"drawing_context",wid,J(batch with{FrameId="unknown"})),"unknown coordinate frame rejected");
Reject(()=>SourceBindingTools.Bind(store,baseId,"drawing_context",wid,J(batch with{Anchors=[]})),"missing referenced anchors rejected");
var typo=N(batch);typo["invented_field"]=true;Reject(()=>SourceBindingTools.Bind(store,baseId,"drawing_context",wid,J(typo)),"unknown assistant fields rejected");
var other=context with{SourcePath=Path.Combine(output,"other.png")};File.Copy(source,other.SourcePath);
var otherId=Id(CompactModelInputs.Store(store,"drawing_context",J(other),null,null));Reject(()=>SourceBindingTools.Bind(store,otherId,"drawing_context",wid,J(batch)),"different source path rejected even with identical bytes");
var badw=w with{Frames=w.Frames.Select(f=>f.Id=="front"?f with{PageWidth=399}:f).ToArray()};Reject(()=>BindingWorkspaceImages.Validate(badw),"invalid page metric rejected");
File.AppendAllText(frame.GridPath,"changed");Reject(()=>SourceBindingTools.Bind(store,baseId,"drawing_context",wid,J(batch)),"changed grid invalidates workspace");
Save("result",new{success=true,count=checks.Count,checks,source_hash=sourceHash});
