using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CadModeling.Core;
using CadModeling.Core.Evaluation;
using CadModeling.Drawing.Contracts;
using CadModeling.Drawing.Ingestion;
using CadModeling.Drawing.Providers.Abstractions;
using CadModeling.Ir;

var root=Path.GetFullPath(args.FirstOrDefault()??Path.Combine("artifacts","omission-core-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")));
Directory.CreateDirectory(root);
var results=new List<string>();
void Check(bool ok,string name) {if(!ok)throw new Exception(name);results.Add(name);Console.WriteLine("PASS "+name);}
void Save<T>(string name,T value)=>File.WriteAllText(Path.Combine(root,name),JsonSerializer.Serialize(value,new JsonSerializerOptions(ModelingIrJson.Options){WriteIndented=true}));
bool Throws<T>(Action action) where T:Exception {try {action();return false;} catch(T) {return true;}}
var source=Path.Combine(root,"four-hole-source.png");
Fixture.Draw(source);
var originalHash=DrawingPlanValidation.FileHash(source);
// Known detector omission: the primitive provider deliberately emits ONLY the outer rectangle.
// Raw source contains four holes. This tests downstream missing-detector handling, not detector accuracy.
var service=new EngineeringDrawingIngestionService(text:new FixtureText(),primitives:new OuterContourOnly());
var input=new DrawingIngestionRequest{InputPath=source,ArtifactRoot=Path.Combine(root,"ingestion")};
var ingested=await service.IngestAsync(input);
Save("ingestion.json",ingested);
Check(ingested.Outcome!=IngestionOutcome.Rejected,"real raster ingestion returns omission evidence");
Check(ingested.SourceFacts is not null&&File.Exists(ingested.SourceFactsPath)&&ingested.SourceFactsRevisionId==ingested.SourceFacts.RevisionId,"T02 cad_read_drawing ingestion emits a versioned independent source-facts document");
Check(ingested.SourceFacts!.Facts.All(f=>f.Fact.Status==FactStatus.Unknown),"T02 raw OCR/vision dimension candidates are not silently promoted to stated truth");
var inventoryPath=ingested.OmissionInventoryPath!;
var inventory=DrawingOmissionValidation.Load(inventoryPath,ingested.OmissionInventorySha256);
Check(inventory.Candidates.Any(c=>c.Kind==DrawingCandidateKind.ResidualInk),"unrecognized interior holes survive outer-contour masking");
Check(inventory.Regions.Any(r=>r.ResidualInkPixels>500),"independent raw foreground retains omitted hole strokes");
Check(inventory.Candidates.Where(c=>c.Kind==DrawingCandidateKind.Annotation).All(c=>c.ObservationIds.Count==1),"annotation source IDs retained");
Check(inventory.Regions.Count(r=>r.Purpose=="detail")>=2,"large source produces overlapping raw detail crops");
Check(inventory.Candidates.All(c=>c.ReviewRegionIds.Count>0),"every candidate has a raw review region");
Check(inventory.Regions.Any(r=>r.Purpose=="overview"),"full-page overview always required");
var sourceBytes=File.ReadAllBytes(source);
Check(DrawingPlanValidation.FileHash(source)==originalHash,"ingestion preserves source bytes");
var starts=DrawingOmissionInventoryBuilder.TileStarts(4000);
Check(starts[0]==0&&starts[^1]+DrawingOmissionInventoryBuilder.TileSize==4000,"tiling covers both page edges");
Check(starts.Zip(starts.Skip(1)).All(p=>p.Second-p.First<=DrawingOmissionInventoryBuilder.TileSize-DrawingOmissionInventoryBuilder.TileOverlap),"tile seams retain overlap");
Check(DrawingOmissionInventoryBuilder.TileStarts(100).SequenceEqual(new[]{0}),"small image has one original-resolution tile");
var transform=new TransformRecord{FromFrameId="pdf",ToFrameId="pixel",ForwardMatrix=[new[]{2d,0d,0d},new[]{0d,-2d,200d},new[]{0d,0d,1d}],InverseMatrix=[new[]{.5,0d,0d},new[]{0d,-.5,100d},new[]{0d,0d,1d}]};
Check(DrawingOmissionInventoryBuilder.TryTransform(new(){CoordinateFrameId="pdf",X=10,Y=20},"pixel",[transform],out var point)&&point==(20d,160d),"PDF-to-pixel origin and scale mapped explicitly");
Check(DrawingOmissionInventoryBuilder.TryTransform(new(){CoordinateFrameId="pixel",X=20,Y=160},"pdf",[transform],out point)&&point==(10d,20d),"inverse coordinate mapping round trips");
Check(!DrawingOmissionInventoryBuilder.TryTransform(new(){CoordinateFrameId="unknown"},"pixel",[transform],out _),"unknown coordinate frame cannot be matched by proximity");
DrawingOmissionCandidate Piece(string id,string literal,double left,double top,double right,double bottom)=>new(){Id=id,Literal=literal,Kind=DrawingCandidateKind.Annotation,PageNumber=1,Bounds=new(left,top,right,bottom),ObservationIds=[id]};
var countPiece=Piece("count-piece","4×",.1,.1,.14,.14);var sizePiece=Piece("size-piece","⌀6",.145,.1,.19,.14);
var compounds=DrawingAnnotationAssembler.Assemble([countPiece,sizePiece]);
Check(compounds.Count==1&&compounds[0].NumericValue==6&&compounds[0].Multiplicity==4,"split repeated-hole callout retains size and count");
Check(compounds[0].ObservationIds.Count==2&&compounds[0].RelatedCandidateIds.Count==2,"assembled callout retains both source fragments");
Check(DrawingAnnotationAssembler.Assemble([countPiece,sizePiece with{PageNumber=2}]).Count==0,"annotation assembly cannot cross pages");
Check(DrawingAnnotationAssembler.Assemble([countPiece,sizePiece with{Bounds=new(.7,.7,.8,.8)}]).Count==0,"distant symbols cannot be assembled");
Check(DrawingAnnotationAssembler.Assemble([countPiece with{Literal="80"},sizePiece with{Literal="50"}]).Count==0,"neighboring bare numbers are not concatenated");
Check(DrawingAnnotationAssembler.Assemble([countPiece,sizePiece,sizePiece with{Id="ambiguous",Literal="⌀8",ObservationIds=["ambiguous"]}]).Count==0,"ambiguous adjacent alternatives remain unresolved");
var pitch=DrawingAnnotationAssembler.Assemble([countPiece with{Literal="M10×"},sizePiece with{Literal="1"}]);
Check(pitch.Count==1&&pitch[0].SecondaryValue==1&&pitch[0].DimensionKind=="thread","split metric thread pitch retained");
Check(EngineeringDimensionParser.Extract([new(){ObservationId="overflow",Kind=ObservationKind.Text,RawLiteral="99999999999999999999999×⌀6"}]).Count==0,"overflowing OCR count remains raw evidence without crashing extraction");

