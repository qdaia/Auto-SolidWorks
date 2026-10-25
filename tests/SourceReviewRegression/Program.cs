using System.Text.Json;
using System.Text.Json.Nodes;
using CadModeling.Core;
using CadModeling.Ir;

var output=Path.GetFullPath(args[0]);Directory.CreateDirectory(output);
var checks=new List<string>();
void Check(bool ok,string name){if(!ok)throw new Exception(name);checks.Add(name);Console.WriteLine("PASS "+name);}
var evidence=new DrawingDimensionEvidence{FeatureId="bore",MeasurementId="diameter",ViewId="front",QuantityKind=DrawingQuantityKind.Diameter,
    TextBounds=new(.1,.1,.3,.2),TargetBounds=new(.6,.4,.8,.6),Datum="bore axis",AttachmentExplanation="leader to bore"};
var fact=new DrawingDimensionFact{Id="diameter",OperationId="hole",ParameterPath="feature.diameter_mm",Value=10,SourceLiteral="Ø10",BindingEvidence=evidence};
DrawingPlanContext Context(DrawingDimensionFact f)=>new(){SourcePath="fixture.png",WorkflowRevision=SourceWorkflow.Revision,Dimensions=[f]};
Check(DimensionCalloutValidation.Assess(fact,true).AutomaticCoverage==DrawingCalloutCoverage.Verified,"simple literal has explicit verified nominal coverage");
var partial=fact with{SourceLiteral="Ø10 深12"};
var assessment=DimensionCalloutValidation.Assess(partial,true);
Check(assessment.AutomaticCoverage==DrawingCalloutCoverage.Partial && assessment.SourceReviewRequired && assessment.Diagnostics.Any(d=>d.Code=="DIMENSION_CALLOUT_REVIEW_REQUIRED"),"partial complex callout is blocked pending visual component review");
var thread=fact with{SourceLiteral="M10×1.5-6H"};
Check(DimensionCalloutValidation.Assess(thread,true).AutomaticCoverage==DrawingCalloutCoverage.Unsupported,"thread grammar explicitly unsupported");
Check(DimensionCalloutValidation.Assess(fact with{SourceLiteral="Ø12"},true).AutomaticCoverage==DrawingCalloutCoverage.Conflict,"wrong literal has conflict coverage");
Check(DimensionCalloutValidation.Assess(fact with{Status=DrawingFactStatus.Derived},true).AutomaticCoverage==DrawingCalloutCoverage.NotApplicable,"derived formula is not mistaken for source literal parsing");
var reviewed=partial with{CalloutReview=new(){State=DrawingReviewState.Reviewed,SourceLiteral=partial.SourceLiteral,SelectedComponent="bore nominal diameter; depth is separate",QuantityKind=DrawingQuantityKind.Diameter,Value=10,Unit=DrawingValueUnit.Millimeter,Evidence="original leader points to inner circle; depth suffix binds separate depth fact"}};
assessment=DimensionCalloutValidation.Assess(reviewed,true);
Check(assessment.AutomaticCoverage==DrawingCalloutCoverage.Partial && assessment.VisualReviewComplete && !assessment.SourceReviewRequired && assessment.Diagnostics.Count==0,"visual review resolves gate without claiming automatic parser support");
Check(DimensionCalloutValidation.Assess(reviewed with{SourceLiteral="Ø12 深12"},true).Diagnostics.Any(d=>d.Code=="DIMENSION_CALLOUT_REVIEW_STALE"),"source text edit invalidates previous review");
Check(DimensionCalloutValidation.Assess(reviewed with{CalloutReview=reviewed.CalloutReview! with{UnresolvedTokens=["depth?"]}},true).Diagnostics.Any(d=>d.Code=="DIMENSION_CALLOUT_REVIEW_INVALID"),"unresolved symbols cannot be auto-reviewed");
Check(DimensionCalloutValidation.Assess(reviewed with{CalloutReview=reviewed.CalloutReview! with{Value=12}},true).Diagnostics.Any(d=>d.Code=="DIMENSION_CALLOUT_REVIEW_INVALID"),"depth cannot replace visually selected diameter");
Check(DimensionCalloutValidation.Assess(partial,false).Diagnostics.Count==0 && DimensionCalloutValidation.Assess(partial,false).SourceReviewRequired,"legacy retains compile compatibility but exposes missing automatic coverage");
Check(DimensionCalloutValidation.Assess(Context(partial with{Critical=false}) with{Features=[new(){Id="bore",SourceLiteral="bore",Critical=true,CriticalParameters=[new(){DimensionId="diameter",OperationId="hole",ParameterPath="feature.diameter_mm"}]}]}).Single().Diagnostics.Count>0,"critical feature cannot bypass unparsed review with fact flag");
var readiness=PlanningReadiness.Collect(new GenericModelDraft{SourceText="source",Operations=[],DrawingContext=Context(partial)},JsonNode.Parse("{\"success\":false,\"diagnostics\":[]}")!.AsObject());
Check(readiness["callout_source_review_required_count"]!.GetValue<int>()==1 && readiness["callout_coverage"]!.AsArray().Count==1,"compile readiness exposes per-callout coverage and pending count");
var many=PlanningReadiness.Collect(new GenericModelDraft{SourceText="source",Operations=[],DrawingContext=Context(partial) with{Dimensions=Enumerable.Range(0,20).Select(i=>partial with{Id="d"+i}).ToArray()}},JsonNode.Parse("{\"success\":false,\"diagnostics\":[]}")!.AsObject());
Check(PlanningReadiness.Summarize(many)["callout_coverage_truncated"]!.GetValue<bool>() && many["callout_coverage"]!.AsArray().Count==20,"summary truncation retains full coverage result for paging");
TraceChecks.Run(output,Check);
MaterialChecks.Run(output,Check);
File.WriteAllText(Path.Combine(output,"summary.json"),JsonSerializer.Serialize(new{passed=checks.Count,checks,native_execution=false},new JsonSerializerOptions{WriteIndented=true}));
