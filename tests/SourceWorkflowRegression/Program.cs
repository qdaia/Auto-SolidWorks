using System.Text.Json;
using System.Text.Json.Nodes;
using CadModeling.Core;
using CadModeling.Ir;

var output = Path.GetFullPath(args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "source-workflow-" + Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(output);
var passed = new List<string>();
void Check(bool ok, string label) { if (!ok) throw new Exception(label); passed.Add(label); Console.WriteLine("PASS " + label); }
void Reject(Action run, string code) { try { run(); } catch (JsonException e) when(e.Message.Contains(code)) { Check(true, code); return; } throw new Exception("Expected " + code); }
var node = JsonNode.Parse("""
{"source_text":"independent source","operations":[{"type":"ProfileSketch","id":"sk","plane":"Front","primitives":[{"type":"CenteredRectangle","width_mm":25.4,"height_mm":10}]}],
"drawing_context":{"source_path":"missing-source.png","dimensions":[{"id":"width","operation_id":"sk","parameter_path":"primitives.0.width_mm","value":1,"unit":"Inch","source_literal":"1 inch"}]},
"verification":{"bounds":[{"id":"bbox","source_literal":"width","source_dimension_ids":["width"],"size_mm":{"x":25.4,"y":10,"z":2}}],"bindings":[{"dimension_id":"width","check_id":"bbox","parameter_path":"size_mm.x"}]}}
""")!;
var original = node.ToJsonString();
var prepared = SourceWorkflowInputs.Prepare(node);
Check(node.ToJsonString() == original, "source input remains immutable");
Check(prepared.Adoption["converted_to_references"]!.GetValue<int>() == 2, "operation and verification bindings become references together");
Check(prepared.Input["operations"]![0]!["primitives"]![0]!["height_mm"] is JsonValue, "undeclared values are not guessed from equality");
var decoded = CompactModelInputs.Decode<GenericModelDraft>(prepared.Input);
Check(decoded.Operations[0].Primitives[0].WidthMm == 25.4, "inch source converted to millimetre consumer");
Check(decoded.DrawingContext is { WorkflowRevision: SourceWorkflow.DefaultRevision, RequireGeometryAnchors: true, RequireDimensionEvidence: true, RequireInterpretationReview: false, RequireShapeDecomposition: false, RequireCompleteBindings: true }, "four-stage default retains binding checks without auxiliary review stages");
prepared.Input["drawing_context"]!["dimensions"]![0]!["value"] = 2;
decoded = CompactModelInputs.Decode<GenericModelDraft>(prepared.Input);
Check(decoded.Operations[0].Primitives[0].WidthMm == 50.8 && decoded.Verification.Bounds[0].SizeMm.X == 50.8, "source edit updates both consumers");
var again = SourceWorkflowInputs.Prepare(prepared.Input);
Check(again.Adoption["existing_references"]!.GetValue<int>() == 2 && again.Adoption["converted_to_references"]!.GetValue<int>() == 0, "preparation is idempotent and preserves references");
var conflict = node.DeepClone(); conflict["operations"]![0]!["primitives"]![0]!["width_mm"] = 23;
var conflicting = SourceWorkflowInputs.Prepare(conflict);
Check(conflicting.Input["operations"]![0]!["primitives"]![0]!["width_mm"]!.GetValue<int>() == 23 && conflicting.Adoption["unresolved_consumers"]!.AsArray().Count == 1, "contradictory values preserved for diagnostics");
Check(DrawingPlanValidation.Validate(CompactModelInputs.Decode<GenericModelDraft>(conflicting.Input)).Any(d => d.Code == "DRAWING_BINDING" && d.Message.Contains("不匹配")), "mismatched consumer still fails independent binding validation");
var disabled = node.DeepClone(); disabled["drawing_context"]!["require_geometry_anchors"] = false;
Check(SourceWorkflow.Validate(CompactModelInputs.Decode<GenericModelDraft>(SourceWorkflowInputs.Prepare(disabled).Input).DrawingContext!).Any(d => d.Code == "SOURCE_WORKFLOW_GATES"), "explicit disabled gate is rejected rather than silently overwritten");
Check(SourceWorkflow.Validate(new DrawingPlanContext { SourcePath = "legacy", RequireGeometryAnchors = false }).Count == 0, "historical compiled context retains policy identity");
var duplicate = node.DeepClone(); var fact = duplicate["drawing_context"]!["dimensions"]![0]!.DeepClone(); fact["id"] = "another"; duplicate["drawing_context"]!["dimensions"]!.AsArray().Add(fact);
Reject(() => SourceWorkflowInputs.Prepare(duplicate), "SOURCE_CONSUMER_AMBIGUOUS");
var wrongRef = node.DeepClone(); wrongRef["operations"]![0]!["primitives"]![0]!["width_mm"] = JsonNode.Parse("""{"$dimension":"another","unit":"Millimeter"}""");
Reject(() => SourceWorkflowInputs.Prepare(wrongRef), "SOURCE_CONSUMER_ID_CONFLICT");
var noSource = JsonNode.Parse("""{"source_text":"text","operations":[]}""")!;
Check(JsonNode.DeepEquals(SourceWorkflowInputs.Prepare(noSource).Input, noSource), "text-only workflow is unchanged");
var store = new CompactArtifactStore(Path.Combine(output, "store"), "workflow-regression");
var sourceFile=Path.Combine(output,"source.txt"); File.WriteAllText(sourceFile,"independent source fixture");
node["drawing_context"]!["source_path"]=sourceFile;
var stored = JsonSerializer.SerializeToNode(CompactModelInputs.Store(store, "draft", JsonSerializer.SerializeToElement(node), null, null), ModelingIrJson.Options)!;
var saved = store.LoadInput(stored["input_id"]!.GetValue<string>(), "draft");
Check(saved["operations"]![0]!["primitives"]![0]!["width_mm"] is JsonObject && stored["source_workflow"]!["anchors_required"]!.GetValue<bool>(), "MCP store persists canonical references and exposes adoption");
var workflow=new ModelingWorkflow(new(),new(),new NamedPipeModelingExecutor("never-contact-native-in-this-test"));
var direct=JsonSerializer.SerializeToNode(CompactModelingTools.CreatePlan(new(),workflow,store,Path.Combine(output,"unreviewed.SLDPRT"),draft:JsonSerializer.SerializeToElement(node)),ModelingIrJson.Options)!;
Check(direct["success"]!.GetValue<bool>()==false && direct["prepared_draft_id"] is not null &&
    store.LoadInput(direct["prepared_draft_id"]!.GetValue<string>(),"draft")["operations"]![0]!["primitives"]![0]!["width_mm"] is JsonObject,
    "direct compilation persists canonical reference input even when source review is incomplete");

var invalid = decoded with {
    DrawingContext=decoded.DrawingContext! with {WorkflowRevision=SourceWorkflow.Revision,RequireInterpretationReview=true,RequireShapeDecomposition=true},
    PreviewViews = ["isometric"], PreviewScopeReason = "diagnostic regression",
    Operations = [new GenericOperationDraft { Id="broken", Type=GenericOperationKind.ProfileSketch, Plane=ReferencePlane.Front,
        Primitives=[new GenericPrimitiveDraft {Type=GenericPrimitiveKind.CompositeCurve, Curves=[]}] },
        new GenericOperationDraft { Id="poly", Type=GenericOperationKind.ProfileSketch, Plane=ReferencePlane.Front, AutoDimensionPrimitives=true,
        Primitives=[new GenericPrimitiveDraft {Type=GenericPrimitiveKind.Polygon, Points=[new(0,0),new(10,0),new(0,10)]}] }],
    Verification = new ModelVerificationSpec { CylinderGroups=[new CylinderGroupCheck {Id="invalid-cylinder", SourceLiteral="source", DiameterMm=10, LengthMm=20, Direction=new(0,0,1), AxisStartsMm=[new(0,0,0)], Coverage=CylinderCoverage.TrimmedAxisAndDiameter}] }
};
var compiled = new GenericPlanCompiler().Compile(invalid, Path.Combine(output,"never-built.SLDPRT"));
Check(compiled.Plan is null, "invalid mixed draft never exposes a partial executable plan");
foreach(var code in new[]{"DRAWING_BINDING","GPC045","IR_AUTO_DIMENSION","VERIFICATION_SPEC","OUT_PREVIEW_COVERAGE"})
    Check(compiled.Diagnostics.Any(d=>d.Code==code), "single preflight reports " + code);
PlacementChecks.Run(Check);
NativeNameChecks.Run(Check);
CheckpointChecks.Run(output,Check);
await DeliveryChecks.Run(output,Check);
File.WriteAllText(Path.Combine(output,"summary.json"),JsonSerializer.Serialize(new{passed=true,checks=passed.Count,native_execution=false,results=passed},ModelingIrJson.Options));
