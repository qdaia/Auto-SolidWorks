using System.IO;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CadModeling.Core;
using CadModeling.Drawing.Ingestion;
using CadModeling.Drawing.Providers.Abstractions;
using CadModeling.Ir;

if(args[0]=="--draft")
{
    var prepared=SourceWorkflowInputs.Prepare(System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(args[1]))!);
    var draft=CompactModelInputs.Decode<GenericModelDraft>(prepared.Input);
    var compiled=new GenericPlanCompiler().Compile(draft,args[2]);
    File.WriteAllText(args[3],JsonSerializer.Serialize(compiled,ModelingIrJson.Options));
    foreach(var d in compiled.Diagnostics)Console.WriteLine(d.Code+" "+d.Path+" "+d.Message);
    Console.WriteLine("plan="+(compiled.Plan is not null));return;
}

var output=Path.GetFullPath(args[0]);Directory.CreateDirectory(output);
var checks=new List<string>();
void Check(bool ok,string name){if(!ok)throw new Exception(name);checks.Add(name);Console.WriteLine("PASS "+name);}
void Save(string name,object value)=>File.WriteAllText(Path.Combine(output,name),JsonSerializer.Serialize(value,ModelingIrJson.Options));
const int size=301;
var pixels=Enumerable.Repeat((byte)255,size*size*4).ToArray();
void Pixel(int x,int y,byte gray){var n=(y*size+x)*4;pixels[n]=pixels[n+1]=pixels[n+2]=gray;}
void Line(int x1,int y1,int x2,int y2,byte gray=0)
{var count=Math.Max(Math.Abs(x2-x1),Math.Abs(y2-y1));for(var i=0;i<=count;i++)Pixel(x1+(x2-x1)*i/Math.Max(1,count),y1+(y2-y1)*i/Math.Max(1,count),gray);}
Line(60,80,60,150);Line(75,160,240,160);Line(60,150,75,160); // actual chamfer and two finite support edges
Line(100,80,100,160,191);Line(140,80,140,160,191); // gray walls: NO drawn axis
Line(60,170,60,200,191);Line(120,170,120,200,191);Line(60,200,120,200,191);
Line(220,30,220,60,220);Line(245,30,245,60,0); // faint line and dark control
// Uniform gray and near-white low contrast must not become foreground.
for(var y=230;y<280;y++)for(var x=10;x<50;x++)Pixel(x,y,191);
Line(270,30,270,60,250);
var source=Path.Combine(output,"fixture.png");
var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(size,size,96,96,PixelFormats.Bgra32,null,pixels,size*4)));
using(var stream=File.Create(source))encoder.Save(stream);
var original=DrawingPlanValidation.FileHash(source);
var ingestion=await new EngineeringDrawingIngestionService().IngestAsync(new DrawingIngestionRequest{InputPath=source,ArtifactRoot=Path.Combine(output,"ingestion")});
Save("ingestion.json",ingestion);
var inventory=DrawingOmissionValidation.Load(ingestion.OmissionInventoryPath!,ingestion.OmissionInventorySha256);
var region=inventory.Regions.Single(r=>r.Purpose=="overview");var mask=File.ReadAllBytes(region.InkMaskPath!);
bool Ink(int x,int y){var n=y*size+x;return(mask[n/8]&(1<<(n%8)))!=0;}
Check(region.PixelWidth==size && region.PixelHeight==size,"source ink keeps exact original coordinate dimensions");
Check(region.InkMaskAlgorithm?.StartsWith("source-local-contrast-1")==true,"inventory records source mask algorithm");
Check(Enumerable.Range(80,81).All(y=>Ink(100,y)&&Ink(140,y)),"all 162 gray191 wall pixels survive real ingestion");
Check(Enumerable.Range(30,31).All(y=>Ink(220,y)&&Ink(245,y)),"gray220 and black control strokes survive");
Check(!Ink(120,120)&&!Ink(30,255)&&!Ink(270,45)&&!Ink(290,290),"implicit axis, uniform gray, near-white and white remain blank");
Check(DrawingPlanValidation.FileHash(source)==original,"source file unchanged by mask extraction");
DrawingPagePoint P(int x,int y)=>new(x/300d,y/300d);
DrawingGeometryAnchor LineAnchor(string id,string feature,int x1,int y1,int x2,int y2)=>new(){Id=id,FeatureId=feature,PhysicalEntityId=id,ViewId="front",Kind=DrawingAnchorKind.Line,State=DrawingReviewState.Reviewed,
    Bounds=new((Math.Min(x1,x2)-3)/300d,(Math.Min(y1,y2)-3)/300d,(Math.Max(x1,x2)+3)/300d,(Math.Max(y1,y2)+3)/300d),SourcePath=[P(x1,y1),P(x2,y2)],Evidence="independent known source stroke"};
