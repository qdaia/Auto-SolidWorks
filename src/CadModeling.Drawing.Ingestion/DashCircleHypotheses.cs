using OpenCvSharp;
using System.Runtime.InteropServices;

namespace CadModeling.Drawing.Ingestion;

internal static class DashCircleHypotheses
{
    public static (List<(double X,double Y,double R)> Circles,bool Truncated) Find(Mat source,int width,int height,double minimumRadius,CancellationToken token)
    {
        using var foreground=new Mat();Cv2.BitwiseNot(source,foreground);
        using var labels=new Mat();using var stats=new Mat();using var centers=new Mat();
        int count=Cv2.ConnectedComponentsWithStats(foreground,labels,stats,centers,PixelConnectivity.Connectivity8);
        token.ThrowIfCancellationRequested();
        if(count>50_000)return ([],true);
        var ids=new int[width*height];Marshal.Copy(labels.Data,ids,0,ids.Length);
        var xx=new double[count];var xy=new double[count];var yy=new double[count];
        for(int y=0;y<height;y++)
        {
            token.ThrowIfCancellationRequested();
            for(int x=0;x<width;x++)
            {
            int i=ids[y*width+x];if(i==0)continue;
            xx[i]+=(double)x*x;xy[i]+=(double)x*y;yy[i]+=(double)y*y;
            }
        }
        var points=new List<(double X,double Y)>();
        for(int i=1;i<count;i++)
        {
            int area=stats.At<int>(i,4);if(area<5||area>2000)continue;
            double x=centers.At<double>(i,0),y=centers.At<double>(i,1);
            double vx=xx[i]/area-x*x,vy=yy[i]/area-y*y,vxy=xy[i]/area-x*y;
            double difference=Math.Sqrt((vx-vy)*(vx-vy)+4*vxy*vxy),major=(vx+vy+difference)/2,minor=(vx+vy-difference)/2;
            if(major<2||minor/major>.35)continue;
            points.Add((x,y));
        }
        if(points.Count<6)return ([],false);
        bool truncated=points.Count>256;
        if(points.Count>256)points=points.Where((p,i)=>i%((points.Count+255)/256)==0).Take(256).ToList();
        var random=new Random(75031);var candidates=new List<(double X,double Y,double R,int Support)>();
        for(int trial=0;trial<1536;trial++)
        {
            token.ThrowIfCancellationRequested();
            var a=points[random.Next(points.Count)];var b=points[random.Next(points.Count)];var c=points[random.Next(points.Count)];
            double bx=b.X-a.X,by=b.Y-a.Y,cx=c.X-a.X,cy=c.Y-a.Y,d=2*(bx*cy-by*cx);
            if(Math.Abs(d)<10)continue;
            double bs=bx*bx+by*by,cs=cx*cx+cy*cy,x=a.X+(bs*cy-cs*by)/d,y=a.Y+(bx*cs-cx*bs)/d,r=double.Hypot(x-a.X,y-a.Y);
            if(x<0||y<0||x>=width||y>=height||r<minimumRadius||r>Math.Max(width,height))continue;
            if(candidates.Any(h=>double.Hypot(h.X-x,h.Y-y)<3&&Math.Abs(h.R-r)<3))continue;
            int support=0;int sectors=0;
            foreach(var p in points)
            {
                if(Math.Abs(double.Hypot(p.X-x,p.Y-y)-r)>2)continue;
                support++;int sector=(int)((Math.Atan2(p.Y-y,p.X-x)+Math.PI)*6/Math.PI)%12;sectors|=1<<sector;
            }
            if(support>=6&&System.Numerics.BitOperations.PopCount((uint)sectors)>=6)candidates.Add((x,y,r,support));
        }
        return (candidates.OrderByDescending(c=>c.Support).Take(32).Select(c=>(c.X,c.Y,c.R)).ToList(),truncated||candidates.Count>32);
    }
}