var draft=Fixture.Draft(source,inventoryPath,ingested.OmissionInventorySha256!,inventory);
var context=draft.DrawingContext!;
var review=context.OmissionReview!;
var assessment=DrawingOmissionValidation.Evaluate(context);
Save("valid-assessment.json",assessment);Save("valid-draft.json",draft);
Check(assessment.Passed,"explicit source-derived candidate and crop reviews pass");
Check(!assessment.FullDrawingEquivalence&&assessment.Status=="declared_review_complete","review completeness never claims drawing equivalence");
var residueDecisions=review.Candidates.Where(c=>inventory.Candidates.Any(i=>i.Id==c.CandidateId&&i.Kind==DrawingCandidateKind.ResidualInk)).ToArray();
var batchReview=review with{Candidates=review.Candidates.Except(residueDecisions).Append(residueDecisions[0] with{CandidateId="",CandidateIds=residueDecisions.Select(c=>c.CandidateId).ToArray()}).ToArray()};
Check(DrawingOmissionValidation.Evaluate(context with{OmissionReview=batchReview}).Passed,"explicit ID batch preserves individual candidate accounting");
Check(!DrawingOmissionValidation.Evaluate(context with{OmissionReview=batchReview with{Candidates=batchReview.Candidates.Append(residueDecisions[0]).ToArray()}}).Passed,"overlapping review batches are rejected");
Check(!DrawingOmissionValidation.Evaluate(context with{OmissionReview=review with{Candidates=[review.Candidates[0] with{CandidateIds=[review.Candidates[0].CandidateId]}]}}).Passed,"ambiguous scalar and batch IDs are rejected");
void Reject(string name,DrawingPlanContext changed,string? code=null)
{
    var r=DrawingOmissionValidation.Evaluate(changed);
    Check(!r.Passed&&(code is null||r.Issues.Any(i=>i.Code==code)),name);
}
var annotation=inventory.Candidates.Single(c=>c.Literal=="4×⌀6");
var first=review.Candidates.First(c=>c.CandidateId==annotation.Id);
DrawingOmissionReview Replace(DrawingCandidateReview decision)=>review with{Candidates=review.Candidates.Select(c=>c.CandidateId==decision.CandidateId?decision:c).ToArray()};
Reject("missing review blocks complete drawing",context with{OmissionReview=null},"DRAWING_OMISSION_MISSING");
Reject("omitted annotation cannot disappear with plan omission",context with{OmissionReview=review with{Candidates=review.Candidates.Where(c=>c.CandidateId!=annotation.Id).ToArray()}},"DRAWING_OMISSION_UNACCOUNTED");
Reject("unresolved candidate blocks",context with{OmissionReview=Replace(first with{Disposition=DrawingCandidateDisposition.Unresolved})});
Reject("empty explanation blocks",context with{OmissionReview=Replace(first with{Rationale=""})});
Reject("missing mapped feature blocks",context with{Features=context.Features.Where(f=>f.Id!="holes").ToArray()},"DRAWING_OMISSION_TARGET");
Reject("wrong candidate feature target blocks",context with{OmissionReview=Replace(first with{FeatureIds=["missing"]})},"DRAWING_OMISSION_TARGET");
Reject("same value without source observation binding blocks",context with{Dimensions=context.Dimensions.Select(d=>d.Id=="diameter"?d with{ObservationIds=[]}:d).ToArray()},"DRAWING_OMISSION_BINDING");
Reject("4-hole annotation versus 2-hole count rejected",context with{Dimensions=context.Dimensions.Select(d=>d.Id=="count"?d with{Value=2}:d).ToArray()},"DRAWING_OMISSION_COUNT");
Reject("diameter annotation versus wrong size rejected",context with{Dimensions=context.Dimensions.Select(d=>d.Id=="diameter"?d with{Value=8}:d).ToArray()},"DRAWING_OMISSION_QUANTITY");
Reject("missing count binding rejected",context with{OmissionReview=Replace(first with{DimensionIds=["diameter"]})},"DRAWING_OMISSION_COUNT");
Reject("numeric annotation cannot be called a border",context with{OmissionReview=Replace(first with{Disposition=DrawingCandidateDisposition.NonModel,FeatureIds=[],DimensionIds=[],NonModelCategory="border"})},"DRAWING_OMISSION_NONMODEL");
Reject("unsupported dismissal category rejected",context with{OmissionReview=Replace(first with{Disposition=DrawingCandidateDisposition.NonModel,FeatureIds=[],DimensionIds=[],NonModelCategory="ignore"})});
Reject("missing full-page inspection rejected",context with{OmissionReview=review with{Regions=review.Regions.Where(r=>r.RegionId!=inventory.Regions[0].Id).ToArray()}},"DRAWING_OMISSION_REGION");
Reject("unreviewed local crop rejected",context with{OmissionReview=review with{Regions=review.Regions.Select((r,i)=>i==1?r with{State=DrawingReviewState.Unresolved}:r).ToArray()}},"DRAWING_OMISSION_REGION");
Reject("duplicate decision IDs rejected",context with{OmissionReview=review with{Candidates=review.Candidates.Append(first).ToArray()}},"DRAWING_OMISSION_IDS");
Reject("unknown candidate ID rejected",context with{OmissionReview=review with{Candidates=review.Candidates.Append(first with{CandidateId="invented"}).ToArray()}},"DRAWING_OMISSION_UNKNOWN_ID");
Reject("self duplicate rejected",context with{OmissionReview=Replace(first with{Disposition=DrawingCandidateDisposition.Duplicate,DuplicateOf=first.CandidateId,FeatureIds=[],DimensionIds=[]})},"DRAWING_OMISSION_DUPLICATE");
var distant=review.Candidates.First(c=>c.CandidateId!=first.CandidateId);
Reject("unrelated distant annotation cannot be deduplicated",context with{OmissionReview=Replace(first with{Disposition=DrawingCandidateDisposition.Duplicate,DuplicateOf=distant.CandidateId,FeatureIds=[],DimensionIds=[]})},"DRAWING_OMISSION_DUPLICATE");
Reject("OCR correction cannot erase a quantitative requirement",context with{OmissionReview=Replace(first with{CorrectedLiteral="unreadable",CorrectionReason="test"})},"DRAWING_OMISSION_CORRECTION");
Reject("correction without reason rejected",context with{OmissionReview=Replace(first with{CorrectedLiteral="4×⌀6"})},"DRAWING_OMISSION_CORRECTION");
Check(DrawingOmissionValidation.Evaluate(context with{OmissionReview=Replace(first with{CorrectedLiteral="4×⌀6",CorrectionReason="Independently read the original high-resolution crop."})}).Passed,"source-based correction with unchanged original inventory is supported");
var multi=context with{Views=context.Views.Append(new(){Id="top",Kind=ModelDrawingView.Top}).ToArray(),Features=context.Features.Select(f=>f.Id=="holes"?f with{ViewIds=["front","top"]}:f).ToArray()};
Reject("multi-view feature without consistency check rejected",multi,"DRAWING_OMISSION_CROSS_VIEW");
var cross=new DrawingCrossViewReview{Id="holes-front-top",ViewIds=["front","top"],FeatureIds=["holes"],State=DrawingReviewState.Reviewed,Evidence="Contract fixture only: explicit two-view correspondence."};
Check(DrawingOmissionValidation.Evaluate(multi with{OmissionReview=review with{CrossViewChecks=[cross]}}).Passed,"declared cross-view relation accounted without claiming geometric proof");
Reject("cross-view conflict blocks",multi with{OmissionReview=review with{CrossViewChecks=[cross with{State=DrawingReviewState.Conflict}]}},"DRAWING_OMISSION_CROSS_VIEW");
Reject("additional visually found slot cannot be omitted",context with{OmissionReview=review with{AdditionalFindings=[new(){Id="missed-slot",PageNumber=1,Bounds=new(.2,.2,.3,.3),Description="A slot found on second review.",State=DrawingReviewState.Unresolved}]}},"DRAWING_OMISSION_FINDING");
Reject("inventory hash mismatch rejected",context with{OmissionReview=review with{InventorySha256=new string('0',64)}},"DRAWING_OMISSION_INTEGRITY");
Reject("different source path rejected",context with{SourcePath=Path.Combine(root,"other.png")},"DRAWING_OMISSION_SOURCE");
var variantIndex=0;
DrawingPlanContext Variant(DrawingOmissionInventory altered)
{
    var path=Path.Combine(Path.GetDirectoryName(inventoryPath)!,"test-variant-"+(++variantIndex)+".json");
    File.WriteAllText(path,JsonSerializer.Serialize(altered,ModelingIrJson.Options));
    return context with{OmissionReview=review with{InventoryPath=path,InventorySha256=DrawingPlanValidation.FileHash(path)}};
}
Reject("capped extraction cannot pass",Variant(inventory with{CompleteExtraction=false}),"DRAWING_OMISSION_EXTRACTION");
Reject("unread source pages cannot pass",Variant(inventory with{TotalPages=2,Pages=inventory.Pages.Append(new(){PageNumber=2,Ingested=false}).ToArray()}),"DRAWING_OMISSION_PAGE");
Reject("missing raw crop cannot pass",Variant(inventory with{Regions=inventory.Regions.Select((r,i)=>i==0?r with{ImagePath=Path.Combine(Path.GetDirectoryName(inventoryPath)!,"missing.png")}:r).ToArray()}),"DRAWING_OMISSION_INTEGRITY");
Reject("changed observation hash rejected",Variant(inventory with{Pages=inventory.Pages.Select(p=>p with{ObservationSha256=new string('0',64)}).ToArray()}),"DRAWING_OMISSION_INTEGRITY");
Reject("out-of-page candidate location rejected",Variant(inventory with{Candidates=inventory.Candidates.Select((c,i)=>i==0?c with{Bounds=new(-1,0,.5,.5)}:c).ToArray()}),"DRAWING_OMISSION_INTEGRITY");
Reject("unknown region reference rejected",Variant(inventory with{Candidates=inventory.Candidates.Select((c,i)=>i==0?c with{ReviewRegionIds=["invented"]}:c).ToArray()}),"DRAWING_OMISSION_INTEGRITY");
Reject("detector conflict needs a source reading",Variant(inventory with{Candidates=inventory.Candidates.Select(c=>c.Id==annotation.Id?c with{Conflict=true}:c).ToArray()}),"DRAWING_OMISSION_CONFLICT");