var left=LineAnchor("left","plate",60,80,60,150);var bottom=LineAnchor("bottom","plate",75,160,240,160);
var wall1=LineAnchor("wall1","holes",100,80,100,160);var wall2=LineAnchor("wall2","holes",140,80,140,160);
var axis=wall1 with{Id="axis",PhysicalEntityId="hole-axis",Kind=DrawingAnchorKind.Axis,Bounds=new(.31,.25,.48,.58),SourcePath=[P(120,80),P(120,170)],Construction=new(){Kind=DrawingAnchorConstructionKind.ParallelMidline,SupportAnchorIds=["wall1","wall2"],Evidence="projected bore walls prove the implicit mid-axis"}};
var corner=left with{Id="corner",PhysicalEntityId="virtual-corner",Kind=DrawingAnchorKind.Point,Bounds=new(.18,.25,.82,.55),SourcePath=[P(60,160),P(60,160)],Construction=new(){Kind=DrawingAnchorConstructionKind.LineIntersection,SupportAnchorIds=["left","bottom"],Evidence="finite sharp intersection before corner finishing"}};
var trace=new DrawingDimensionTrace{Kind=DrawingDimensionTraceKind.LinearDimension,MeasurementMode=DrawingMeasurementMode.Horizontal,ModelDirection=new(1,0,0),Path=[P(60,200),P(120,200)],StartExtension=[P(60,160),P(60,200)],EndExtension=[P(120,170),P(120,200)],Evidence="real gray dimension and extension strokes"};
var fact=new DrawingDimensionFact{Id="position",OperationId="holes",ParameterPath="feature.hole_centers.0.xmm",Value=15,SourceLiteral="15",BindingEvidence=new(){FeatureId="holes",MeasurementId="x",ViewId="front",QuantityKind=DrawingQuantityKind.Linear,TextBounds=new(.27,.64,.33,.69),TargetBounds=new(.18,.25,.48,.58),Datum="outer theoretical corner",AttachmentExplanation="dimension to implied bore axis",StartAnchorId="corner",EndAnchorId="axis",Trace=trace}};
var context=new DrawingPlanContext{SourcePath=source,WorkflowRevision=SourceWorkflow.Revision,Views=[new(){Id="front",Kind=ModelDrawingView.Front}],Dimensions=[fact],GeometryAnchors=[left,bottom,wall1,wall2,corner,axis],OmissionReview=new(){InventoryPath=ingestion.OmissionInventoryPath!,InventorySha256=ingestion.OmissionInventorySha256!}};
void Good(DrawingPlanContext c,string name){var d=DrawingDimensionTraceValidation.Validate(c);Check(d.Count==0,name+" "+string.Join(";",d.Select(e=>e.Code+":"+e.Message)));}
void Bad(DrawingPlanContext c,string code,string name)=>Check(DrawingDimensionTraceValidation.Validate(c).Any(d=>d.Code==code),name);
DrawingPlanContext Replace(DrawingGeometryAnchor a)=>context with{GeometryAnchors=context.GeometryAnchors.Select(x=>x.Id==a.Id?a:x).ToArray()};
Good(context,"implicit bore axis and virtual corner accepted with source-supported construction");
Save("valid-trace-context.json",context);
Bad(Replace(axis with{Construction=null}),"DIMENSION_TRACE_INK","invisible axis cannot masquerade as a drawn stroke");
Bad(Replace(axis with{SourcePath=[P(130,80),P(130,170)]}),"DIMENSION_ANCHOR_CONSTRUCTION","wrong midpoint rejected even inside the same bore");
Bad(Replace(wall1 with{SourcePath=[P(105,80),P(105,160)]}),"DIMENSION_TRACE_INK","invented support wall across blank source rejected");
Bad(Replace(wall1 with{ViewId="other"}),"DIMENSION_ANCHOR_CONSTRUCTION","support from another view rejected");
Bad(Replace(wall1 with{FeatureId="plate"}),"DIMENSION_ANCHOR_CONSTRUCTION","support from another feature rejected");
Bad(Replace(wall1 with{Construction=axis.Construction}),"DIMENSION_ANCHOR_CONSTRUCTION","recursive or circular construction rejected");
Bad(Replace(axis with{Construction=axis.Construction! with{SupportAnchorIds=["wall1","wall1"]}}),"DIMENSION_ANCHOR_CONSTRUCTION","duplicate supports rejected");
Bad(Replace(axis with{SourcePath=[P(120,80),P(120,240)]}),"DIMENSION_TRACE_ANCHOR_PATH","constructed path cannot exceed its bounds");
Bad(Replace(axis with{Bounds=new(.3,.2,.5,.9),SourcePath=[P(120,80),P(120,240)]}),"DIMENSION_ANCHOR_CONSTRUCTION","enlarging bounds cannot justify unbounded extrapolation");
Bad(Replace(corner with{SourcePath=[P(70,160),P(70,160)]}),"DIMENSION_ANCHOR_CONSTRUCTION","wrong virtual intersection rejected");
Bad(Replace(corner with{Construction=corner.Construction! with{SupportAnchorIds=["wall1","wall2"]}}),"DIMENSION_ANCHOR_CONSTRUCTION","parallel lines cannot define a virtual corner");
Bad(context with{Dimensions=[fact with{BindingEvidence=fact.BindingEvidence! with{Trace=trace with{EndExtension=trace.StartExtension}}}]},"DIMENSION_TRACE_ATTACHMENT","real ink on wrong extension still rejected");
Bad(context with{Dimensions=[fact with{BindingEvidence=fact.BindingEvidence! with{Trace=trace with{Path=[P(60,220),P(120,220)]}}}]},"DIMENSION_TRACE_INK","new mask still rejects invented dimension across blank page");

