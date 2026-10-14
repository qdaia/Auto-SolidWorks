using CadModeling.Core;
using CadModeling.Ir;
void Check(bool condition,string name) { if(!condition)throw new Exception("FAIL: "+name);Console.WriteLine("PASS: "+name); }
var draft=new GenericModelDraft{SourceText="plate with through hole",Operations=[
    new(){Id="base",Type=GenericOperationKind.ProfileSketch,Plane=ReferencePlane.Front,Primitives=[new(){Type=GenericPrimitiveKind.CenteredRectangle,CenterXmm=0,CenterYmm=0,WidthMm=80,HeightMm=60}]},
    new(){Id="boss",Type=GenericOperationKind.ExtrudeBoss,SketchId="base",DepthMm=12,EndCondition=ExtrudeEndCondition.Blind},
    new(){Id="hole",Type=GenericOperationKind.ProfileSketch,Plane=ReferencePlane.Front,Primitives=[new(){Type=GenericPrimitiveKind.Circle,CenterXmm=0,CenterYmm=0,DiameterMm=9}]},
    new(){Id="cut",Type=GenericOperationKind.ExtrudeCut,SketchId="hole",EndCondition=ExtrudeEndCondition.ThroughAll}]};
var result=new GenericPlanCompiler().Compile(draft,Path.Combine(Path.GetTempPath(),"optimization-regression.sldprt"));
foreach(var diagnostic in result.Diagnostics.Where(x=>x.Severity==DiagnosticSeverity.Error))Console.WriteLine(diagnostic);
Check(result.Success,"ThroughAll compiles without a fictitious depth");
Check(result.Diagnostics.Count(d=>d.Code=="EXTRUDE_DIRECTION_TRACE")==2,"standard Front plane reports both extrude directions");
var recipeJson="""{"source_text":"120/60/12 flange, six 9mm holes PCD90 start0","operations":[],"recipe":{"kind":"Flange","outer_diameter_mm":120,"inner_diameter_mm":60,"thickness_mm":12,"hole_diameter_mm":9,"pitch_circle_diameter_mm":90,"hole_count":6,"start_angle_degrees":0}}""";
var recipeDraft=System.Text.Json.JsonSerializer.Deserialize<GenericModelDraft>(recipeJson,ModelingIrJson.Options)!;
var recipeResult=new GenericPlanCompiler().Compile(recipeDraft,Path.Combine(Path.GetTempPath(),"recipe-regression.sldprt"));
Check(recipeResult.Success&&recipeResult.Plan!.Operations.Count>=4,"mechanical flange recipe compiles through public draft contract");
var policyNode=System.Text.Json.Nodes.JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(draft,ModelingIrJson.Options))!;
policyNode["preview_views"]=new System.Text.Json.Nodes.JsonArray("isometric","front");
policyNode["preview_scope_reason"]="All source dimensions and complete hole walls checked; front and isometric cover this simple part.";
var policyDraft=System.Text.Json.JsonSerializer.Deserialize<GenericModelDraft>(policyNode.ToJsonString(),ModelingIrJson.Options)!;
var policyResult=new GenericPlanCompiler().Compile(policyDraft,Path.Combine(Path.GetTempPath(),"policy-regression.sldprt"));
var policyOutput=System.Text.Json.Nodes.JsonNode.Parse(ModelingIrJson.Serialize(policyResult.Plan!))!["output"]!;
Check(policyOutput["preview_views"]?.AsArray().Count==2,"explicit preview scope survives compilation");
var checkpointNode=System.Text.Json.Nodes.JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(draft,ModelingIrJson.Options))!;
checkpointNode["recovery"]=System.Text.Json.Nodes.JsonNode.Parse("""{"enabled":true,"checkpoint_mode":"Explicit","after_operation_ids":[]}""");
var checkpointDraft=System.Text.Json.JsonSerializer.Deserialize<GenericModelDraft>(checkpointNode.ToJsonString(),ModelingIrJson.Options)!;
var checkpointPlan=new GenericPlanCompiler().Compile(checkpointDraft,Path.Combine(Path.GetTempPath(),"checkpoint-regression.sldprt")).Plan!;
Check(ModelingRecovery.CheckpointAfter(checkpointPlan).Count==0,"explicit empty checkpoint scope does not silently add a body checkpoint");
Check(new RuleBasedTextCompiler().Compile("法兰 外径120 中心孔60 厚度12 安装孔径9 孔数6 分度圆90 起始角0",Path.Combine(Path.GetTempPath(),"text-recipe.sldprt")).Success,"complete Chinese flange recipe compiles");
var missing=new RuleBasedTextCompiler().Compile("法兰 外径120 中心孔60 厚度12 安装孔径9",Path.Combine(Path.GetTempPath(),"missing-recipe.sldprt"));
Check(!missing.Success&&missing.Diagnostics.Count(d=>d.Code=="RECIPE_REQUIREMENT")==3,"missing count PCD and angle are grouped without guessing");
Check(!new RuleBasedTextCompiler().Compile("法兰 外径120 中心孔60 厚度12 安装孔径9 孔数6 分度圆90 起始角0 加倒角",Path.Combine(Path.GetTempPath(),"extra-recipe.sldprt")).Success,"unconsumed recipe requirement is rejected");
Check(new SketchQualityObservation{Complete=true,ConstraintStatus=3,DrivingDimensionCount=3,ConsumedBy=["cut"],FixedEntities=[new("fixed_design_point",new(45,0,0))]}.Status=="review_required","fully constrained frozen hole center does not pass editability");
Check(new SketchQualityObservation{Complete=true,ConstraintStatus=3,DrivingDimensionCount=3,ConsumedBy=["cut"],FixedEntities=[new("reference_origin",new(0,0,0))]}.Status=="passed","explicit origin reference is distinguished from a frozen variable hole");
Check(new SketchQualityObservation{Complete=false}.Status=="unverifiable","missing native constraint evidence remains unverifiable");
var noEvidenceRoot=Path.Combine(Path.GetTempPath(),"evidence-regression-"+Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(noEvidenceRoot);
Check(CapabilityEvidence.Read(noEvidenceRoot,"new-version")["status"]!.GetValue<string>()=="not_run","missing native evidence does not inherit upstream certification");
var unscopedDeps=new[]{new ArtifactDependency("model.sha256",new string('A',64)),new("feature_tree.fingerprint","tree"),new("executor.version","executor"),new("solidworks.version","33.0.0"),new("model.reopened","true")};
var unscoped=new CachedArtifact{ArtifactId="edit",Kind=CacheArtifactKind.NativeEditability,SourceRevisionId="source",Dependencies=unscopedDeps};
Check(!ArtifactFreshness.Evaluate(unscoped,new(){SourceRevisionId="source",Dependencies=unscopedDeps}).CacheHit,"editability cache must declare exact probe scope and validator identity");
var blindRecipe = new GenericModelDraft { SourceText="80x60x12 plate,9mm blind hole depth7 at(15,-10)",Operations=[],Recipe=new(){Kind=MechanicalRecipeKind.HoleFamily,
    WidthMm=80,HeightMm=60,ThicknessMm=12,HoleDiameterMm=9,HoleKind=RecipeHoleKind.Blind,DepthMm=7,HoleCenters=[new(15,-10)]}};
var blindPlan = new GenericPlanCompiler().Compile(blindRecipe,Path.Combine(Path.GetTempPath(),"blind-preview.sldprt"));
Check(blindPlan.Success && blindPlan.Plan!.Output.PreviewViews.Contains("back"),"blind-hole recipe includes the opening-side back preview");
var evidenceDirectory=Path.Combine(noEvidenceRoot,"skills","auto-solidworks","references");
Directory.CreateDirectory(evidenceDirectory);
File.WriteAllText(Path.Combine(evidenceDirectory,"current-native-evidence.json"),"""{"schema_version":1,"runtime_version":"older-version"}""");
Check(CapabilityEvidence.Read(noEvidenceRoot,"new-version")["status"]!.GetValue<string>()=="stale_or_unverifiable","wrong native evidence version is a structured stale result, not a tool exception");
