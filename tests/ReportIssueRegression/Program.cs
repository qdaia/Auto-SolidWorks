using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using CadModeling.Core;
using CadModeling.Ir;

var payload = new { trace = new string('x', 2_000_000), count = 25 };
if(args.FirstOrDefault()=="--writer")
{
    var store=new CompactArtifactStore(args[1],"concurrency-fixture");
    for(var i=0;i<8;i++)store.SaveResult(payload);
    return;
}
var output=Path.GetFullPath(args.FirstOrDefault()??Path.Combine(Path.GetTempPath(),"ReportIssueRegression_"+Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(output);
var checks=new List<object>();
void Check(bool value,string name)
{
    if(!value)throw new Exception("FAIL: "+name);
    checks.Add(new{name,passed=true}); Console.WriteLine("PASS: "+name);
}
var box=new DrawingReviewBox(.1,.1,.4,.4);
var selected=new SourceInterpretationHypothesis{Id="flat-cap",Description="solid flat cap",Observations=[
    new(){ViewId="front",Bounds=box,Observation="top edge with no bore hidden lines"},
    new(){ViewId="iso",Bounds=box,Observation="solid circular top surface"}]};
var rejected=new SourceInterpretationHypothesis{Id="bore",Description="central bore",Observations=[new(){ViewId="iso",Bounds=box,Observation="solid top contradicts extra bore",Contradicts=true}]};
var review=new SourceInterpretationReview{FeatureId="cap",SelectedHypothesisId="flat-cap",DimensionDatumDerivation="diameter20 is top outline; 6x15 is independent six-hole annotation",Hypotheses=[selected,rejected]};
var context=new DrawingPlanContext{SourcePath=Path.Combine(output,"source.png"),RequireInterpretationReview=true,
    Views=[new(){Id="front"},new(){Id="iso",Kind=ModelDrawingView.Isometric}],Features=[new(){Id="cap",SourceLiteral="solid top",ViewIds=["front","iso"]}],InterpretationReviews=[review]};
bool Has(DrawingPlanContext c,string code)=>SourceInterpretationValidation.Validate(c).Any(d=>d.Code==code&&d.Severity==DiagnosticSeverity.Error);
Check(!SourceInterpretationValidation.Validate(context).Any(d=>d.Severity==DiagnosticSeverity.Error),"E01 supported flat top with rejected seventh-hole alternative");
Check(Has(context with{InterpretationReviews=[review with{SelectedHypothesisId="bore"}]},"SOURCE_INTERPRETATION_CONFLICT"),"E01 selected seventh-hole hypothesis blocked by source counterevidence");
Check(Has(context with{InterpretationReviews=[]},"SOURCE_INTERPRETATION_REQUIRED"),"strict source review cannot omit critical feature");
Check(Has(context with{InterpretationReviews=[review with{Hypotheses=[selected,rejected with{Observations=[rejected.Observations[0] with{Contradicts=false}]}]}]},"SOURCE_INTERPRETATION_AMBIGUOUS"),"unrefuted alternative blocks ambiguous source interpretation");
Check(Has(context with{InterpretationReviews=[review with{Hypotheses=[selected with{Observations=[selected.Observations[0]]},rejected]}]},"SOURCE_INTERPRETATION_INCOMPLETE"),"missing isometric support cannot be replaced by numeric checks");
Check(Has(context with{InterpretationReviews=[review with{Hypotheses=[selected with{Observations=[selected.Observations[0] with{Bounds=new(double.NaN,0,1,1)},selected.Observations[1]]},rejected]}]},"SOURCE_INTERPRETATION_INVALID"),"nonfinite source localization rejected");
Check(Has(context with{InterpretationReviews=[review,review]},"SOURCE_INTERPRETATION_INVALID"),"duplicate feature reviews rejected");
Check(Has(context with{InterpretationReviews=[review with{Hypotheses=[selected,selected]}]},"SOURCE_INTERPRETATION_INVALID"),"duplicate hypothesis ids rejected");
Check(Has(context with{RequireInterpretationReview=false,InterpretationReviews=[review with{SelectedHypothesisId="bore"}]},"SOURCE_INTERPRETATION_CONFLICT"),"declared contradiction still blocks legacy contract");
var band=selected with{Id="round-tube",Description="entire circular section",Observations=[selected.Observations[0],selected.Observations[1] with{Observation="continuous flat band and sidewalls contradict circular tube",Contradicts=true}]};
Check(Has(context with{InterpretationReviews=[review with{SelectedHypothesisId=band.Id,Hypotheses=[band,rejected]}]},"SOURCE_INTERPRETATION_CONFLICT"),"E02 full round section cannot dismiss flat band counterevidence");
var nominal=new DrawingDimensionFact{Id="pitch",OperationId="holes",ParameterPath="feature.hole_centers.0.ymm",SourceLiteral="R32.5",Value=32.45,NominalValue=32.5,DeviationReason="tangency",DeviationAuthorization="explicit user instruction recorded",Status=DrawingFactStatus.Derived,Derivation="32.5 - .05 = 32.45"};
Check(!Has(context with{Dimensions=[nominal]},"SOURCE_DEVIATION_INVALID"),"E09/E10 authorized deviation retains nominal and executed values");
Check(SourceInterpretationValidation.SourceLiteralValue(nominal)==32.5&&nominal.Value==32.45,"E11 source annotation compares nominal while operation binds executed value");
Check(SourceInterpretationValidation.SourceLiteralValue(nominal with{DeviationAuthorization=null})==32.45,"unrecorded authorization cannot substitute nominal source quantity");
Check(SourceInterpretationValidation.SourceLiteralValue(nominal with{NominalValue=null})==32.45,"erased nominal cannot silently validate original source quantity");
Check(Has(context with{Dimensions=[nominal with{DeviationAuthorization=null}]},"SOURCE_DEVIATION_INVALID"),"deviation without recorded authorization rejected");
Check(Has(context with{Dimensions=[nominal with{NominalValue=null}]},"SOURCE_DEVIATION_INVALID"),"deviation cannot erase nominal value");
Check(Has(context with{Dimensions=[nominal with{NominalValue=double.NaN}]},"SOURCE_DEVIATION_INVALID"),"nonfinite nominal value rejected");
Check(Has(context with{Dimensions=[nominal with{Status=DrawingFactStatus.Stated}]},"SOURCE_DEVIATION_INVALID"),"executed deviation cannot claim original stated value");

var sketch=new GenericOperationDraft{Id="sketch",Type=GenericOperationKind.ProfileSketch,Frame=new(),AutoDimensionPrimitives=true,
    Primitives=[new(){Type=GenericPrimitiveKind.Circle,DiameterMm=80,CenterXmm=0,CenterYmm=0}]};
var boss=new GenericOperationDraft{Id="column",Type=GenericOperationKind.ExtrudeBoss,SketchId="sketch",DepthMm=100,EndCondition=ExtrudeEndCondition.Blind};
var holes=new GenericOperationDraft{Id="holes",Type=GenericOperationKind.NativeFeature,Feature=new(){Kind=NativeFeatureKind.Hole,Frame=new(){OriginMm=new(0,0,200)},DiameterMm=15,DepthMm=160,ThroughAll=false,Reverse=true,HoleCenters=[new(0,32.5)]}};
var draft=new GenericModelDraft{SourceText="nominal cylinder and tangent hole",Operations=[sketch,boss,holes]};
var slot = new GenericOperationDraft { Id="slot", Type=GenericOperationKind.ExtrudeCut, SketchId="sketch", DepthMm=4, ReverseDirection=true };
var traces=ModelingPreflight.Validate(draft with {Operations=[sketch,boss,slot]});
Check(traces.Any(d=>d.Code=="EXTRUDE_DIRECTION_TRACE"&&d.Path=="operations.slot"&&d.Message.Contains("计划方向 (-0,-0,-1)")&&d.Message.Contains("api_flip=False")),"cut trace maps typed reverse to executor flip without changing draft");
Check(ModelingPreflight.Validate(draft with {Operations=[sketch,slot with {ReverseDirection=false}]}).Any(d=>d.Code=="EXTRUDE_DIRECTION_TRACE"&&d.Message.Contains("api_flip=True")&&d.Message.Contains("计划方向 (0,0,1)")),"opposite cut direction has opposite trace");
Check(!ModelingPreflight.Validate(draft with {Operations=[sketch with {PlaneId="attached-plane"},slot]}).Any(d=>d.Code=="EXTRUDE_DIRECTION_TRACE"),"unresolved reference-plane direction is never inferred from fallback frame");
var passCache = new InspectionPassCache<double[]>(); var queries=0;
double[] Query(double value) { queries++; return [value,0,0,0,0]; }
var point = new Vector3(1,2,3);
var first=passCache.GetOrQuery(0,point,()=>Query(10));
Check(ReferenceEquals(first,passCache.GetOrQuery(0,point,()=>Query(99)))&&queries==1,"identical face/point reuses one native closest-point query");
Check(passCache.GetOrQuery(1,point,()=>Query(20))[0]==20&&queries==2,"different face cannot reuse another trimmed boundary");
Check(passCache.GetOrQuery(0,new(1,2,3.000001),()=>Query(30))[0]==30&&queries==3,"nearby points are never rounded into one query");
Check(new InspectionPassCache<double[]>().GetOrQuery(0,point,()=>Query(40))[0]==40&&queries==4,"new pass including reopen must measure again");
var failedQueries=0;
for(var attempt=0;attempt<2;attempt++)
    try { passCache.GetOrQuery(2,point,()=>{failedQueries++;throw new InvalidOperationException("boundary failure");}); throw new Exception("swallowed failure"); }
    catch(InvalidOperationException ex) { Check(ex.Message=="boundary failure","failed query remains unverifiable on attempt "+attempt); }
Check(failedQueries==1,"failed exact query is not retried or converted to success inside a pass");
Check(new GenericPlanCompiler().Compile(draft).Success,"nominal preflight baseline is a valid typed draft");
Check(ModelingPreflight.Validate(draft).Any(d=>d.Code=="HOLE_ZERO_THICKNESS_RISK"&&d.Severity==DiagnosticSeverity.Warning),"E09 40-32.5-7.5 tangency detected as risk, not unique kernel proof");
Check(!ModelingPreflight.Validate(draft with{Operations=[sketch,boss,holes with{Feature=holes.Feature! with{HoleCenters=[new(0,32.45)]}}]}).Any(d=>d.Code=="HOLE_ZERO_THICKNESS_RISK"),"authorized .05 wall does not produce exact tangency warning");
Check(holes.Feature!.HoleCenters[0].Ymm==32.5,"preflight does not modify nominal pitch radius");
Check(ModelingPreflight.Validate(draft).Any(d=>d.Code=="HOLE_DIRECTION_TRACE"&&d.Message.Contains("reverse=True")),"E04 cut direction trace records explicit frame and reversal");
var endPositive=ModelingPreflight.Rotate(new(1,0,0),new(0,0,0),new(0,0,1),90);
var endNegative=ModelingPreflight.Rotate(new(1,0,0),new(0,0,0),new(0,0,1),90,true);
Check(ModelVerification.Norm(ModelVerification.Sub(endPositive,new(0,1,0)))<1e-12,"E05 right hand 90degree endpoint");
Check(ModelVerification.Norm(ModelVerification.Sub(endNegative,new(0,-1,0)))<1e-12,"E05 reverse 90degree endpoint points opposite way");
Check(ModelVerification.Norm(ModelVerification.Sub(ModelingPreflight.Rotate(new(11,20,0),new(10,20,0),new(10,20,1),90),new(10,21,0)))<1e-12,"rotation datum translation preserved");
var duplicate=sketch with{Dimensions=[new(){Name="D1",Kind=SketchDimensionKind.Diameter,Value=80,Entities=[new(){Part=SketchEntityPart.Segment}]}]};
Check(!new GenericPlanCompiler().Compile(draft with{Operations=[duplicate,boss,holes]}).Success,"E08 duplicate automatic/manual circle diameter blocks compile");
Check(ModelingPreflight.Validate(draft with{Operations=[duplicate with{Dimensions=[duplicate.Dimensions[0] with{Kind=SketchDimensionKind.Radius}]},boss]}).Any(d=>d.Code=="SKETCH_DUPLICATE_DRIVING_DIMENSION"),"manual radius duplicates automatic diameter");
Check(!ModelingPreflight.Validate(draft).Any(d=>d.Code=="SKETCH_DUPLICATE_DRIVING_DIMENSION"),"ordinary automatic circle dimensions remain accepted");
Check(NativeFailureDiagnostics.SketchStatus(4).Code=="SKETCH_OVER_CONSTRAINED","native state4 is overconstrained");
Check(NativeFailureDiagnostics.SketchStatus(2).Code=="SKETCH_NOT_FULLY_DEFINED","other unaccepted states preserve raw diagnostic without guessing");
var executor=Assembly.Load("CadModeling.Executor.SolidWorks").GetType("SolidWorksComExecutor",true)!;
var wrap=executor.GetMethod("WrapOperationFailure",BindingFlags.NonPublic|BindingFlags.Static)!;
var selectionFailure=executor.GetMethod("EntitySelectionFailure",BindingFlags.NonPublic|BindingFlags.Static)!;
var selectionError=selectionFailure.Invoke(null,[new EntityQuery{Kind=EntityKind.Edge,Geometry=GeometryKind.Line,Direction=new(0,0,1)},Enumerable.Range(0,30).Select(_=>new object()).ToArray(),0,"FeatureGeneratedFaces"])!;
var selectionData=(IReadOnlyDictionary<string,string>)selectionError.GetType().GetProperty("Diagnostics")!.GetValue(selectionError)!;
Check(selectionData["query_json"].Contains("direction")&&selectionData["selection_scope"]=="FeatureGeneratedFaces","E03 reference rejection exposes exact filter and original scope");
Check(selectionData["candidate_filter_results_json"].Contains("rejected_filters")&&selectionData["candidate_filter_results_truncated"]=="True","E03 bounded per-candidate filter rejection reports truncation");
foreach(var code in new[]{"SKETCH_OVER_CONSTRAINED","SKETCH_NOT_FULLY_DEFINED","CUT_EXTRUDE_NULL"})
{
    var e=(Exception)wrap.Invoke(null,[new ExtrudeCutOperation{Id="cut",Name="cut",SketchId="sketch"},new InvalidOperationException(code+": fixture")])!;
    Check((string)e.GetType().GetProperty("Code")!.GetValue(e)! == code,"executor preserves diagnostic code "+code);
}

var cylinderCheck=new CylinderGroupCheck{Id="six",SourceLiteral="six diameter15 axes with top clipped by cone",DiameterMm=15,LengthMm=100,AxisStartsMm=[new(0,32.45,40)]};
var measured=new MeasuredCylinder(new(0,32.45,40),new(0,32.45,140),new(0,0,1),7.5,2*Math.PI*7.5*100*.7,true,"hole");
ModelVerificationResult Evaluate(CylinderGroupCheck c,IReadOnlyList<MeasuredCylinder>? m=null)=>ModelVerification.Evaluate(new(){CylinderGroups=[c]},m??[measured],new Dictionary<string,double>(),null);
Check(!Evaluate(cylinderCheck).Passed&&Evaluate(cylinderCheck).Checks[0].Status=="unverifiable","E10 legacy complete-wall gate still rejects clipped wall");
var trimmed=cylinderCheck with{Coverage=CylinderCoverage.TrimmedAxisAndDiameter,LengthMm=0};
Check(Evaluate(trimmed).Passed,"E10 explicit trimmed mode accepts analytic axis diameter count");
Check(Evaluate(trimmed).Checks[0].Measurements!["scope"]=="trimmed_cylinder_axes_diameters_count"&&!Evaluate(trimmed).WholeModelInventoryChecked,"trimmed acceptance never claims full wall or whole inventory");
Check(!Evaluate(trimmed,[measured with{RadiusMm=8}]).Passed,"trimmed wrong diameter rejected");
Check(!Evaluate(trimmed,[measured with{AxisStartMm=new(1,32.45,40),AxisEndMm=new(1,32.45,140)}]).Passed,"trimmed wrong axis rejected");
Check(!Evaluate(trimmed,[measured,measured with{AxisStartMm=new(10,32.45,40),AxisEndMm=new(10,32.45,140)}]).Passed,"trimmed extra matching cylinder rejected");
Check(!Evaluate(trimmed,[measured with{Interior=false}]).Passed,"trimmed exterior wall cannot replace bore");
Check(ModelVerification.Validate(new(){CylinderGroups=[cylinderCheck with{LengthMm=0}]},null).Any(),"E12 zero length remains invalid for complete wall");
Check(ModelVerification.Validate(new(){CylinderGroups=[trimmed with{LengthMm=100}]},null).Any(),"trimmed mode cannot silently claim axial length");
Check(!ModelVerification.Validate(new(){CylinderGroups=[trimmed]},null).Any(),"trimmed explicit contract is valid");
var inventorySpec=new ModelVerificationSpec{RequireWholeModelInventory=true,SurfaceSamples=[new(){Id="finite",SourceLiteral="finite point",SurfaceKind=LocalSurfaceKind.Plane,PointsMm=[new(0,0,0)],OutwardNormals=[new(0,0,1)]}]};
Check(ModelVerification.Validate(inventorySpec,null).Any(),"E12 finite points cannot satisfy required whole inventory");
var inventory=new WholeModelCheck{Id="stock",SourceLiteral="independent topology expectation",SolidBodyCount=1,FaceCount=4,EdgeCount=8,SurfaceFaceCounts=new Dictionary<LocalSurfaceKind,int>{{LocalSurfaceKind.Cylinder,4}}};
Check(!ModelVerification.Validate(new(){WholeModelChecks=[inventory]},null).Any(),"complete independent topology inventory contract accepted");
var stockMeasurement=new WholeModelMeasurement(true,1,0,4,8,0,new Dictionary<LocalSurfaceKind,int>{{LocalSurfaceKind.Cylinder,4}},100,100);
Check(!ModelVerification.Evaluate(new(){WholeModelChecks=[inventory with{EdgeCount=16}]},[],new Dictionary<string,double>(),null,wholeModel:stockMeasurement).Passed,"E14 duplicated periodic seam count remains rejected");
Check(ModelVerification.Evaluate(new(){WholeModelChecks=[inventory]},[],new Dictionary<string,double>(),null,wholeModel:stockMeasurement).WholeModelInventoryChecked,"correct independent inventory accepts unique edge count");
var dimCheck=new NativeDimensionCheck{Id="diam",SourceLiteral="80",DimensionName="D1@sketch",Value=80};
var missing=ModelVerification.Evaluate(new(){NativeDimensions=[dimCheck]},[],new Dictionary<string,double>{{"直径@sketch",.08}},null);
Check(!missing.Passed&&missing.Checks[0].Measurements!["available_active_names"].Contains("直径@sketch"),"E07 missing guessed dimension returns actual names and fails closed");
var suggestionOnly=ModelVerification.Evaluate(new(){NativeDimensions=[dimCheck]},[],new Dictionary<string,double>(),null,availableNativeDimensionNames:[dimCheck.DimensionName]);
Check(!suggestionOnly.Passed,"dimension name suggestion cannot be used as measured acceptance value");
Check(NativeFailureDiagnostics.RequirementCode(missing)=="SOURCE_REQUIREMENT_UNVERIFIABLE","unavailable target has separate requirement error code");
var mismatch=missing with{Checks=[new("diam",false,"mismatch","wrong value")]};
Check(NativeFailureDiagnostics.RequirementCode(mismatch)=="SOURCE_REQUIREMENT_MISMATCH","measured wrong dimension remains mismatch");
Check(NativeFailureDiagnostics.RequirementCode(missing,true)=="SAVED_REQUIREMENT_UNVERIFIABLE","saved unavailable target is also distinct");
Check(ModelVerification.Validate(new(){NativeDimensions=[dimCheck with{Unit=DrawingValueUnit.Unitless,Value=6,Tolerance=0}]},null).Any(),"E13 zero count tolerance rejected with specific advice");
Check(!ModelVerification.Validate(new(){NativeDimensions=[dimCheck with{Unit=DrawingValueUnit.Unitless,Value=6,Tolerance=.01}]},null).Any(),"E13 six instances with .01 tolerance accepted");
Check(ModelVerification.Validate(new(){NativeDimensions=[dimCheck with{Unit=DrawingValueUnit.Unitless,Value=6,Tolerance=1}]},null).Any(),"count tolerance cannot accept five as six");
try{DrawingLayoutPlanner.Create(new(){ScheduleRowHeightMm=5});throw new Exception("accepted5");}
catch(ArgumentException e){Check(e.Message.Contains("DRAWING_ROW_HEIGHT_RANGE")&&e.Message.Contains("7..20"),"E15 row height rejection exposes exact field and accepted range");}
Check(DrawingLayoutPlanner.Create(new(){ScheduleRowHeightMm=8}).ScheduleRowHeight==.008,"valid8mm drawing layout accepted");
var roundtrip=JsonSerializer.Deserialize<DrawingPlanContext>(JsonSerializer.Serialize(context,ModelingIrJson.Options),ModelingIrJson.Options)!;
Check(roundtrip.RequireInterpretationReview&&roundtrip.InterpretationReviews[0].Hypotheses[1].Observations[0].Contradicts,"source interpretation roundtrip retains counterevidence");
var storeRoot=Path.Combine(output,"concurrent-receipts");
var processes=new List<Process>();
var reading=true;
var invalidReads=new System.Collections.Concurrent.ConcurrentQueue<string>();
var reader=Task.Run(async ()=>
{
    while(Volatile.Read(ref reading))
    {
        if(Directory.Exists(storeRoot))foreach(var path in Directory.GetFiles(storeRoot,"result_*.json"))
        {
            try
            {
                var bytes=File.ReadAllBytes(path);
                var expected=Path.GetFileNameWithoutExtension(path)[7..];
                if(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant()!=expected)invalidReads.Enqueue("partial hash");
                using var parsed=JsonDocument.Parse(bytes);
            }
            catch(Exception e){invalidReads.Enqueue(e.GetType().Name);}
        }
        await Task.Delay(1);
    }
});
for(var i=0;i<6;i++)
{
    var start=new ProcessStartInfo("dotnet"){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,RedirectStandardOutput=true,RedirectStandardError=true};
    start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);start.ArgumentList.Add("--writer");start.ArgumentList.Add(storeRoot);
    processes.Add(Process.Start(start)!);
}
foreach(var process in processes)
{
    await process.WaitForExitAsync();
    Check(process.ExitCode==0,"E16 cross-process large identical receipt writer exits cleanly: "+process.Id);
    process.Dispose();
}
Volatile.Write(ref reading,false);await reader;
Check(invalidReads.IsEmpty,"concurrent readers never observe partially written final receipt");
var receipts=Directory.GetFiles(storeRoot,"result_*.json");
Check(receipts.Length==1,"concurrent identical writers publish one immutable content address");
Check(Directory.GetFiles(storeRoot,"*.pending").Length==0,"successful atomic receipt publication leaves no pending files");
var storeReader=new CompactArtifactStore(storeRoot,"concurrency-fixture");
var receipt=storeReader.SaveResult(payload);
var page=JsonSerializer.SerializeToElement(storeReader.Read(receipt.ArtifactId,"/trace",0,50));
Check(page.GetProperty("total").GetInt32()==2_000_000&&page.GetProperty("next_offset").GetInt32()==24000,"large receipt string pagination remains complete and bounded");
var text=File.ReadAllText(receipt.Path);File.WriteAllText(receipt.Path,text+" ");
try{storeReader.Read(receipt.ArtifactId);throw new Exception("accepted corrupt receipt");}
catch(InvalidOperationException){Check(true,"altered receipt hash is rejected after atomic publication");}
File.WriteAllText(Path.Combine(output,"regression-summary.json"),JsonSerializer.Serialize(new{passed=checks.Count,checks,native_execution=false,scope="source_contract_and_managed_executor_diagnostics_and_cross_process_receipts"},new JsonSerializerOptions{WriteIndented=true}));
Console.WriteLine($"ReportIssueRegression: {checks.Count} passed");