foreach(var raw in new[]{"4× C 0,8","C0.8","4xC .8","C.0315in","3×R1,5"})
{var p=HoleCalloutParser.Parse(raw);Check(p.Complete && p.Components.All(c=>raw.Substring(c.Start,c.Length)==c.Literal),"complete typed grammar retains literal spans: "+raw);}
foreach(var raw in new[]{"C0","C-0.8","C0.8×30°","C0.8°","C0.8 THRU","C0.8 ⌴⌀2","4×C0.8 extra","H80"})
    Check(!HoleCalloutParser.Parse(raw).Complete,"unsupported or conflicting literal remains blocked: "+raw);
var chamferAnnotation=new DrawingSourceAnnotation{Id="c",FeatureId="plate",OperationId="finish",ViewId="front",TextBounds=new(.1,.1,.2,.2),SourceLiteral="4× C 0,8",State=DrawingReviewState.Reviewed,Evidence="controlled source note"};
var selections=Enumerable.Range(0,4).Select(i=>new EntityQuery{Kind=EntityKind.Edge,PositionMm=new(i*10,0,0)}).ToArray();
var op=new GenericOperationDraft{Id="finish",Type=GenericOperationKind.NativeFeature,Feature=new(){Kind=NativeFeatureKind.Chamfer,DistanceMm=.8,AngleDegrees=45,TangentPropagation=false,Selections=selections}};
var ledgerContext=context with{AnnotationLedger=new(){SourceSha256=original,Annotations=[chamferAnnotation]}};
IReadOnlyList<ModelingDiagnostic> Ops(GenericOperationDraft o,DrawingSourceAnnotation? a=null)=>SourceAnnotationValidation.ValidateOperations(ledgerContext with{AnnotationLedger=ledgerContext.AnnotationLedger! with{Annotations=[a??chamferAnnotation]}},[o]);
Check(Ops(op).Count==0,"C0.8 maps to four finite 45 degree chamfer selections");
Check(Ops(op with{Feature=op.Feature! with{ChamferMode=ChamferMode.EqualDistance}}).Count==0,"C0.8 accepts native equal-distance mode");
Check(Ops(op with{Feature=op.Feature! with{AngleDegrees=30}}).Any(d=>d.Code=="SOURCE_ANNOTATION_CHAMFER_MODE"),"C shorthand cannot silently become 30 degree chamfer");
Check(Ops(op with{Feature=op.Feature! with{ChamferMode=ChamferMode.TwoDistances,SecondDistanceMm=1}}).Any(d=>d.Code=="SOURCE_ANNOTATION_CHAMFER_MODE"),"C shorthand cannot silently become unequal distances");
Check(Ops(op with{Feature=op.Feature! with{Kind=NativeFeatureKind.Fillet}}).Any(d=>d.Code=="SOURCE_ANNOTATION_CHAMFER_MODE"),"C cannot execute as a fillet");
foreach(var bad in new[]{op.Feature! with{TangentPropagation=true},op.Feature! with{Selections=selections.Take(3).ToArray()},op.Feature! with{Selections=selections.Select(s=>s with{AllMatches=true}).ToArray()},op.Feature! with{Selections=[selections[0],selections[0],selections[1],selections[2]]}})
    Check(Ops(op with{Feature=bad}).Any(d=>d.Code=="SOURCE_ANNOTATION_EXECUTED_COUNT"),"finite edge count rejects propagation, missing, broad or duplicate selections");
