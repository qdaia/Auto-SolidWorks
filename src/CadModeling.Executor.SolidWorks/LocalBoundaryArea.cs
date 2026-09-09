using CadModeling.Core;
using CadModeling.Ir;
using SolidWorks.Interop.sldworks;

internal sealed partial class SolidWorksComExecutor
{
    // Stokes integrals over every oriented trimming loop. These three analytic surfaces are developable.
    // Plane: 1/2 (p x dp).n. Cylinder: -r z dtheta. Cone: r^2/(2 sin(alpha)) dtheta.
    // This includes inner loops and seam coedges, without tessellating the whole face over out-of-process COM.
    private static (double Area,double Error) MeasureBoundaryFaceArea(IFace2 face,double budgetMm2)
    {
        var surface=(ISurface)face.GetSurface();var kind=surface.IsPlane()?0:surface.IsCylinder()?1:surface.IsCone()?2:-1;
        if(kind<0)throw new InvalidOperationException("Area integration supports plane, cylinder and cone faces only.");
        Vector3 origin,axis;double radius=0,halfAngle=0;
        if(kind==0)
        {
            var uv=ToDoubles(face.GetUVBounds(),4,"plane UV");var p=ToDoubles(surface.Evaluate((uv[0]+uv[1])/2,(uv[2]+uv[3])/2,0,0),6,"plane point and normal");
            origin=new(p[0]*1000,p[1]*1000,p[2]*1000);axis=ModelVerification.Unit(new(p[3],p[4],p[5]));
        }
        else
        {
            var p=ToDoubles(kind==1?surface.CylinderParams:surface.ConeParams2,kind==1?7:11,"analytic area parameters");
            origin=new(p[0]*1000,p[1]*1000,p[2]*1000);axis=ModelVerification.Unit(new(p[3],p[4],p[5]));
            radius=p[6]*1000;if(kind==2)halfAngle=Math.Abs(p[7]);
            if(kind==1&&!ModelVerification.Positive(radius)||kind==2&&(!ModelVerification.Positive(halfAngle)||halfAngle>=Math.PI/2))throw new InvalidOperationException("Invalid analytic area parameters.");
        }
        var loops=(face.GetLoops() as object[]??[]).Cast<ILoop2>().ToArray();
        if(loops.Length==0||loops.Length!=face.GetLoopCount())throw new InvalidOperationException("Incomplete area trimming-loop inventory.");
        var coedges=new List<ICoEdge>();
        foreach(var loop in loops)
        {
            if(loop.IsSingular())continue; // A cone apex contributes zero to the boundary integral.
            var items=(loop.GetCoEdges() as object[]??[]).Cast<ICoEdge>().ToArray();
            if(items.Length==0||items.Length!=loop.GetCoEdgeCount())throw new InvalidOperationException("Incomplete oriented trimming boundary.");
            coedges.AddRange(items);
        }
        if(coedges.Count==0||coedges.Count>2048)throw new InvalidOperationException("Unsupported area boundary size.");
        double total=0,error=0;int evaluations=0;
        var elapsed=System.Diagnostics.Stopwatch.StartNew();
        foreach(var coedge in coedges)
        {
            // Initialize underlying curve metadata before retrieving the coedge's parameter interval.
            _=((IEdge)coedge.GetEdge()).GetCurve();
            var parameters=ToDoubles(coedge.GetCurveParams(),8,"coedge parameters");
            var lo=Math.Min(parameters[6],parameters[7]);var hi=Math.Max(parameters[6],parameters[7]);
            if(!double.IsFinite(lo)||!double.IsFinite(hi)||hi<=lo)throw new InvalidOperationException("Invalid coedge interval.");
            double Integrand(double t)
            {
                if(++evaluations>20_000||elapsed.Elapsed.TotalSeconds>20)throw new InvalidOperationException("Boundary area exceeded its bounded evaluation/time budget.");
                var value=ToDoubles(coedge.Evaluate2(t,1),6,"oriented coedge derivative");
                var p=ModelVerification.Sub(new(value[0]*1000,value[1]*1000,value[2]*1000),origin);
                var derivative=new Vector3(value[3]*1000,value[4]*1000,value[5]*1000);
                if(!ModelVerification.Finite(p)||!ModelVerification.Finite(derivative))throw new InvalidOperationException("Invalid boundary derivative.");
                var cross=new Vector3(p.Y*derivative.Z-p.Z*derivative.Y,p.Z*derivative.X-p.X*derivative.Z,p.X*derivative.Y-p.Y*derivative.X);
                var projected=ModelVerification.Dot(cross,axis);
                return kind==0?projected/2:kind==1?-ModelVerification.Dot(p,axis)*projected/radius:projected/(2*Math.Sin(halfAngle));
            }
            var integral=RefineIntegral(Integrand,lo,hi,budgetMm2/coedges.Count);
            total+=integral.Value;error+=integral.Error;
        }
        var area=Math.Abs(total);
        if(!ModelVerification.Positive(area)||!double.IsFinite(error)||error>budgetMm2)throw new InvalidOperationException("Boundary area did not reach the requested numerical precision.");
        return (area,error);
    }

    private static (double Value,double Error) RefineIntegral(Func<double,double> fn,double lo,double hi,double budget)
    {
        double[] nodes=[.1834346424956498,.525532409916329,.7966664774136267,.9602898564975363];
        double[] weights=[.362683783378362,.3137066458778873,.2223810344533745,.1012285362903763];
        double? prior=null,priorDifference=null;
        for(var panels=1;panels<=64;panels*=2)
        {
            double result=0;var width=(hi-lo)/panels;
            for(var panel=0;panel<panels;panel++)
            {
                var mid=lo+(panel+.5)*width;
                for(var i=0;i<4;i++)result+=width/2*weights[i]*(fn(mid-width/2*nodes[i])+fn(mid+width/2*nodes[i]));
            }
            if(!double.IsFinite(result))throw new InvalidOperationException("Nonfinite boundary area integral.");
            if(prior is {} p)
            {
                var difference=Math.Abs(result-p);var floor=Math.Max(1,Math.Abs(result))*1e-10;
                var estimatedError=Math.Max(2*difference,floor);
                if(priorDifference is {} last&&difference<=last+floor&&estimatedError<=budget)return (result,estimatedError);
                priorDifference=difference;
            }
            prior=result;
        }
        throw new InvalidOperationException("Boundary area quadrature failed to converge within 64 panels.");
    }
}
