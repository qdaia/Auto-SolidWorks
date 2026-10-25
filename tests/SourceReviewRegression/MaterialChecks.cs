using System.Text.Json;
using CadModeling.Core;
using CadModeling.Ir;
static class MaterialChecks
{
    public static void Run(string output,Action<bool,string> check)
    {
        MaterialRayHit Hit(int b,double x,bool enter)=>new(b,x,enter,!enter);
        MaterialIntervalResult Eval(MaterialRayHit[] h,DrawingMaterialState state=DrawingMaterialState.Material)=>MaterialIntervalVerification.Evaluate(h,2,2,8,.001,state);
        check(Eval([Hit(0,1,true),Hit(0,9,false)]).Passed,"full material interval passes");
        check(Eval([],DrawingMaterialState.Void).Passed,"no crossings on valid bodies proves sampled void");
        check(!Eval([Hit(0,1,true),Hit(0,3,false),Hit(0,3.01,true),Hit(0,9,false)]).Passed,"thin internal cavity is caught between endpoints");
        check(!Eval([Hit(0,4,true),Hit(0,4.01,false)],DrawingMaterialState.Void).Passed,"thin extra material in expected void is caught");
        check(Eval([Hit(0,1,true),Hit(0,5,false),Hit(1,4,true),Hit(1,9,false)]).Passed,"overlapping bodies are unioned without double counting");
        check(!Eval([Hit(0,1,true),Hit(0,4,false),Hit(1,5,true),Hit(1,9,false)]).Passed,"disjoint bodies retain missing material gap");
        check(!Eval([Hit(0,1,true)]).Complete,"missing exit is unverifiable");
        check(!Eval([Hit(0,9,false)]).Complete,"first exit rejects inside-origin or incomplete ray");
        check(!Eval([new(0,4,true,true)]).Complete,"ambiguous tangency is unverifiable");
        check(!Eval([Hit(0,double.NaN,true)]).Complete,"nonfinite native hit is unverifiable");
        check(Eval([Hit(0,1,true),Hit(0,1,true),Hit(0,9,false)]).Passed,"duplicate same-face crossings do not double count");
        SourceMaterialSegment Segment(string id,double y,double x1=-39,double x2=39,double z=15)=>new(){Id=id,StartMm=new(x1,y,z),EndMm=new(x2,y,z),SourceExplanation="Independent controlled source: centered 80 by 50 plate with 20 thickness; not inferred from model"};
        var regions=new[]{new DrawingMaterialRegion{Id="upper-layer",MaterialState=DrawingMaterialState.Material,Description="Plate upper layer Z=15",Realization="20 mm extrusion",ProbeSegments=[Segment("a",-15),Segment("b",0),Segment("c",15)]},
            new DrawingMaterialRegion{Id="outside-right",MaterialState=DrawingMaterialState.Void,Description="Beyond X=40 remains void",Realization="80 mm width",ProbeSegments=[Segment("d",-15,41,49),Segment("e",0,41,49),Segment("f",15,41,49)]}};
        var context=new DrawingPlanContext{SourcePath="controlled-independent-plate",WorkflowRevision=SourceWorkflow.Revision,ShapeDecomposition=new(){State=DrawingReviewState.Reviewed,Features=[new(){FeatureId="plate",Kind=DrawingShapeKind.Prismatic,TopologyReview="Full 80x50x20 plate with no extensions",Regions=regions}]}};
        var generated=SourceMaterialCoverage.Generate(context);var spec=new ModelVerificationSpec{MaterialRegions=generated.Checks};
        check(generated.Diagnostics.Count==0&&SourceMaterialCoverage.Validate(spec,context).Count==0,"source material requirements generated independently of operations");
        check(SourceMaterialCoverage.Validate(spec with{MaterialRegions=generated.Checks.Take(1).ToArray()},context).Any(d=>d.Code=="SOURCE_MATERIAL_COVERAGE"),"omitted source void region rejected");
        check(SourceMaterialCoverage.Validate(spec with{MaterialRegions=generated.Checks.Select(c=>c with{ExpectedState=DrawingMaterialState.Void}).ToArray()},context).Any(d=>d.Code=="SOURCE_MATERIAL_COVERAGE"),"material expectation cannot be changed to fit model");
        var repeated=generated.Checks[0] with{Segments=Enumerable.Range(0,3).Select(i=>regions[0].ProbeSegments[0] with{Id=i.ToString()}).ToArray()};
        check(SourceMaterialCoverage.Validate(new(){MaterialRegions=[repeated]},null).Any(d=>d.Code=="SOURCE_MATERIAL_SPEC"),"renamed duplicate segments cannot fabricate coverage");
        check(!ModelVerification.Evaluate(spec,[],new Dictionary<string,double>(),null).Passed,"material checks with absent native result fail closed");
        File.WriteAllText(Path.Combine(output,"material-source.json"),JsonSerializer.Serialize(context,ModelingIrJson.Options));
        File.WriteAllText(Path.Combine(output,"material-verification.json"),JsonSerializer.Serialize(spec,ModelingIrJson.Options));
        DrawingAngularSector Sector(double a,double b,string r)=>new(){StartDegrees=a,EndDegrees=b,RegionId=r};
        DrawingAxialLayer Layer(string id,double a,double b,params DrawingAngularSector[] s)=>new(){Id=id,MinimumMm=a,MaximumMm=b,BoundaryEvidence="Controlled independent stepped annulus",Sectors=s};
        var axial=context with{ShapeDecomposition=new(){State=DrawingReviewState.Reviewed,Features=[new(){FeatureId="stepped-ring",Kind=DrawingShapeKind.AxialAngular,TopologyReview="Full base, positive-Y half wall and short negative quadrant; upper negative half remains void",Regions=new[]{"base","half","back","opening"}.Select(id=>new DrawingMaterialRegion{Id=id,MaterialState=id=="opening"?DrawingMaterialState.Void:DrawingMaterialState.Material,Description=id+" independent source region",Realization="Controlled annulus"}).ToArray(),AxialMaps=[new(){Id="band",OriginMm=new(0,0,0),AxisDirection=new(0,0,1),ZeroAngleDirection=new(1,0,0),InnerRadiusMm=10,OuterRadiusMm=20,MinimumMm=0,MaximumMm=40,DatumEvidence="Source Z from bottom, theta from X toward Y",Layers=[Layer("base",0,20,Sector(0,360,"base")),Layer("middle",20,30,Sector(0,180,"half"),Sector(180,270,"back"),Sector(270,360,"opening")),Layer("upper",30,40,Sector(0,180,"half"),Sector(180,360,"opening"))]}]}]}};
        var axialGenerated=SourceMaterialCoverage.Generate(axial);var axialSpec=new ModelVerificationSpec{MaterialRegions=axialGenerated.Checks};
        check(axialGenerated.Diagnostics.Count==0&&axialGenerated.Checks.Sum(c=>c.Segments.Count)==54,"all axial layers and material/void sectors receive nine rays each");
        check(axialGenerated.Checks.Single(c=>c.RegionId=="back").Segments.All(s=>s.StartMm.X<0&&s.StartMm.Y<0&&s.StartMm.Z>20&&s.StartMm.Z<30),"source frame generates local back quadrant at its exact height range");
        check(SourceMaterialCoverage.Validate(axialSpec,axial).Count==0,"automatic axial segment identities validate without model input");
        File.WriteAllText(Path.Combine(output,"axial-source.json"),JsonSerializer.Serialize(axial,ModelingIrJson.Options));
        File.WriteAllText(Path.Combine(output,"axial-verification.json"),JsonSerializer.Serialize(axialSpec,ModelingIrJson.Options));
    }
}
