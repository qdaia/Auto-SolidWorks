using System.Text.Json;
using System.Text.Json.Nodes;
using CadModeling.Core;
using CadModeling.Ir;

var outDir = Path.GetFullPath(args.Length>0 ? args[0] : Path.Combine(Path.GetTempPath(),"dimension-expressions-"+Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(outDir);
var results = new List<string>();
void Check(bool ok,string name) { if(!ok) throw new Exception(name); results.Add(name); Console.WriteLine("PASS "+name); }
void Reject(Action action,string code,string name)
{
    try { action(); } catch(Exception e) when(e is InvalidDataException or JsonException or ArgumentException)
    { Check(e.Message.Contains(code,StringComparison.Ordinal),name+" ["+code+"]"); return; }
    throw new Exception("Expected rejection: "+name);
}
DimensionExpression Ref(string id)=>new(){Kind=DimensionExpressionKind.Reference,DimensionId=id};
DimensionExpression Const(double value,DrawingValueUnit unit=DrawingValueUnit.Unitless)=>new(){Kind=DimensionExpressionKind.Constant,Value=value,Unit=unit};
DimensionExpression Op(DimensionExpressionKind kind,params DimensionExpression[] values)=>new(){Kind=kind,Arguments=values};
DrawingDimensionFact Fact(string id,double value,DrawingValueUnit unit=DrawingValueUnit.Millimeter)=>new()
{Id=id,Value=value,Unit=unit,SourceLiteral=value.ToString(System.Globalization.CultureInfo.InvariantCulture),OperationId="sk",ParameterPath="primitives.0.width_mm"};
DrawingDimensionFact Derived(string id,DimensionExpression expr,DrawingValueUnit unit=DrawingValueUnit.Millimeter)=>
    Fact(id,0,unit) with {Status=DrawingFactStatus.Derived,Expression=expr,Derivation="Explicit source dimension relation"};
var source=Path.Combine(outDir,"source.txt"); File.WriteAllText(source,"independent dimensional source");
DrawingPlanContext Context(params DrawingDimensionFact[] facts)=>new(){SourcePath=source,Dimensions=facts,RequireCompleteBindings=false};
double Value(DrawingPlanContext context,string id)=>DimensionExpressions.Resolve(context).Dimensions.Single(d=>d.Id==id).Value;
var d=Fact("diameter",20);
var r=Derived("radius",Op(DimensionExpressionKind.Divide,Ref("diameter"),Const(2))) with {ParameterPath="primitives.0.height_mm"};
Check(Value(Context(d,r),"radius")==10,"diameter / 2 evaluates radius");
Check(Value(Context(r,d),"radius")==10,"evaluation is independent of input order");
var resolved=DimensionExpressions.Resolve(Context(d,r));
Check(resolved.Dimensions[1].DerivedFromDimensionIds.SequenceEqual(new[]{"diameter"}),"dependencies generated from actual expression");
Check(DimensionExpressions.Validate(resolved).Count==0,"resolved immutable facts validate");
Check(DimensionExpressions.Validate(Context(d,r)).Any(e=>e.Code=="DIMENSION_EXPRESSION_STALE"),"stale IR calculation rejected");
Check(Value(Context(d with{Value=24},r),"radius")==12,"premise edit recomputes derived value");
var offset=Derived("offset",Op(DimensionExpressionKind.Add,Ref("radius"),Const(3,DrawingValueUnit.Millimeter)));
Check(Value(Context(offset,r,d),"offset")==13,"transitive dependencies evaluated");
Check(Value(Context(Fact("inch",1,DrawingValueUnit.Inch),Derived("mm",Ref("inch"))),"mm")==25.4,"explicit length conversion");
Check(Value(Context(d,Derived("ratio",Op(DimensionExpressionKind.Divide,Ref("diameter"),Const(2,DrawingValueUnit.Millimeter)),DrawingValueUnit.Unitless)),"ratio")==10,"same-family division becomes unitless");
Check(Value(Context(d,Derived("negative",Op(DimensionExpressionKind.Negate,Ref("diameter")))),"negative")==-20,"negative coordinate expression");
Check(Value(Context(d,Derived("twice",Op(DimensionExpressionKind.Multiply,Const(2),Ref("diameter")))),"twice")==40,"unitless multiplication");
Reject(()=>Value(Context(d,r with{Status=DrawingFactStatus.Stated}),"radius"),"STATUS","stated source cannot be formula-owned");
Reject(()=>Value(Context(d,r with{DerivedFromDimensionIds=["diameter","other"]}),"radius"),"DEPENDENCIES","false premise list rejected");
Reject(()=>Value(Context(d,r with{DerivedFromDimensionIds=["diameter","diameter"]}),"radius"),"DEPENDENCIES","duplicate premise list rejected");
Reject(()=>Value(Context(r),"radius"),"MISSING","missing source dimension rejected");
Reject(()=>Value(Context(d with{Status=DrawingFactStatus.Unknown},r),"radius"),"PREMISE","unknown source rejected");
Reject(()=>Value(Context(d with{Status=DrawingFactStatus.Assumed},r),"radius"),"PREMISE","assumed source rejected");
Reject(()=>Value(Context(d with{NominalValue=22},r),"radius"),"PREMISE","execution deviation is not silently used as nominal premise");
Reject(()=>Value(Context(d,r with{Expression=Ref("radius")}),"radius"),"CYCLE","self cycle rejected");
Reject(()=>Value(Context(Derived("a",Ref("b")),Derived("b",Ref("a"))),"a"),"CYCLE","mutual cycle rejected");
Reject(()=>Value(Context(d,r with{Unit=DrawingValueUnit.Degree}),"radius"),"UNIT","output family mismatch rejected");
Reject(()=>Value(Context(d,Derived("x",Op(DimensionExpressionKind.Add,Ref("diameter"),Const(1)))),"x"),"UNIT","length plus scalar rejected");
Reject(()=>Value(Context(d,Derived("x",Op(DimensionExpressionKind.Multiply,Ref("diameter"),Ref("diameter")))),"x"),"UNIT","unsupported area result rejected");
Reject(()=>Value(Context(d,Derived("x",Op(DimensionExpressionKind.Divide,Ref("diameter"),Const(0)))),"x"),"ZERO","zero division rejected");
Reject(()=>Value(Context(d,Derived("x",Op(DimensionExpressionKind.Multiply,Ref("diameter"),Const(double.MaxValue)))),"x"),"NONFINITE","arithmetic overflow rejected");
Reject(()=>Value(Context(d,Derived("x",Ref("diameter") with{Value=1})),"x"),"SHAPE","ignored expression fields rejected");
Reject(()=>Value(Context(d,Derived("x",Op(DimensionExpressionKind.Add,Ref("diameter")))),"x"),"SHAPE","operator arity checked");
var deep=Ref("diameter"); for(var i=0;i<34;i++) deep=Op(DimensionExpressionKind.Negate,deep);
Reject(()=>Value(Context(d,Derived("x",deep)),"x"),"LIMIT","expression depth bounded");
var equation=new DimensionEquation{Id="closure",Left=Ref("diameter"),Right=Op(DimensionExpressionKind.Multiply,Ref("radius"),Const(2)),SourceExplanation="diameter is twice radius"};
Check(DimensionExpressions.Resolve(Context(d,r) with{DimensionEquations=[equation]}).Dimensions.Count==2,"consistent independent dimension chain accepted");
Reject(()=>DimensionExpressions.Resolve(Context(d,r) with{DimensionEquations=[equation with{Right=Ref("radius")}]}),"CONFLICT","contradictory dimension chain rejected");
Reject(()=>DimensionExpressions.Resolve(Context(d,r) with{DimensionEquations=[equation,equation]}),"EQUATION_ID","equation identities unique");

var node=JsonNode.Parse("""
{"source_text":"test","operations":[{"type":"ProfileSketch","id":"sk","plane":"Front","primitives":[{"type":"CenteredRectangle","width_mm":{"$dimension":"diameter","unit":"Millimeter"},"height_mm":{"$dimension":"radius","unit":"Millimeter"}}]}]}
""")!;
node["drawing_context"]=JsonSerializer.SerializeToNode(Context(d,r),ModelingIrJson.Options);
var original=node.ToJsonString();
var draft=CompactModelInputs.Decode<GenericModelDraft>(node);
Check(draft.Operations[0].Primitives[0].WidthMm==20 && draft.Operations[0].Primitives[0].HeightMm==10,"direct MCP decoder resolves shared references");
Check(node.ToJsonString()==original,"normalization preserves immutable submitted source");
var noValue=node.DeepClone();noValue["drawing_context"]!["dimensions"]![1]!.AsObject().Remove("value");
Check(CompactModelInputs.Decode<GenericModelDraft>(noValue).DrawingContext!.Dimensions[1].Value==10,"formula value can be omitted");
var bad=node.DeepClone(); bad["operations"]![0]!["primitives"]![0]!["width_mm"]!["unit"]="Inch";
Reject(()=>CompactModelInputs.Decode<GenericModelDraft>(bad),"TARGET_UNIT","mm field cannot receive inch numeric value");
bad=node.DeepClone(); bad["operations"]![0]!["primitives"]![0]!["width_mm"]!["extra"]=true;
Reject(()=>CompactModelInputs.Decode<GenericModelDraft>(bad),"SHAPE","reference extra fields rejected");
bad=node.DeepClone(); bad["drawing_context"]!["dimensions"]![0]!["unexpected"]=true;
Reject(()=>CompactModelInputs.Decode<GenericModelDraft>(bad),"unexpected","unknown context fields remain rejected");
bad=node.DeepClone(); bad["operations"]![0]!["primitives"]![0]!["width_mm"]!["$dimension"]="missing";
Reject(()=>CompactModelInputs.Decode<GenericModelDraft>(bad),"REFERENCE_ID","missing reference rejected");
var store=new CompactArtifactStore(Path.Combine(outDir,"store"),"expression-regression");
JsonNode Result(object value)=>JsonSerializer.SerializeToNode(value,ModelingIrJson.Options)!;
var saved=Result(CompactModelInputs.Store(store,"draft",JsonSerializer.SerializeToElement(node),null,null));
var id=saved["input_id"]!.GetValue<string>();
var patch=JsonSerializer.SerializeToElement(new[]{new{op="replace",path="/drawing_context/dimensions/0/value",value=30}});
var revised=Result(CompactModelInputs.Store(store,"draft",null,id,patch));
var modified=CompactModelInputs.Decode<GenericModelDraft>(store.LoadInput(revised["input_id"]!.GetValue<string>(),"draft"));
Check(modified.Operations[0].Primitives[0].WidthMm==30 && modified.Operations[0].Primitives[0].HeightMm==15,"immutable JSON patch recomputes formulas and all consumers");
Check(CompactModelInputs.Decode<GenericModelDraft>(store.LoadInput(id,"draft")).Operations[0].Primitives[0].HeightMm==10,"previous revision remains unchanged");
Check(store.LoadInput(id,"draft")["operations"]![0]!["primitives"]![0]!["width_mm"] is JsonObject,"stored input retains source references");
Check(!DrawingPlanValidation.Validate(modified).All(x=>x.Severity!=DiagnosticSeverity.Error),"new reference path does not bypass source/geometry validation");
AnchorChecks.Run(outDir,Check);
var arrayInput=JsonNode.Parse("""{"samples_mm":[{"$dimension":"diameter","unit":"Millimeter"}]}""")!;
arrayInput["drawing_context"]=JsonSerializer.SerializeToNode(Context(d),ModelingIrJson.Options);
Check(DimensionInputResolver.Normalize(arrayInput,false)["samples_mm"]![0]!.GetValue<double>()==20,"numeric array references resolve without mutating input");
arrayInput["samples_mm"]![0]!["unit"]="Inch";
Reject(()=>DimensionInputResolver.Normalize(arrayInput,false),"TARGET_UNIT","array reference retains parent unit check");
bad=node.DeepClone();bad["operations"]![0]!["primitives"]![0]!["center_xmm"]=JsonNode.Parse("""{"$dimension":"diameter","unit":"Inch"}""");
Reject(()=>CompactModelInputs.Decode<GenericModelDraft>(bad),"TARGET_UNIT","center coordinate unit cannot evade length check");
File.WriteAllText(Path.Combine(outDir,"summary.json"),JsonSerializer.Serialize(new{checks=results.Count,passed=true,results,native_execution=false},new JsonSerializerOptions{WriteIndented=true}));