var fragment=annotation with{Id="split-count",Literal="4×",NumericValue=null,Multiplicity=null,DimensionKind=null};
var compoundContext=Variant(inventory with{Candidates=inventory.Candidates.Select(c=>c.Id==annotation.Id?c with{Provider="spatial-annotation-assembler",RelatedCandidateIds=[fragment.Id]}:c).Append(fragment).ToArray()});
var fragmentDecision=new DrawingCandidateReview{CandidateId=fragment.Id,Disposition=DrawingCandidateDisposition.Duplicate,DuplicateOf=annotation.Id,Rationale="Contract variant: count fragment is retained by the compound callout."};
compoundContext=compoundContext with{OmissionReview=compoundContext.OmissionReview! with{Candidates=review.Candidates.Append(fragmentDecision).ToArray()}};
Check(DrawingOmissionValidation.Evaluate(compoundContext).Passed,"resolved assembled callout accounts for its retained source fragment");
Reject("unresolved assembled callout cannot account for a fragment",compoundContext with{OmissionReview=compoundContext.OmissionReview! with{Candidates=compoundContext.OmissionReview!.Candidates.Select(c=>c.CandidateId==annotation.Id?c with{Disposition=DrawingCandidateDisposition.Unresolved}:c).ToArray()}},"DRAWING_OMISSION_DUPLICATE");

