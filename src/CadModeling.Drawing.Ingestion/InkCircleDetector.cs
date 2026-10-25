using System.Diagnostics;
using System.Runtime.InteropServices;
using OpenCvSharp;
using OpenCvSharp.XImgProc;

namespace CadModeling.Drawing.Ingestion;

public sealed record InkArcRange(double StartDegrees,double SweepDegrees);
public sealed record InkCircle(double CenterX,double CenterY,double RadiusPixels,double SupportFraction,double RadialErrorPixels,IReadOnlyList<InkArcRange> ObservedArcs);
public sealed record InkCircleDetection(IReadOnlyList<InkCircle> Circles,int RawCircleCount,int SegmentCircleCount,int ExcludedEllipseCount,bool Truncated,double Milliseconds,string EngineVersion)
{
    public int FragmentCircleCount{get;init;}
    public int DashCircleCount{get;init;}
}


/// <summary>EDCircles hypotheses checked against original ink; no hole/diameter or view semantics.</summary>
public static class InkCircleDetector
{
    public const string Revision="source-circles-2";
    private const int Samples=720;
    private static readonly double[] Cos=Enumerable.Range(0,Samples).Select(i=>Math.Cos(i*2*Math.PI/Samples)).ToArray();
    private static readonly double[] Sin=Enumerable.Range(0,Samples).Select(i=>Math.Sin(i*2*Math.PI/Samples)).ToArray();
    public static InkCircleDetection Detect(byte[] ink,int width,int height,double minimumRadius=8,int maximumCircles=64,CancellationToken token=default)
    {
        if(width<3||height<3||(long)width*height!=ink.Length||width>32768||height>32768||ink.Length>16_000_000)throw new ArgumentException("Invalid or oversized source ink image.");
        if(!double.IsFinite(minimumRadius)||minimumRadius<4||maximumCircles<1||maximumCircles>128)throw new ArgumentException("Invalid circle limits.");
        token.ThrowIfCancellationRequested();var timer=Stopwatch.StartNew();
        var pixels=new byte[ink.Length];for(int i=0;i<pixels.Length;i++)pixels[i]=ink[i]==0?(byte)255:(byte)0;
        using var src=new Mat(height,width,MatType.CV_8UC1);Marshal.Copy(pixels,0,src.Data,pixels.Length);
        using var detector=EdgeDrawing.Create();detector.DetectEdges(src);token.ThrowIfCancellationRequested();
        var raw=detector.DetectEllipses();token.ThrowIfCancellationRequested();
        var hypotheses=new List<(double X,double Y,double R)>();int count=0,ellipses=0,segmentCircles=0;
        foreach(var e in raw.Take(2048))
        {if(e.Item2<=0){ellipses++;continue;}count++;hypotheses.Add((e.Item0,e.Item1,e.Item2));}
        // Short arcs may not be promoted to circles by EDCircles. Fit connected edge
        // segments as additional hypotheses; the same source-ink coverage checks apply.
        var segments=detector.GetSegments();
        foreach(var segment in segments.Take(4096))
        {
            token.ThrowIfCancellationRequested();if(segment.Length<24)continue;
            int step=Math.Max(1,segment.Length/256);var points=new List<(double X,double Y)>();
            for(int i=0;i<segment.Length;i+=step)points.Add((segment[i].X,segment[i].Y));
            double ox=points.Average(p=>p.X),oy=points.Average(p=>p.Y);
            if(!Fit(points,ox,oy,out var sx,out var sy,out var sr)||sr<minimumRadius||sr>Math.Max(width,height))continue;
            if(Math.Sqrt(points.Average(p=>Math.Pow(double.Hypot(p.X-sx,p.Y-sy)-sr,2)))>2.5)continue;
            hypotheses.Add((sx,sy,sr));segmentCircles++;
            if(hypotheses.Count>=2048)break;
        }

        int primaryHypotheses=hypotheses.Count;int fragmentCircleCount=0;
        bool supplementaryTruncated=false;
        var dashed=DashCircleHypotheses.Find(src,width,height,minimumRadius,token);supplementaryTruncated=dashed.Truncated;
        foreach(var dash in dashed.Circles)hypotheses.Add(dash);
        int examinedWindows=0;
        foreach(var segment in segments.Take(4096))
        {
            foreach(int length in new[]{24,48,96})
            {
                if(segment.Length<length)continue;
                for(int start=0;start+length<=segment.Length;start+=length/2)
                {
                    token.ThrowIfCancellationRequested();
                    if(++examinedWindows>6000){supplementaryTruncated=true;break;}
                    var first=segment[start];var middle=segment[start+length/2];var last=segment[start+length-1];
                    double chord=double.Hypot(last.X-first.X,last.Y-first.Y);
                    if(length>=48&&chord>1&&Math.Abs((middle.X-first.X)*(double)(last.Y-first.Y)-(middle.Y-first.Y)*(double)(last.X-first.X))/chord<.15)continue;
                    if(hypotheses.Take(count).Any(h=>Math.Abs(double.Hypot(first.X-h.X,first.Y-h.Y)-h.R)<2&&Math.Abs(double.Hypot(middle.X-h.X,middle.Y-h.Y)-h.R)<2&&Math.Abs(double.Hypot(last.X-h.X,last.Y-h.Y)-h.R)<2))continue;
                    var points=new List<(double X,double Y)>();
                    for(int i=start;i<start+length;i++)points.Add((segment[i].X,segment[i].Y));
                    if(!Fit(points,points[0].X,points[0].Y,out var x,out var y,out var radius))continue;
                    if(radius<minimumRadius||radius>Math.Max(width,height)||x<0||y<0||x>=width||y>=height)continue;
                    if(Math.Sqrt(points.Average(p=>Math.Pow(double.Hypot(p.X-x,p.Y-y)-radius,2)))>.55)continue;
                    if(hypotheses.Any(p=>double.Hypot(p.X-x,p.Y-y)<3&&Math.Abs(p.R-radius)<3))continue;
                    int supported=0;
                    for(int i=0;i<Samples;i+=12)if(StrokeOffset(ink,width,height,x+radius*Cos[i],y+radius*Sin[i],Cos[i],Sin[i],3) is not null)supported++;
                    if(supported<36)continue;
                    hypotheses.Add((x,y,radius));fragmentCircleCount++;
                    if(hypotheses.Count>=1024)break;
                }
                if(examinedWindows>6000||hypotheses.Count>=1024)break;
            }
            if(examinedWindows>6000||hypotheses.Count>=1024)break;
        }
        var proposals=new List<InkCircle>();
        for(int hypothesisIndex=0;hypothesisIndex<Math.Min(2048,hypotheses.Count);hypothesisIndex++)
        {
            var e=hypotheses[hypothesisIndex];
            token.ThrowIfCancellationRequested();
            double cx=e.X,cy=e.Y,r=e.R;
            if(!double.IsFinite(cx)||!double.IsFinite(cy)||!double.IsFinite(r)||cx<0||cy<0||cx>=width||cy>=height||r<minimumRadius||r>Math.Max(width,height))continue;
            for(int iteration=0;iteration<3;iteration++)
            {
                var points=RadialPoints(ink,width,height,cx,cy,r);
                if(points.Count<24||!Fit(points,cx,cy,out var nextX,out var nextY,out var nextR))break;
                if(double.Hypot(nextX-cx,nextY-cy)>8||Math.Abs(nextR-r)>8)break;
                cx=nextX;cy=nextY;r=nextR;
            }
            if(r<minimumRadius||cx<0||cy<0||cx>=width||cy>=height)continue;
            var hits=new bool[Samples];double errorSum=0;int supported=0;
            for(int i=0;i<Samples;i++)
            {
                var offset=StrokeOffset(ink,width,height,cx+r*Cos[i],cy+r*Sin[i],Cos[i],Sin[i],1.5);
                if(offset is null)continue;hits[i]=true;supported++;errorSum+=offset.Value*offset.Value;
            }
            var arcs=Ranges(hits,hypothesisIndex>=primaryHypotheses?3:10);double coverage=supported/(double)Samples;
            int sectors=Enumerable.Range(0,12).Count(sector=>Enumerable.Range(sector*60,60).Count(i=>hits[i])>=15);
            if(arcs.Count==0||coverage<.24||coverage<.75&&arcs.Max(a=>a.SweepDegrees)<85&&!(coverage>=.45&&sectors>=10))continue;
            double radialError=Math.Sqrt(errorSum/Math.Max(1,supported));
            bool fragment=hypothesisIndex>=primaryHypotheses;
            if(fragment&&coverage<.75&&sectors<10)continue;
            if(radialError>(fragment?.4:.65))continue;
            if(fragment&&proposals.Any(p=>double.Hypot(p.CenterX-cx,p.CenterY-cy)<4&&Math.Abs(p.RadiusPixels-r)<4))continue;
            proposals.Add(new(cx,cy,r,coverage,radialError,arcs));
        }
        var selected=new List<InkCircle>();
        foreach(var p in proposals.OrderByDescending(x=>x.SupportFraction*x.RadiusPixels).ThenBy(x=>x.CenterX).ThenBy(x=>x.CenterY).ThenBy(x=>x.RadiusPixels))
        {
            if(selected.Any(q=>double.Hypot(p.CenterX-q.CenterX,p.CenterY-q.CenterY)<2&&Math.Abs(p.RadiusPixels-q.RadiusPixels)<2))continue;
            selected.Add(p);if(selected.Count>maximumCircles)break;
        }
        return new(selected.Take(maximumCircles).ToArray(),count,segmentCircles,ellipses,supplementaryTruncated||fragmentCircleCount>0&&hypotheses.Count>=1024||selected.Count>maximumCircles||raw.Length>2048||segments.Length>4096||hypotheses.Count>=2048,timer.Elapsed.TotalMilliseconds,Cv2.GetVersionString()??"unknown"){FragmentCircleCount=fragmentCircleCount,DashCircleCount=dashed.Circles.Count};
    }
    private static double? StrokeOffset(byte[] ink,int width,int height,double x,double y,double nx,double ny,double band)
    {
        double sum=0;int count=0,previous=-1;
        for(double t=-band;t<=band+.01;t+=.5)
        {
            int px=(int)Math.Round(x+nx*t),py=(int)Math.Round(y+ny*t);
            if(px<0||py<0||px>=width||py>=height)continue;
            int index=py*width+px;if(index==previous)continue;previous=index;
            if(ink[index]==0)continue;sum+=(px-x)*nx+(py-y)*ny;count++;
        }
        if(count==0)
        {
            int baseX=(int)Math.Round(x),baseY=(int)Math.Round(y);
            for(int oy=-1;oy<=1;oy++)for(int ox=-1;ox<=1;ox++)
            {
                int px=baseX+ox,py=baseY+oy;if(px<0||py<0||px>=width||py>=height||ink[py*width+px]==0)continue;
                double dx=px-x,dy=py-y,radial=dx*nx+dy*ny,tangent=-dx*ny+dy*nx;
                if(Math.Abs(radial)<=band&&Math.Abs(tangent)<=1){sum+=radial;count++;}
            }
        }
        return count==0?null:sum/count;
    }
    private static List<(double X,double Y)> RadialPoints(byte[] ink,int width,int height,double cx,double cy,double r)
    {
        var points=new List<(double X,double Y)>();var offsets=new double?[Samples];
        for(int i=0;i<Samples;i++)
            offsets[i]=StrokeOffset(ink,width,height,cx+r*Cos[i],cy+r*Sin[i],Cos[i],Sin[i],3);
        // End caps of an open stroke are not circle samples. Trim a small source
        // neighbourhood at each gap so they cannot pull a quarter-arc fit inward.
        int trim=Math.Clamp((int)Math.Ceiling(4/r*Samples/(2*Math.PI)),3,16);
        for(int i=0;i<Samples;i+=2)
        {
            if(offsets[i] is not {} t)continue;
            bool stable=true;for(int j=-trim;j<=trim;j++)if(offsets[(i+j+Samples)%Samples] is null){stable=false;break;}
            if(stable)points.Add((cx+(r+t)*Cos[i],cy+(r+t)*Sin[i]));
        }
        return points;
    }
    private static bool Fit(List<(double X,double Y)> points,double originX,double originY,out double cx,out double cy,out double radius)
    {
        // Centered least squares avoids loss of precision on large source pages.
        var m=new double[3,4];
        foreach(var p in points)
        {
            double u=p.X-originX,v=p.Y-originY,z=u*u+v*v;double[] a=[2*u,2*v,1];
            for(int i=0;i<3;i++){for(int j=0;j<3;j++)m[i,j]+=a[i]*a[j];m[i,3]+=a[i]*z;}
        }
        cx=cy=radius=0;
        if(!Solve(m))return false;
        cx=originX+m[0,3];cy=originY+m[1,3];double square=m[2,3]+m[0,3]*m[0,3]+m[1,3]*m[1,3];
        if(square<=0||!double.IsFinite(square))return false;radius=Math.Sqrt(square);
        // Geometric residual refinement removes algebraic-fit shrinkage on short,
        // thick arcs. It uses only the observed edge/stroke samples.
        for(int iteration=0;iteration<4;iteration++)
        {
            var n=new double[3,4];
            foreach(var p in points)
            {
                double dx=cx-p.X,dy=cy-p.Y,d=double.Hypot(dx,dy);if(d<1e-9)continue;
                double[] a=[dx/d,dy/d,-1];double error=d-radius;
                for(int i=0;i<3;i++){for(int j=0;j<3;j++)n[i,j]+=a[i]*a[j];n[i,3]-=a[i]*error;}
            }
            if(!Solve(n)||double.Hypot(n[0,3],n[1,3])>Math.Max(radius,10))break;
            cx+=n[0,3];cy+=n[1,3];radius+=n[2,3];
            if(double.Hypot(n[0,3],n[1,3])<1e-4&&Math.Abs(n[2,3])<1e-4)break;
        }
        return double.IsFinite(cx)&&double.IsFinite(cy)&&double.IsFinite(radius)&&radius>0;
    }
    private static bool Solve(double[,] m)
    {
        for(int col=0;col<3;col++)
        {
            int pivot=col;for(int row=col+1;row<3;row++)if(Math.Abs(m[row,col])>Math.Abs(m[pivot,col]))pivot=row;
            if(Math.Abs(m[pivot,col])<1e-9)return false;
            for(int j=col;j<4;j++)(m[col,j],m[pivot,j])=(m[pivot,j],m[col,j]);
            double scale=m[col,col];for(int j=col;j<4;j++)m[col,j]/=scale;
            for(int row=0;row<3;row++)if(row!=col){double factor=m[row,col];for(int j=col;j<4;j++)m[row,j]-=factor*m[col,j];}
        }
        return true;
    }
    private static List<InkArcRange> Ranges(bool[] hits,double minimumSweep)
    {
        var result=new List<InkArcRange>();int firstGap=Array.FindIndex(hits,x=>!x);
        if(firstGap<0){result.Add(new(0,360));return result;}
        int run=-1;
        for(int step=1;step<=Samples+1;step++)
        {
            bool hit=step<=Samples&&hits[(firstGap+step)%Samples];
            if(hit&&run<0)run=step;
            if(!hit&&run>=0)
            {
                double sweep=(step-run)*360d/Samples;
                if(sweep>=minimumSweep)result.Add(new((firstGap+run)%Samples*360d/Samples,sweep));
                run=-1;
            }
        }
        return result;
    }
}