var fillet=op with{Feature=op.Feature! with{Kind=NativeFeatureKind.Fillet,RadiusMm=1.5,Selections=selections.Take(3).ToArray()}};
Check(Ops(fillet,chamferAnnotation with{SourceLiteral="3×R1.5"}).Count==0,"non-hole radius multiplicity uses real edge list");
var chamferEdge=LineAnchor("chamfer","plate",60,150,75,160);
var leader=new DrawingDimensionTrace{Kind=DrawingDimensionTraceKind.Leader,Path=[P(60,135),P(60,150),P(68,155)],Evidence="known source edge leader"};
// This fixture deliberately uses real existing ink and a concrete chamfer edge. Endpoint corruption must still fail.
var cf=fact with{Id="chamfer-size",SourceLiteral="C0.8",SourceAnnotationId="c",SourceComponentKey="chamfer_distance",Value=.8,OperationId="finish",ParameterPath="feature.distance_mm",BindingEvidence=fact.BindingEvidence! with{FeatureId="plate",QuantityKind=DrawingQuantityKind.Linear,TargetAnchorId="chamfer",TextBounds=new(.19,.43,.21,.46),TargetBounds=chamferEdge.Bounds,Trace=null}};
var ca=chamferAnnotation with{SourceLiteral="C0.8",LeaderTrace=leader,LeaderTargetAnchorId="chamfer"};
var cc=context with{Dimensions=[cf],GeometryAnchors=[chamferEdge],AnnotationLedger=new(){SourceSha256=original,Annotations=[ca]}};
Good(cc,"C chamfer uses actual leader and concrete edge, without invented linear extension lines");
Bad(cc with{AnnotationLedger=cc.AnnotationLedger! with{Annotations=[ca with{LeaderTrace=leader with{Path=[P(60,135),P(60,142)]}}]}},"DIMENSION_TRACE_ATTACHMENT","chamfer leader stopping on adjacent real ink is rejected");
var damaged=(byte[])mask.Clone();damaged[0]^=1;File.WriteAllBytes(region.InkMaskPath!,damaged);
Bad(context,"DIMENSION_TRACE_SOURCE","mask hash tampering still invalidates source evidence");File.WriteAllBytes(region.InkMaskPath!,mask);
if(args.Length>1)
{
    var realSource=Path.GetFullPath(args[1]);var before=DrawingPlanValidation.FileHash(realSource);
    var real=await new EngineeringDrawingIngestionService().IngestAsync(new DrawingIngestionRequest{InputPath=realSource,ArtifactRoot=Path.Combine(output,"real-ingestion")});Save("real-ingestion.json",real);
    var ri=DrawingOmissionValidation.Load(real.OmissionInventoryPath!,real.OmissionInventorySha256);var rr=ri.Regions.Single(r=>r.Purpose=="overview");var rm=File.ReadAllBytes(rr.InkMaskPath!);
    Check(Enumerable.Range(530,18).All(y=>{var n=y*rr.PixelWidth+649;return(rm[n/8]&(1<<(n%8)))!=0;}),"original blind sample gray191 extension restored at all 18 recorded pixels");
    Check(before==DrawingPlanValidation.FileHash(realSource),"original blind drawing hash unchanged");
}
Save("summary.json",new{passed=checks.Count,checks,native_execution=false});
