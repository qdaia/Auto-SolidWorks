using System.Text.Json;
using CadModeling.Core;
using CadModeling.Ir;

internal static class CurvatureCoreChecks
{
    internal static int Run(string output)
    {
        Directory.CreateDirectory(output);int passed=0;
        var checks=new List<string>();
        void Check(bool condition,string name){if(!condition)throw new Exception("FAIL: "+name);passed++;checks.Add(name);Console.WriteLine("PASS: "+name);}
        void Reject(Action action,string name){try{action();}catch(ArgumentException){Check(true,name);return;}throw new Exception("FAIL accepted: "+name);}
        var zero=new Vector3(0,0,0);var x=new Vector3(1,0,0);var y=new Vector3(0,1,0);var z=new Vector3(0,0,1);
        var plane=new SurfaceDerivatives(x,y,zero,zero,zero,z);
        var normalXPlane=new SurfaceDerivatives(y,z,zero,zero,zero,x);
        var cylinder=new SurfaceDerivatives(new(0,.01,0),z,new(-.01,0,0),zero,zero,x);
        double Difference(SurfaceDerivatives a,SurfaceDerivatives b,Vector3? tangent=null)=>SurfaceDifferentialGeometry.MaximumNormalCurvatureDifferencePerMm(a,b,tangent??x);
        bool Close(double a,double b)=>Math.Abs(a-b)<1e-10;
        Check(Difference(plane,plane)==0,"coplanar surfaces have zero normal curvature difference");
        Check(Close(Difference(normalXPlane,cylinder,z),.1),"10 mm cylinder vs plane detects 0.1 inverse-mm cross-seam curvature");
        Check(Difference(cylinder,cylinder,z)==0,"same cylinder geometry agrees");
        Check(Difference(cylinder,cylinder with{Normal=new(-1,0,0)},z)==0,"opposite underlying surface orientation agrees");
        var scaled=cylinder with{Du=new(0,.37,0),Duu=new(-13.69,0,0),Dv=new(0,0,.004)};
        Check(Close(Difference(cylinder,scaled,z),0),"curvature invariant to independent UV scales");
        var mixed=plane with{Duv=new(0,0,60)};
        Check(Close(Difference(plane,mixed),.06),"mixed curvature detected even when both axial curvatures are zero");
        var bent=plane with{Duu=new(0,0,100),Duv=new(0,0,30),Dvv=new(0,0,-40)};
        var sheared=bent with{Dv=new(2,1,0),Duv=new(0,0,230),Dvv=new(0,0,480)};
        Check(Close(Difference(bent,sheared),0),"second fundamental form invariant to skew UV basis");
        var rotated=bent with{Du=y,Dv=new(-1,0,0),Duu=bent.Dvv,Duv=new(0,0,-30),Dvv=bent.Duu};
        Check(Close(Difference(bent,rotated),0),"second fundamental form invariant to rotated UV basis");
        Check(Close(Difference(plane,bent,x),Difference(plane,bent,new(1,2,0))),"spectral maximum independent of seam basis");
        var angle=.001;var cos=Math.Cos(angle);var sin=Math.Sin(angle);
        Vector3 Rotate(Vector3 v)=>new(v.X,v.Y*cos-v.Z*sin,v.Y*sin+v.Z*cos);
        var tilted=bent with{Du=Rotate(bent.Du),Dv=Rotate(bent.Dv),Duu=Rotate(bent.Duu),Duv=Rotate(bent.Duv),Dvv=Rotate(bent.Dvv),Normal=Rotate(bent.Normal)};
        Check(Close(Difference(bent,tilted),0),"normal-aligning tangent-plane rotation preserves full curvature form");
        Reject(()=>Difference(plane,plane with{Dv=x}),"singular surface metric rejected");
        Reject(()=>Difference(plane,plane with{Du=zero}),"unsupported zero derivatives rejected");
        Reject(()=>Difference(plane,plane with{Normal=x}),"inconsistent native normal rejected");
        Reject(()=>Difference(plane,plane with{Duv=new(double.NaN,0,0)}),"nonfinite derivative rejected");
        Reject(()=>Difference(plane,plane,z),"normal-only seam tangent rejected");
        Reject(()=>SurfaceDifferentialGeometry.FromNativeEvaluation(new double[29]),"incomplete native derivative array rejected");
        var encoded=new double[30];encoded[3]=1;encoded[10]=1;encoded[12+2]=60;encoded[29]=1;
        Check(Close(Difference(plane,SurfaceDifferentialGeometry.FromNativeEvaluation(encoded)),.06),"SolidWorks derivative slots decoded including mixed derivative and final normal");
        encoded[5]=double.NaN;
        Reject(()=>SurfaceDifferentialGeometry.FromNativeEvaluation(encoded),"nonfinite native derivative array rejected");
        var requirement=new SurfaceContinuityCheck{Id="seam",SourceLiteral="Requested G2 continuity",Edge=new(){Kind=EntityKind.Edge},RequireCurvatureContinuity=true};
        ModelVerificationResult Evaluate(SurfaceContinuityCheck c,ShapeMeasurement m)=>ModelVerification.Evaluate(new(){SurfaceContinuity=[c]},[],new Dictionary<string,double>(),null,
            shapeMeasurements:new Dictionary<string,ShapeMeasurement>{{"seam",m}});
        var measurement=new ShapeMeasurement(true,MaximumGapMm:0,MaximumAngleDegrees:0,Samples:33,MaximumNormalCurvatureDifferencePerMm:0,CurvatureSamples:33);
        Check(Evaluate(requirement,measurement).Passed,"complete G2 sample evidence accepted");
        Check(!Evaluate(requirement,measurement with{MaximumNormalCurvatureDifferencePerMm=.1}).Passed,"G1 pass cannot conceal curvature mismatch");
        Check(!Evaluate(requirement,measurement with{MaximumNormalCurvatureDifferencePerMm=null,CurvatureSamples=0}).Passed,"G1-only evidence cannot satisfy G2 request");
        Check(!Evaluate(requirement,measurement with{CurvatureSamples=32}).Passed,"partial curvature sampling rejected");
        Check(!Evaluate(requirement,measurement with{MaximumNormalCurvatureDifferencePerMm=double.NaN}).Passed,"nonfinite curvature evidence rejected");
        Check(!Evaluate(requirement,measurement with{MaximumNormalCurvatureDifferencePerMm=-.1}).Passed,"negative maximum curvature evidence rejected");
        Check(!Evaluate(requirement,measurement with{MaximumAngleDegrees=-1}).Passed,"negative angle evidence rejected");
        Check(ModelVerification.Validate(new(){SurfaceContinuity=[requirement with{RequireTangency=false}]},null).Any(),"G2 contract cannot disable tangency check");
        foreach(var tolerance in new[]{0,-1,double.NaN,double.PositiveInfinity})
            Check(ModelVerification.Validate(new(){SurfaceContinuity=[requirement with{CurvatureTolerancePerMm=tolerance}]},null).Any(),"invalid curvature tolerance "+tolerance);
        Check(Evaluate(requirement with{RequireCurvatureContinuity=false},measurement with{MaximumNormalCurvatureDifferencePerMm=null,CurvatureSamples=0}).Passed,"existing G1 contract remains compatible");
        var spec=new ModelVerificationSpec{SurfaceContinuity=[requirement]};
        Check(JsonSerializer.Deserialize<ModelVerificationSpec>(JsonSerializer.Serialize(spec,ModelingIrJson.Options),ModelingIrJson.Options)!.SurfaceContinuity[0].RequireCurvatureContinuity,"G2 contract survives JSON roundtrip");
        File.WriteAllText(Path.Combine(output,"core-summary.json"),JsonSerializer.Serialize(new{passed,checks,scope="analytic_curvature_and_fail_closed_contract"},ModelingIrJson.Options));return 0;
    }
}