// Seed a full accumulated inventory to exercise the next-page capacity branch without 50 pages of detector work.
var cappedBuilder=new DrawingOmissionInventoryBuilder();
var accumulated=(List<DrawingOmissionCandidate>)typeof(DrawingOmissionInventoryBuilder).GetField("candidates",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.GetValue(cappedBuilder)!;
accumulated.AddRange(Enumerable.Range(0,DrawingOmissionInventoryBuilder.MaximumCandidatesTotal).Select(i=>new DrawingOmissionCandidate{Id="capacity-"+i,PageNumber=1,Kind=DrawingCandidateKind.Geometry,Bounds=new(.1,.1,.2,.2),ReviewRegionIds=["page-0001-overview"]}));
var capRoot=Path.Combine(root,"capacity-boundary");Directory.CreateDirectory(capRoot);
var rawCap=Path.Combine(capRoot,"ocr-source.png");File.Copy(source,rawCap);
var emptyObservation=new DrawingObservationDocument{SourceSha256=originalHash};
var capObservation=Path.Combine(capRoot,"observation.json");File.WriteAllText(capObservation,JsonSerializer.Serialize(emptyObservation,ModelingIrJson.Options));
cappedBuilder.AddPage(emptyObservation,capObservation,new(){NormalizedPage=new(){PageNumber=1,ImagePath=rawCap,CoordinateFrameId="capacity-pixels"}},capRoot);
var capSaved=cappedBuilder.Save(source,originalHash,1,capRoot);var capInventory=DrawingOmissionValidation.Load(capSaved.Path,capSaved.Sha256);
Check(capInventory.Candidates.Count==50000&&!capInventory.CompleteExtraction,"full document candidate capacity stays loadable and explicitly incomplete");
Check(capInventory.Pages[0].Limitations.Any(l=>l.Contains("Candidate budget 0 exceeded"))&&capInventory.Regions.Count>1,"capacity exhaustion preserves raw overview and detail evidence");

var compiler=new GenericPlanCompiler();
var compiled=compiler.Compile(draft,Path.Combine(root,"fixture.SLDPRT"));Save("compiled.json",compiled);
Check(compiled.Success,"complete reviewed drawing compiles to typed executable plan");
Check(new ModelingIrValidator().Validate(compiled.Plan!,true).IsValid,"reviewed plan passes execution validation without SolidWorks");
Check(!compiler.Compile(draft with{DrawingContext=context with{OmissionReview=null}},Path.Combine(root,"missing.SLDPRT")).Success,"compiler rejects absent omission review");
var fewer=draft with{Operations=draft.Operations.Select(o=>o.Id=="holes"?o with{Feature=o.Feature! with{HoleCenters=o.Feature!.HoleCenters.Take(2).ToArray()}}:o).ToArray()};
Check(!compiler.Compile(fewer,Path.Combine(root,"two-holes.SLDPRT")).Success,"computed hole_center_count binding detects shortened center list before execution");
var changedPlan=compiled.Plan! with{DrawingContext=context with{OmissionReview=null}};
Check(!new ModelingIrValidator().Validate(changedPlan,true).IsValid,"compiled review removal fails execution validation");
var partial=draft with{DrawingContext=context with{OmissionReview=null,RequireCompleteBindings=false}};
var partialResult=compiler.Compile(partial,Path.Combine(root,"partial.SLDPRT"));
Check(partialResult.Success&&partialResult.Diagnostics.Any(d=>d.Code=="DRAWING_OMISSION_PARTIAL"),"explicit partial legacy contract carries an omission warning");
try
{
    File.WriteAllBytes(source,sourceBytes.Concat(new byte[]{0}).ToArray());
    Reject("source byte change invalidates review",context,"DRAWING_OMISSION_INTEGRITY");
    Check(!new ModelingIrValidator().Validate(compiled.Plan!,true).IsValid,"build rechecks source after compile");
}
finally {File.WriteAllBytes(source,sourceBytes);}
Check(DrawingPlanValidation.FileHash(source)==originalHash,"generated test source restored exactly");

// AUTO SW 02 stage-1 contracts: T01-T05 code-level acceptance. These are deterministic contract/algorithm
// checks only; they do not claim a blind industrial-drawing or installed SolidWorks acceptance run.
var evalHash=new string('A',64);
var evalCases=new[]{
    new EvaluationCaseManifest{CaseId="dev-a",PartIdentity="plate-a",VariantGroupId="plate-a-all",Partition=EvaluationPartition.RealDevelopment,SourceSha256=evalHash,GeneratorInputArtifactIds=["drawing"],TruthArtifactIds=["gold-cad"]},
    new EvaluationCaseManifest{CaseId="holdout-b",PartIdentity="plate-b",VariantGroupId="plate-b-all",Partition=EvaluationPartition.RealHoldout,SourceSha256=evalHash,GeneratorInputArtifactIds=["drawing-b"],TruthArtifactIds=["gold-b"]}
};
Check(EvaluationIsolation.Validate(evalCases).Count==0,"T01 isolated evaluation partitions accept independent parts");
Check(EvaluationIsolation.Validate([evalCases[0] with{TruthArtifactIds=["drawing"]}]).Any(i=>i.Code=="EVAL_TRUTH_LEAK"),"T01 evaluator truth cannot enter generator inputs");
Check(EvaluationIsolation.Validate([evalCases[0] with{GeneratorInputArtifactIds=["gold-b"]},evalCases[1]]).Any(i=>i.Code=="EVAL_TRUTH_LEAK"),"T01 truth from another case cannot enter generator inputs");
Check(EvaluationIsolation.Validate([evalCases[0],evalCases[1] with{PartIdentity="plate-a",VariantGroupId="plate-a-all"}]).Any(i=>i.Code is "EVAL_PARTITION_LEAK" or "EVAL_VARIANT_LEAK"),"T01 physical-part variants cannot cross development and holdout");
var evalResults=new[]{new EvaluationCaseResult{CaseId="dev-a",BaselineFingerprint=evalHash,SourceSha256=evalHash,Initial=new(){AttemptId="first",Status=EvaluationCaseStatus.Passed,Built=true,Reopened=true,RequiredScopePassed=true,NativeEditable=true}}};
var evalMetrics=EvaluationMetrics.Compute([evalCases[0]],evalResults);
Check(evalMetrics.CompleteBuildableDenominator==1&&evalMetrics.BuiltNumerator==1&&evalMetrics.ScopePassNumerator==1,"T01 metrics preserve explicit numerator and denominator");
Check(EvaluationMetrics.Compute([evalCases[0]],
    [evalResults[0] with{SourceSha256=new string('B',64)}]).ScopePassNumerator==0,
    "T01 stale result with wrong source hash cannot count as scope pass");
Check(EvaluationMetrics.Compute([evalCases[0]],
    [evalResults[0] with{Initial=evalResults[0].Initial with{Status=EvaluationCaseStatus.Failed}}]).ScopePassNumerator==0,
    "T01 failed attempt cannot count as scope pass even when RequiredScopePassed is true");
var requiredEvalCase=evalCases[0] with{RequiredRequirementIds=["hole-required"]};
var failedRequirementResult=evalResults[0] with{Initial=evalResults[0].Initial with{RequirementOutcomes=new SortedDictionary<string,string>{{"hole-required","failed"}}}};
Check(EvaluationMetrics.Compute([requiredEvalCase],[failedRequirementResult]).ScopePassNumerator==0,
    "T01 failed required requirement cannot be overridden by aggregate RequiredScopePassed");
Check(EvaluationMetrics.Compute([evalCases[0]],evalResults,expectedBaselineFingerprint:new string('C',64)).ScopePassNumerator==0,
    "T01 result from a different baseline cannot count when the expected baseline is frozen");

SourceFact StatedFact(string id,SourceFactKind kind,double value,MeasurementUnit unit,string observation)=>new()
{
    FactId=id,Kind=kind,PageNumber=1,ViewId="front",SourceRegionId="region-front",NumericValue=value,Unit=unit,
    InterpretedLiteral=id,EvidenceIds=[observation],Candidates=[new(){CandidateId="candidate-"+id,RawLiteral=value.ToString(CultureInfo.InvariantCulture),NumericValue=value,Unit=unit,Interpretation=id,Confidence=1,EvidenceStatus=EvidenceStatus.Confirmed,EvidenceIds=[observation]}],
    Fact=new(){Status=FactStatus.Stated,SourceIds=[observation],Rationale="fixture source annotation"}
};
var totalFact=StatedFact("total",SourceFactKind.LinearDimension,100,MeasurementUnit.Millimeter,"obs-total");
var segmentFact=StatedFact("segment",SourceFactKind.LinearDimension,30,MeasurementUnit.Millimeter,"obs-segment");
var derivedFact=new SourceFact{FactId="remainder",Kind=SourceFactKind.LinearDimension,PageNumber=1,ViewId="front",SourceRegionId="region-front",Unit=MeasurementUnit.Millimeter,
    Fact=new(){Status=FactStatus.Derived,Rationale="total-segment",SourceIds=["total","segment"]},Derivation=new(){Formula="total - segment",Coefficients=new SortedDictionary<string,double>{{"total",1},{"segment",-1}}}};
var sourceFacts0=new SourceFactsDocument{RevisionId="pending",RevisionRationale="fixture",DocumentId="source-facts",SourceSha256=evalHash,ProducerName="regression",ProducerVersion="1",ConfigurationHash=evalHash,CreatedAt=DateTimeOffset.UtcNow,
    Facts=[totalFact,segmentFact,derivedFact]};
var recomputed=SourceFactRevisions.Recompute(sourceFacts0);
Check(recomputed.IsValid&&recomputed.Document.Facts.Single(f=>f.FactId=="remainder").NumericValue==70,"T02 derived source fact recomputes from independent inputs");
var revision=SourceFactRevisions.CreateRevision(recomputed.Document,[segmentFact with{NumericValue=40,Candidates=[segmentFact.Candidates[0] with{NumericValue=40}]}],"source reread changed segment");
Check(revision.IsValid&&revision.Document.ParentRevisionId==recomputed.Document.RevisionId&&revision.Document.RevisionId!=recomputed.Document.RevisionId&&revision.Document.Facts.Single(f=>f.FactId=="remainder").NumericValue==60,"T02 source revision invalidates and recomputes derivation");
var unknownDerived=SourceFactRevisions.Recompute(sourceFacts0 with{Facts=[totalFact,segmentFact with{Fact=new(){Status=FactStatus.Unknown}},derivedFact]});
Check(unknownDerived.Document.Facts.Single(f=>f.FactId=="remainder").Fact.Status==FactStatus.Unknown,"T02 unresolved derivation input stays unknown instead of guessed");
var duplicateFacts=SourceFactRevisions.Recompute(sourceFacts0 with{Facts=[totalFact,totalFact]});
Check(!duplicateFacts.IsValid&&duplicateFacts.Issues.Any(i=>i.Code=="SRC_DUPLICATE_FACT")&&duplicateFacts.Document.Facts.Count==2,
    "T02 duplicate fact IDs return structured diagnostics instead of throwing");

var art=new ArtifactRecord{ArtifactId="page-image",Kind=ArtifactKind.SourcePage,Uri="fixture://page",MediaType="image/png",Sha256=evalHash,PageNumber=1};
var frame=new CoordinateFrame{FrameId="page-pixel",Space=CoordinateSpace.NormalizedPixel,Unit=MeasurementUnit.Pixel,ArtifactId=art.ArtifactId,OriginDescription="top-left",AxisLabels=["x","y"]};
var region=new SourceRegion{RegionId="region-front",ArtifactId=art.ArtifactId,PageNumber=1,Polygon=[new(){CoordinateFrameId=frame.FrameId,X=0,Y=0},new(){CoordinateFrameId=frame.FrameId,X=1000,Y=0},new(){CoordinateFrameId=frame.FrameId,X=1000,Y=500},new(){CoordinateFrameId=frame.FrameId,X=0,Y=500}]};
var obsDoc=new DrawingObservationDocument{DocumentId="obs",SourceSha256=evalHash,ProducerName="regression",ProducerVersion="1",ConfigurationHash=evalHash,CreatedAt=DateTimeOffset.UtcNow,ArtifactManifest=[art],CoordinateFrames=[frame],SourceRegions=[region],ViewRegions=[new(){ViewRegionId="front",SourceRegionId=region.RegionId,ViewTypeHint=DrawingViewType.Front}]};
var manifest=new DrawingSourceManifest{DocumentId="manifest",SourceSha256=evalHash,ProducerName="regression",ProducerVersion="1",ConfigurationHash=evalHash,CreatedAt=DateTimeOffset.UtcNow,SourcePath=source,FileName=Path.GetFileName(source),MediaType="image/png",Pages=[new(){PageId="p1",PageNumber=1,ArtifactId=art.ArtifactId,Sha256=evalHash,PixelWidth=1000,PixelHeight=500,PhysicalWidth=254,PhysicalHeight=127,PhysicalUnit=MeasurementUnit.Millimeter,Dpi=100,Representation=SourceRepresentation.Raster}]};
var viewMap=DrawingViewMapBuilder.Build(new(){SourceManifest=manifest,Pages=[obsDoc],
    ModelAnchors=[new(){ViewId="front",EvidenceId="origin",Basis="fixture origin"}],
    ScaleCalibrations=[new(){ViewId="front",ModelMillimetersPerSheetMillimeter=1,EvidenceId="scale-1-1",Basis="explicit 1:1 scale"}],ConfigurationHash=evalHash});
var sourceToMm=viewMap.Transforms.Single(t=>t.TransformId=="front-source-to-view-mm");
var mapped25=DrawingTransformMath.Transform2(sourceToMm.ForwardMatrix,100,0);
Check(Math.Abs(mapped25.X-25.4)<1e-9,"T03 pixel-to-view chain preserves physical millimeters");
Check(DrawingTransformMath.Validate(viewMap).Count==0,"T03 generated view map round-trips and stays right-handed");
var halfScaleMap=DrawingViewMapBuilder.Build(new(){SourceManifest=manifest,Pages=[obsDoc],
    ModelAnchors=[new(){ViewId="front",EvidenceId="origin",Basis="fixture origin"}],
    ScaleCalibrations=[new(){ViewId="front",ModelMillimetersPerSheetMillimeter=2,EvidenceId="scale-1-2",Basis="explicit 1:2 drawing scale"}],ConfigurationHash=evalHash});
var halfScaleTransform=halfScaleMap.Transforms.Single(t=>t.TransformId=="front-source-to-view-mm");
var mappedHalfScale=DrawingTransformMath.Transform2(halfScaleTransform.ForwardMatrix,100,0);
Check(Math.Abs(mappedHalfScale.X-50.8)<1e-9,"T03 1:2 drawing scale converts sheet millimeters to doubled model millimeters");
var localRegion=region with{Polygon=[new(){CoordinateFrameId=frame.FrameId,X=200,Y=100},new(){CoordinateFrameId=frame.FrameId,X=800,Y=100},new(){CoordinateFrameId=frame.FrameId,X=800,Y=400},new(){CoordinateFrameId=frame.FrameId,X=200,Y=400}]};
var localObs=obsDoc with{SourceRegions=[localRegion],ViewRegions=[new(){ViewRegionId="front",SourceRegionId=localRegion.RegionId,ViewTypeHint=DrawingViewType.Front}]};
var localMap=DrawingViewMapBuilder.Build(new(){SourceManifest=manifest,Pages=[localObs],
    ModelAnchors=[new(){ViewId="front",EvidenceId="origin",Basis="fixture origin"}],
    ScaleCalibrations=[new(){ViewId="front",ModelMillimetersPerSheetMillimeter=1,EvidenceId="scale-1-1",Basis="explicit 1:1 scale"}],ConfigurationHash=evalHash});
var localTransform=localMap.Transforms.Single(t=>t.TransformId=="front-source-to-view-mm");
var localA=DrawingTransformMath.Transform2(localTransform.ForwardMatrix,200,100);
var localB=DrawingTransformMath.Transform2(localTransform.ForwardMatrix,300,100);
Check(Math.Abs((localB.X-localA.X)-25.4)<1e-9,"T03 local view crop cannot redefine full-page physical calibration");
var noScaleMap=DrawingViewMapBuilder.Build(new(){SourceManifest=manifest,Pages=[obsDoc],ModelAnchors=[new(){ViewId="front",EvidenceId="origin",Basis="fixture origin"}],ConfigurationHash=evalHash});
Check(noScaleMap.Views.Single().Status==ViewMapStatus.Unverifiable&&Throws<InvalidOperationException>(()=>DrawingPlanContextAdapter.ApplyViewMap(context,noScaleMap)),
    "T03 missing drawing scale remains unverifiable and cannot adapt into typed plan metadata");
var scale254=new IReadOnlyList<double>[] {new[]{25.4,0d,0d,0d},new[]{0d,25.4,0d,0d},new[]{0d,0d,25.4,0d},new[]{0d,0d,0d,1d}};
var mirrorMatrix=new IReadOnlyList<double>[] {new[]{-1d,0d,0d,0d},new[]{0d,1d,0d,0d},new[]{0d,0d,1d,0d},new[]{0d,0d,0d,1d}};
Check(!DrawingTransformMath.IsRigidMillimeterTransform(scale254)&&!DrawingTransformMath.IsRigidMillimeterTransform(mirrorMatrix),"T03 free 25.4 scale and reflection cannot masquerade as model alignment");
Check(ProjectionConventionResolver.Resolve([new(){EvidenceId="symbol",Convention=ProjectionConvention.ThirdAngle,Basis="explicit symbol"}]).Convention==ProjectionConvention.ThirdAngle,"T03 explicit third-angle evidence overrides default");
Check(ProjectionConventionResolver.Resolve([new(){EvidenceId="symbol",Convention=ProjectionConvention.ThirdAngle},new(){EvidenceId="label",Convention=ProjectionConvention.FirstAngle}]).Status==ViewMapStatus.Conflict,"T03 conflicting projection evidence remains blocking conflict");
var adaptedContext=DrawingPlanContextAdapter.ApplyViewMap(context,viewMap);
Check(adaptedContext.Views.Count==1&&adaptedContext.Views[0].Kind==ModelDrawingView.Front&&adaptedContext.Views[0].RegionId=="region-front"&&adaptedContext.ProjectionStatus==DrawingFactStatus.Assumed,"T03 resolved view roles and projection provenance adapt into existing typed-plan context");

var thicknessFact=StatedFact("thickness",SourceFactKind.Thickness,10,MeasurementUnit.Millimeter,"obs-thickness");
var marginFact=StatedFact("margin",SourceFactKind.LinearDimension,10,MeasurementUnit.Millimeter,"obs-margin");
var thicknessSource=SourceFactRevisions.Recompute(sourceFacts0 with{Facts=[thicknessFact]}).Document;
var marginSource=SourceFactRevisions.Recompute(sourceFacts0 with{Facts=[marginFact]}).Document;
var thicknessObservation=new DimensionObservation{DimensionObservationId="dim-thickness",SourceObservationId="obs-thickness",SourceRegionId="region-front",RawLiteral="10",CandidateNumericValue=10,DimensionKind="linear",NearbyGeometryObservationIds=["edge-thickness"]};
var binding=DimensionBindingResolver.Resolve(new(){Fact=thicknessFact,SourceRevisionId=thicknessSource.RevisionId,Observation=thicknessObservation,Targets=[
    new(){TargetId="plate-thickness",Kind=DimensionTargetKind.Face,ViewId="front",ObservationIds=["edge-thickness"],FeatureIds=["plate"],SemanticKinds=["thickness"],OperationId="block",ParameterPath="depth_mm"},
    new(){TargetId="hole-margin",Kind=DimensionTargetKind.Edge,ViewId="front",ObservationIds=["edge-margin"],FeatureIds=["hole"],SemanticKinds=["linear"],OperationId="hole",ParameterPath="feature.offset_mm"}]});
Check(binding.Decision==DimensionBindingDecision.Bound&&binding.Candidates[0].TargetId=="plate-thickness","T04 same numeric value binds by attachment and semantics, not value equality");
var noAttachmentBinding=DimensionBindingResolver.Resolve(new(){Fact=thicknessFact,SourceRevisionId=thicknessSource.RevisionId,Observation=thicknessObservation with{NearbyGeometryObservationIds=[]},Targets=[
    new(){TargetId="semantic-only",Kind=DimensionTargetKind.Face,ViewId="other-view",ObservationIds=[],FeatureIds=["plate"],SemanticKinds=["thickness"],OperationId="wrong",ParameterPath="depth_mm"}]});
Check(noAttachmentBinding.Decision==DimensionBindingDecision.Unresolved,"T04 semantic compatibility without attachment evidence cannot confirm a binding");
var diameterFact=StatedFact("diameter-source",SourceFactKind.Diameter,12,MeasurementUnit.Millimeter,"obs-diameter");
var diameterSource=SourceFactRevisions.Recompute(sourceFacts0 with{Facts=[diameterFact]}).Document;
var diameterRadiusConflict=DimensionBindingResolver.Resolve(new(){Fact=diameterFact,SourceRevisionId=diameterSource.RevisionId,Observation=new(){DimensionObservationId="dim-diameter",SourceObservationId="obs-diameter",SourceRegionId="region-front",RawLiteral="⌀12",CandidateNumericValue=12,DimensionKind="diameter",NearbyGeometryObservationIds=["obs-diameter"]},Targets=[
    new(){TargetId="radius-target",Kind=DimensionTargetKind.Circle,ViewId="front",ObservationIds=["obs-diameter"],FeatureIds=["hole"],SemanticKinds=["radius"],OperationId="hole",ParameterPath="feature.radius_mm"}]});
Check(diameterRadiusConflict.Decision!=DimensionBindingDecision.Bound,"T04 explicit diameter-versus-radius semantic contradiction blocks direct binding");
var ambiguousBinding=DimensionBindingResolver.Resolve(new(){Fact=marginFact,SourceRevisionId=marginSource.RevisionId,Observation=new(){DimensionObservationId="dim-margin",SourceObservationId="obs-margin",SourceRegionId="region-front",RawLiteral="10",CandidateNumericValue=10,DimensionKind="linear",NearbyGeometryObservationIds=["edge-a","edge-b"]},Targets=[
    new(){TargetId="edge-a",Kind=DimensionTargetKind.Edge,ViewId="front",ObservationIds=["edge-a"],OperationId="a",ParameterPath="depth_mm"},
    new(){TargetId="edge-b",Kind=DimensionTargetKind.Edge,ViewId="front",ObservationIds=["edge-b"],OperationId="b",ParameterPath="depth_mm"}]});
Check(ambiguousBinding.Decision==DimensionBindingDecision.Conflict,"T04 equally supported same-value targets remain ambiguous");
var constraintFacts=sourceFacts0 with{Facts=[StatedFact("a",SourceFactKind.LinearDimension,30,MeasurementUnit.Millimeter,"oa"),StatedFact("b",SourceFactKind.LinearDimension,40,MeasurementUnit.Millimeter,"ob")]};
var conflict=DimensionConstraintSolver.Evaluate(constraintFacts,[new(){ConstraintId="chain",Kind=DimensionConstraintKind.Sum,FactIds=["a","b"],ExpectedValue=100}]);
Check(!conflict.Passed&&conflict.Issues[0].Code=="DIM_CHAIN_CONFLICT","T04 inconsistent dimension chain reports conflict without averaging error");
var adapted=SourceFactPlanAdapter.ToLegacyDimensions(thicknessSource,[binding]);
Check(adapted.Count==1&&adapted[0].OperationId=="block"&&adapted[0].ParameterPath=="depth_mm"&&adapted[0].Value==10,"T04 confirmed source binding compiles to existing typed-plan dimension contract");
var movedThickness=thicknessFact with{NumericValue=12,ViewId="other-view",EvidenceIds=["obs-other"],Candidates=[thicknessFact.Candidates[0] with{NumericValue=12,EvidenceIds=["obs-other"]}],Fact=new(){Status=FactStatus.Stated,SourceIds=["obs-other"],Rationale="source revision moved requirement"}};
var movedRevision=SourceFactRevisions.CreateRevision(thicknessSource,[movedThickness],"dimension reread belongs to another object").Document;
Check(Throws<InvalidOperationException>(()=>SourceFactPlanAdapter.ToLegacyDimensions(movedRevision,[binding])),"T04 source revision change invalidates stale object/parameter binding");

ProjectionPrimitive Line(string id,double x1,double y1,double x2,double y2,string req)=>new(){Id=id,Kind=ProjectionPrimitiveKind.Line,Start=new(x1,y1),End=new(x2,y2),RequirementId=req};
ProjectionPrimitive Circle(string id,double x,double y,double radius,string req)=>new(){Id=id,Kind=ProjectionPrimitiveKind.Circle,Center=new(x,y),RadiusMm=radius,RequirementId=req};
var projectionSource=new ProjectionSnapshot{ViewId="front",CoordinateFrameId="front-mm",SourceSha256=evalHash,Primitives=[
    Line("top",-40,25,40,25,"outline"),Line("bottom",-40,-25,40,-25,"outline"),Line("left",-40,-25,-40,25,"outline"),Line("right",40,-25,40,25,"outline"),
    Circle("hole-asymmetric",20,10,3,"hole-1")]};
ProjectionSnapshot ModelProjection(params ProjectionPrimitive[] primitives)=>new(){ViewId="front",CoordinateFrameId="front-mm",SourceSha256=evalHash,NativeModelPath="fixture.SLDPRT",NativeModelSha256=evalHash,NativeModelReopened=true,Primitives=primitives};
var correctProjection=ModelProjection(projectionSource.Primitives.Select(p=>p with{Id="model-"+p.Id,RequirementId=null}).ToArray());
Check(ProjectionVerifier.Compare(projectionSource,correctProjection,new(){MirrorAxisXmm=0}).Passed,"T05 correct asymmetric plate line/circle projection passes in fixed millimeters");
var hiddenRequiredSource=projectionSource with{Primitives=[Line("hidden-required",0,0,10,0,"hidden-req") with{LineStyle="hidden"}]};
var hiddenRequiredModel=ModelProjection(Line("model-hidden",0,0,10,0,"unused") with{RequirementId=null,LineStyle="hidden"});
var hiddenRequiredReport=ProjectionVerifier.Compare(hiddenRequiredSource,hiddenRequiredModel);
Check(!hiddenRequiredReport.Passed&&hiddenRequiredReport.Requirements.Single().Status==ProjectionRequirementStatus.Unverifiable&&hiddenRequiredReport.CheckedRequiredPrimitiveCount==0,
    "T05 required hidden geometry outside visible-only scope is unverifiable, never passed");
var emptySource=projectionSource with{Primitives=[]};
var emptyModel=ModelProjection();
var emptyReport=ProjectionVerifier.Compare(emptySource,emptyModel);
Check(!emptyReport.Passed&&emptyReport.RequiredPrimitiveCount==0&&emptyReport.Differences.Any(d=>d.Kind==ProjectionDifferenceKind.Unsupported),
    "T05 empty required projection scope cannot pass");
Check(Throws<ArgumentException>(()=>ProjectionVerifier.Compare(projectionSource,correctProjection with{SourceSha256=new string('B',64)})),
    "T05 projection snapshots from different source drawings cannot be paired");
Check(Throws<ArgumentOutOfRangeException>(()=>ProjectionVerifier.Compare(projectionSource,correctProjection,new(){PositionToleranceMm=double.PositiveInfinity})),
    "T05 non-finite comparison tolerances are rejected");
var incompleteCapture=correctProjection with{CaptureComplete=false,CaptureLimitations=["unsupported spline edge"]};
var incompleteReport=ProjectionVerifier.Compare(projectionSource,incompleteCapture);
Check(!incompleteReport.Passed&&incompleteReport.CaptureLimitations.Count==1&&incompleteReport.Differences.Any(d=>d.Kind==ProjectionDifferenceKind.Unsupported),
    "T05 incomplete model projection capture cannot produce complete acceptance");
var extraGeometry=ModelProjection(correctProjection.Primitives.Append(Circle("unexpected",-10,0,2,"unused") with{RequirementId=null}).ToArray());
Check(!ProjectionVerifier.Compare(projectionSource,extraGeometry,new(){MirrorAxisXmm=0}).Passed,"T05 unmatched extra projected geometry prevents complete scope acceptance");
var noHole=ModelProjection(correctProjection.Primitives.Where(p=>p.Kind!=ProjectionPrimitiveKind.Circle).ToArray());
var noHoleReport=ProjectionVerifier.Compare(projectionSource,noHole,new(){MirrorAxisXmm=0});
Check(!noHoleReport.Passed&&noHoleReport.Differences.Any(d=>d.Kind==ProjectionDifferenceKind.Missing&&d.RequirementId=="hole-1"),"T05 omitted hole is localized as missing required projection");
var shifted=ModelProjection(correctProjection.Primitives.Select(p=>p.Kind==ProjectionPrimitiveKind.Circle?p with{Center=new(22,10)}:p).ToArray());
var shiftedReport=ProjectionVerifier.Compare(projectionSource,shifted,new(){PositionToleranceMm=.15,MirrorAxisXmm=0});
Check(!shiftedReport.Passed&&shiftedReport.Differences.Any(d=>d.Kind==ProjectionDifferenceKind.PositionMismatch&&d.RequirementId=="hole-1"),"T05 shifted hole beyond frozen threshold fails position check");
var mirrored=ModelProjection(correctProjection.Primitives.Select(p=>p.Kind==ProjectionPrimitiveKind.Circle?p with{Center=new(-20,10)}:p).ToArray());
var mirroredReport=ProjectionVerifier.Compare(projectionSource,mirrored,new(){MirrorAxisXmm=0});
Check(!mirroredReport.Passed&&mirroredReport.MirrorSignatureDetected&&mirroredReport.Differences.Any(d=>d.Kind==ProjectionDifferenceKind.MirrorDetected),"T05 mirrored asymmetric model is diagnosed but never accepted by reflection alignment");

Save("results.json",new{success=true,checks=results.Count,passed=results,scope="Injected detector/plan omissions and typed contracts; not a blind drawing-reconstruction benchmark.",source_sha256=originalHash});
Console.WriteLine("REPORT "+Path.Combine(root,"results.json"));

static class Fixture
{
    public static readonly (string Id,string Text,double X,double Y)[] Labels=[("width","80",750,110),("height","50",80,530),("depth","10",350,1030),("holes","4×⌀6",1430,260)];
    public static void Draw(string path)
    {
        var visual=new DrawingVisual();
        using(var dc=visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White,null,new Rect(0,0,1800,1250));
            var pen=new Pen(Brushes.Black,3);
            dc.DrawRectangle(null,pen,new Rect(200,180,1200,750));
            foreach(var x in new[]{425d,1175d}) foreach(var y in new[]{330d,780d}) dc.DrawEllipse(null,pen,new Point(x,y),45,45);
            foreach(var l in Labels) dc.DrawText(new FormattedText(l.Text,CultureInfo.InvariantCulture,FlowDirection.LeftToRight,new Typeface("Segoe UI"),40,Brushes.Black,1),new Point(l.X,l.Y));
            dc.DrawText(new FormattedText("Thickness",CultureInfo.InvariantCulture,FlowDirection.LeftToRight,new Typeface("Segoe UI"),30,Brushes.Black,1),new Point(200,1037));
            dc.DrawText(new FormattedText("Hole centers (mm): (-25,-15), (-25,15), (25,-15), (25,15)",CultureInfo.InvariantCulture,FlowDirection.LeftToRight,new Typeface("Segoe UI"),25,Brushes.Black,1),new Point(200,1140));
        }
        var bitmap=new RenderTargetBitmap(1800,1250,96,96,PixelFormats.Pbgra32);bitmap.Render(visual);
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var f=new FileStream(path,FileMode.CreateNew);encoder.Save(f);
    }
    public static GenericModelDraft Draft(string source,string inventoryPath,string inventoryHash,DrawingOmissionInventory inventory)
    {
        var factSpecs=new[]{("width","base","primitives.0.width_mm",80d,"width"),("height","base","primitives.0.height_mm",50d,"height"),("depth","block","depth_mm",10d,"depth"),("diameter","holes","feature.diameter_mm",6d,"holes"),("count","holes","feature.hole_center_count",4d,"holes")};
        var facts=factSpecs.Select(s=>new DrawingDimensionFact{Id=s.Item1,OperationId=s.Item2,ParameterPath=s.Item3,Value=s.Item4,Unit=s.Item1=="count"?DrawingValueUnit.Unitless:DrawingValueUnit.Millimeter,
            SourceLiteral=Labels.Single(l=>l.Id==s.Item5).Text,ViewIds=["front"],ObservationIds=["label-"+s.Item5]}).ToArray();
        var features=new[]{new DrawingFeatureRequirement{Id="plate",SourceLiteral="80 x 50, thickness 10 mm",ViewIds=["front"],OperationIds=["base","block"],
                CriticalParameters=facts.Take(3).Select(f=>new CriticalParameterBinding{OperationId=f.OperationId,ParameterPath=f.ParameterPath,DimensionId=f.Id}).ToArray(),VerificationCheckIds=["bounds"]},
            new DrawingFeatureRequirement{Id="holes",SourceLiteral="4×⌀6; coordinates explicitly listed on the source",ViewIds=["front"],OperationIds=["holes"],
                CriticalParameters=facts.Skip(3).Select(f=>new CriticalParameterBinding{OperationId=f.OperationId,ParameterPath=f.ParameterPath,DimensionId=f.Id}).ToArray(),VerificationCheckIds=["hole-check"]}};
        var decisions=inventory.Candidates.Select(c=>new DrawingCandidateReview{CandidateId=c.Id,Disposition=DrawingCandidateDisposition.Feature,
            FeatureIds=c.Literal=="4×⌀6"?["holes"]:c.Kind==DrawingCandidateKind.ResidualInk?["plate","holes"]:["plate"],
            DimensionIds=facts.Where(f=>f.ObservationIds.Any(c.ObservationIds.Contains)).Select(f=>f.Id).ToArray(),
            Rationale=c.Kind==DrawingCandidateKind.ResidualInk?"Independent raw-crop inspection: four interior holes, thickness caption and explicit hole coordinates belong to the declared plate and hole group.":"Known source fixture: annotation/contour is mapped to its source feature."}).ToArray();
        var centers=new[]{new ProfilePoint(-25,-15),new ProfilePoint(-25,15),new ProfilePoint(25,-15),new ProfilePoint(25,15)};
        return new(){Name="omission_four_holes",SourceText="Source-bound regression fixture; not blind reconstruction.",DrawingContext=new(){SourcePath=source,Views=[new(){Id="front",Kind=ModelDrawingView.Front}],Dimensions=facts,Features=features,
            OmissionReview=new(){InventoryPath=inventoryPath,InventorySha256=inventoryHash,Candidates=decisions,Regions=inventory.Regions.Select(r=>new DrawingRegionReview{RegionId=r.Id,State=DrawingReviewState.Reviewed,FeatureIds=["plate","holes"],Findings="Known source fixture: rectangle, four holes, labeled sizes and explicitly listed center coordinates; no unresolved additional geometry."}).ToArray()}},
            Operations=[new(){Type=GenericOperationKind.ProfileSketch,Id="base",Name="base",Plane=ReferencePlane.Front,Primitives=[new(){Type=GenericPrimitiveKind.CenteredRectangle,CenterXmm=0,CenterYmm=0,WidthMm=80,HeightMm=50}]},
                new(){Type=GenericOperationKind.ExtrudeBoss,Id="block",Name="block",SketchId="base",DepthMm=10,EndCondition=ExtrudeEndCondition.Blind},
                new(){Type=GenericOperationKind.NativeFeature,Id="holes",Name="holes",Feature=new(){Kind=NativeFeatureKind.Hole,DiameterMm=6,HoleCenters=centers,Frame=new(){OriginMm=new(0,0,10),XDirection=new(1,0,0),Normal=new(0,0,-1)}}}],
            Verification=new(){Bounds=[new(){Id="bounds",SourceLiteral="80 x 50 x 10",SourceDimensionIds=["width","height","depth"],SizeMm=new(80,50,10)}],
                CylinderGroups=[new(){Id="hole-check",SourceLiteral="Four diameter-6 complete cylindrical walls, z=0..10",SourceDimensionIds=["diameter","count"],DiameterMm=6,LengthMm=10,AxisStartsMm=centers.Select(p=>new Vector3(p.Xmm,p.Ymm,0)).ToArray()}],
                Bindings=[new("width","bounds","size_mm.x"),new("height","bounds","size_mm.y"),new("depth","bounds","size_mm.z"),new("diameter","hole-check","diameter_mm"),new("count","hole-check","expected_count")]}};
    }
}

