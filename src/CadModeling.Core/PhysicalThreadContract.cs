using System.Globalization;
using System.Text.RegularExpressions;
using CadModeling.Ir;

namespace CadModeling.Core;

public sealed record PhysicalThreadGeometryReceipt(int HelicalEdgeCount, double MinimumAxialSpanMm,
    double MaximumAxialSpanMm, double MaximumPitchErrorMm, double CutDepthMm,
    double MinimumRadiusMm = 0, double MaximumRadiusMm = 0);

public static class PhysicalThreadContract
{
    private static void Require(bool ok,string text)
    {if(!ok)throw new ArgumentException("PHYSICAL_THREAD_CONTRACT："+text);}
    public static void Validate(PhysicalThreadOptions t)
    {
        Require(Enum.IsDefined(t.Location),"未知内外螺纹位置。");
        var m=Regex.Match(t.Designation??"",@"^M(?<d>[0-9]+(?:\.[0-9]+)?)x(?<p>[0-9]+(?:\.[0-9]+)?)$",RegexOptions.CultureInvariant);
        Require(m.Success,"规格必须显式声明 M直径x螺距。");
        Require(double.TryParse(m.Groups["d"].Value,NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out var diameter)
            && double.TryParse(m.Groups["p"].Value,NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out var pitch)
            && diameter==t.MajorDiameterMm && pitch==t.PitchMm,"规格、名义直径和螺距不一致。");
        Require(double.IsFinite(t.MajorDiameterMm)&&t.MajorDiameterMm>0&&double.IsFinite(t.PitchMm)&&t.PitchMm>0
            && t.PitchMm<t.MajorDiameterMm/2&&double.IsFinite(t.LengthMm)&&t.LengthMm>=2*t.PitchMm
            && t.LengthMm/t.PitchMm<=200,"直径、螺距、长度或圈数无效／超限。");
        Require(double.IsFinite(t.RunoutAllowanceMm)&&t.RunoutAllowanceMm>=t.PitchMm&&t.RunoutAllowanceMm<=2*t.PitchMm,
            "必须明确保留 1..2 个螺距的牙型收尾空间。");
        var internalThread=t.Location==PhysicalThreadLocation.Internal;
        Require(Path.IsPathFullyQualified(t.ProfilePath)&&Path.GetFileName(t.ProfilePath).Equals(internalThread?"Metric Tap.SLDLFP":"Metric Die.SLDLFP",StringComparison.OrdinalIgnoreCase)
            && Regex.IsMatch(t.ProfileSha256??"",@"^[a-fA-F0-9]{64}$"),"需要固定 SHA256 且与内外位置一致的原生 Metric Tap／Die 名义牙型。");
        if(internalThread)
            Require(t.BoreDiameterMm is { } bore&&double.IsFinite(bore)&&bore>0&&bore<t.MajorDiameterMm
                &&t.MaximumCutDiameterMm is { } envelope&&double.IsFinite(envelope)&&envelope>=t.MajorDiameterMm
                &&envelope<=t.MajorDiameterMm+t.PitchMm,"名义内螺纹必须独立声明有效底孔直径及实际最大切削直径；不得用名义规格冒充实际公差。");
        else Require(t.BoreDiameterMm is null&&t.MaximumCutDiameterMm is null,"外螺纹不能携带内螺纹底孔或切削直径。");
        Require(t.AxisOriginMm is not null&&NativeFeatureValidation.Finite(t.AxisOriginMm)
            &&t.AxisIntoPart is not null&&NativeFeatureValidation.Finite(t.AxisIntoPart)&&NativeFeatureValidation.Norm(t.AxisIntoPart)>1e-10,
            "螺纹需要明确的入口中心及向零件内部的轴向。");
    }
    public static PhysicalThreadGeometryReceipt VerifyGeometry(PhysicalThreadOptions t,IReadOnlyList<IReadOnlyList<Vector3>> curves)
    {
        Validate(t);Require(curves.Count is >0 and <=4096,"原生牙面边界库存为空或超限。");
        var n=Scale(t.AxisIntoPart,1/NativeFeatureValidation.Norm(t.AxisIntoPart));
        var a=Math.Abs(n.X)<.8?new Vector3(1,0,0):new Vector3(0,1,0);
        var u=Scale(Sub(a,Scale(n,Dot(a,n))),1/NativeFeatureValidation.Norm(Sub(a,Scale(n,Dot(a,n)))));var v=Cross(n,u);
        var internalThread=t.Location==PhysicalThreadLocation.Internal;
        var maximumRadius=(internalThread?t.MaximumCutDiameterMm!.Value:t.MajorDiameterMm)/2;
        var boreRadius=internalThread?t.BoreDiameterMm!.Value/2:0;
        var spans=new List<double>();var independentHelixes=new List<(double Radius,double Phase)>();
        double error=0,cutDepth=0,minRadius=double.PositiveInfinity,maxRadius=0;bool cut=false;
        foreach(var points in curves)
        {
            Require(points.Count is >=65 and <=1025 && points.All(NativeFeatureValidation.Finite),"原生边采样不完整或非有限。");
            var local=points.Select(p=>Sub(p,t.AxisOriginMm)).ToArray();
            var z=local.Select(p=>Dot(p,n)).ToArray();var radius=local.Select(p=>NativeFeatureValidation.Norm(Sub(p,Scale(n,Dot(p,n))))).ToArray();
            minRadius=Math.Min(minRadius,radius.Min());maxRadius=Math.Max(maxRadius,radius.Max());
            cutDepth=Math.Max(cutDepth,z.Max());
            Require(z.Min()>=-.05 && z.Max()<=t.LengthMm+t.RunoutAllowanceMm+.05,"实际牙型超出声明的入口或收尾空间。");
            Require(radius.Max()<=maximumRadius+(internalThread ? .005 : .05),"实际牙面超出声明的切削直径／名义外径。");
            if(internalThread)Require(radius.Min()>=boreRadius-.005,"内螺纹牙面侵入声明底孔。");
            if(radius.Max()-radius.Min()>.005 || z.Max()-z.Min()<t.LengthMm-.05)continue;
            var theta=local.Select(p=>Math.Atan2(Dot(p,v),Dot(p,u))).ToArray();var unwrapped=new double[theta.Length];
            for(int i=1;i<theta.Length;i++)
            {
                var delta=Math.IEEERemainder(theta[i]-theta[i-1],2*Math.PI);
                Require(Math.Abs(delta)<Math.PI*.9,"螺旋边采样发生角度混叠。");unwrapped[i]=unwrapped[i-1]+delta;
            }
            var turns=(unwrapped[^1]-unwrapped[0])/(2*Math.PI);if(Math.Abs(turns)<1)continue;
            var measured=(z[^1]-z[0])/turns;
            var expected=t.RightHanded?t.PitchMm:-t.PitchMm;
            var pitchError=Math.Abs(measured-expected);error=Math.Max(error,pitchError);
            Require(pitchError<=.005,"实体螺旋边的螺距或旋向不符合声明。");
            for(int i=0;i<z.Length;i++)Require(Math.Abs(z[i]-z[0]-expected*unwrapped[i]/(2*Math.PI))<=.01,
                "牙型边界不能由声明的恒定螺距解释。");
            var span=z.Max()-z.Min();Require(span<=t.LengthMm+.05,"螺旋牙长超过声明值。");
            var meanRadius=radius.Average();var phase=z[0]-expected*theta[0]/(2*Math.PI);
            if(independentHelixes.Any(h=>Math.Abs(h.Radius-meanRadius)<=.005&&Math.Abs(Math.IEEERemainder(h.Phase-phase,t.PitchMm))<=.005))continue;
            independentHelixes.Add((meanRadius,phase));spans.Add(span);
            cut|=internalThread?radius.Max()>boreRadius+.05:radius.Min()<t.MajorDiameterMm/2-.05;
        }
        Require(spans.Count>=2&&cut,"缺少两条完整实体螺旋边或实切牙深；不能以装饰螺纹代替。");
        if(internalThread)Require(Math.Abs(minRadius-boreRadius)<=.005&&Math.Abs(maxRadius-maximumRadius)<=.005,
            "实际底孔或槽底直径未达到独立声明值；不能只声明宽松上界。");
        return new(spans.Count,spans.Min(),spans.Max(),error,cutDepth,minRadius,maxRadius);
    }
    private static double Dot(Vector3 a,Vector3 b)=>a.X*b.X+a.Y*b.Y+a.Z*b.Z;
    private static Vector3 Cross(Vector3 a,Vector3 b)=>new(a.Y*b.Z-a.Z*b.Y,a.Z*b.X-a.X*b.Z,a.X*b.Y-a.Y*b.X);
    private static Vector3 Sub(Vector3 a,Vector3 b)=>new(a.X-b.X,a.Y-b.Y,a.Z-b.Z);
    private static Vector3 Scale(Vector3 a,double s)=>new(a.X*s,a.Y*s,a.Z*s);
}