sealed class FixtureText:ITextObservationProvider
{
    public Task<ProviderObservationBatch> ObserveAsync(RasterPreprocessResult page,ProviderPageContext context,CancellationToken cancellationToken=default)=>Task.FromResult(new ProviderObservationBatch
    {
        PageNumber=context.PageNumber,Modality=ObservationModality.RasterText,
        Observations=Fixture.Labels.Select(l=>new ObservationEntity{ObservationId="label-"+l.Id,Kind=ObservationKind.Text,RawLiteral=l.Text,Confidence=1,
            SourceRegionId=$"page-{context.PageNumber:D4}-raster-region",Geometry=[new(){CoordinateFrameId=page.NormalizedPage.CoordinateFrameId,X=l.X,Y=l.Y},new(){CoordinateFrameId=page.NormalizedPage.CoordinateFrameId,X=l.X+160,Y=l.Y+55}],
            CandidateProperties=new Dictionary<string,string>{{"provider","injected-source-labels"}}}).ToArray()
    });
}
sealed class OuterContourOnly:IPrimitiveObservationProvider
{
    public Task<ProviderObservationBatch> ObserveAsync(RasterPreprocessResult page,ProviderPageContext context,CancellationToken cancellationToken=default)=>Task.FromResult(new ProviderObservationBatch
    {
        PageNumber=context.PageNumber,Modality=ObservationModality.RasterPrimitive,
        Observations=[new(){ObservationId="outer-contour",Kind=ObservationKind.VisibleLine,SourceRegionId=$"page-{context.PageNumber:D4}-raster-region",
            Geometry=new[]{(200d,180d),(1400d,180d),(1400d,930d),(200d,930d),(200d,180d)}.Select(p=>new LocatedPoint2{CoordinateFrameId=page.NormalizedPage.CoordinateFrameId,X=p.Item1,Y=p.Item2}).ToArray(),
            CandidateProperties=new Dictionary<string,string>{{"provider","injected-outer-contour-only"}}}]
    });
}
