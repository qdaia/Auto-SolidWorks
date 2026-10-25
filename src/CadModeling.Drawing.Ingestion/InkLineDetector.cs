using System.Diagnostics;

namespace CadModeling.Drawing.Ingestion;

public sealed record InkLine(double X1,double Y1,double X2,double Y2,double SupportFraction,double LengthPixels);
public sealed record InkLineDetection(IReadOnlyList<InkLine> Lines,int ForegroundPixels,int SampledPixels,bool Truncated,double Milliseconds);

/// <summary>Bounded, deterministic all-angle source-ink proposals. No text or semantic recognition.</summary>
public static class InkLineDetector
{
    public const string Revision="source-lines-1";
    public static InkLineDetection Detect(byte[] ink,int width,int height,double minimumLength=24,int maximumLines=128,CancellationToken token=default)
    {
        if(width<1||height<1||(long)width*height!=ink.Length||width>32768||height>32768||ink.Length>64_000_000)
            throw new ArgumentException("Invalid or oversized source-ink image.");
        if(!double.IsFinite(minimumLength)||minimumLength<8||maximumLines<1||maximumLines>512)throw new ArgumentException("Invalid line limits.");
        var clock=Stopwatch.StartNew();var points=new List<(int X,int Y)>();int foreground=0;
        for(int y=0;y<height;y++)
        {
            token.ThrowIfCancellationRequested();
            for(int x=0;x<width;x++)
            {
                if(ink[y*width+x]==0)continue;foreground++;
                if(x==0||y==0||x==width-1||y==height-1||ink[y*width+x-1]==0||ink[y*width+x+1]==0||ink[(y-1)*width+x]==0||ink[(y+1)*width+x]==0)
                    points.Add((x,y));
            }
        }
        if(points.Count<minimumLength/2)return new([],foreground,points.Count,false,clock.Elapsed.TotalMilliseconds);
        // Even sampling bounds work without nondeterministic random state. Original pixels remain authoritative.
        int sampled=Math.Min(points.Count,12000), diagonal=(int)Math.Ceiling(Math.Sqrt((double)width*width+(double)height*height));
        int rhoCount=diagonal*2+3;var votes=new int[180*rhoCount];var cos=new double[180];var sin=new double[180];
        for(int a=0;a<180;a++){cos[a]=Math.Cos(a*Math.PI/180);sin[a]=Math.Sin(a*Math.PI/180);}
        for(int i=0;i<sampled;i++)
        {
            if(i%256==0)token.ThrowIfCancellationRequested();
            var p=points[(int)((long)i*points.Count/sampled)];
            for(int a=0;a<180;a++)votes[a*rhoCount+(int)Math.Round(p.X*cos[a]+p.Y*sin[a])+diagonal]++;
        }
        var peaks=new List<(int Votes,int Angle,int Rho)>();
        int threshold=Math.Max(5,(int)(minimumLength*.3*sampled/points.Count));
        for(int a=0;a<180;a++)
        for(int r=2;r<rhoCount-2;r++)
        {
            int index=a*rhoCount+r,v=votes[index];if(v<threshold)continue;
            if(v<votes[index-1]||v<votes[index+1]||v==votes[index-1])continue;
            if(a>0&&v<votes[index-rhoCount]||a<179&&v<votes[index+rhoCount])continue;
            peaks.Add((v,a,r-diagonal));
        }
        bool peakBudgetReached=peaks.Count>600;
        peaks=peaks.OrderByDescending(p=>p.Votes).ThenBy(p=>p.Angle).ThenBy(p=>p.Rho).Take(600).ToList();
        var candidates=new List<InkLine>();
        foreach(var peak in peaks)
        {
            token.ThrowIfCancellationRequested();
            double nx=cos[peak.Angle],ny=sin[peak.Angle],dx=-ny,dy=nx,rho=peak.Rho;
            double start=double.PositiveInfinity,end=double.NegativeInfinity;
            foreach(var (x,y) in new[]{(0,0),(width-1,0),(0,height-1),(width-1,height-1)}){double t=x*dx+y*dy;start=Math.Min(start,t);end=Math.Max(end,t);}
            int runStart=0,lastHit=0,hits=0;bool active=false;
            void Finish()
            {
                if(!active)return;
                int length=lastHit-runStart+1;
                if(length>=minimumLength&&hits/(double)length>=.8)
                {
                    int stableStart=runStart,stableEnd=lastHit;
                    bool Stable(int first,int direction)
                    {
                        for(int j=0;j<7;j++){int t=first+direction*j;if(!NearInk(ink,width,height,nx*rho+dx*t,ny*rho+dy*t,1))return false;}
                        return true;
                    }
                    while(stableStart<stableEnd-7&&!Stable(stableStart,1))stableStart++;
                    while(stableEnd>stableStart+7&&!Stable(stableEnd,-1))stableEnd--;
                    var line=Refine(ink,width,height,nx,ny,rho,dx,dy,stableStart,stableEnd);
                    if(line.LengthPixels>=minimumLength&&line.SupportFraction>=.85)candidates.Add(line);
                }
                active=false;hits=0;
            }
            for(int t=(int)Math.Floor(start);t<=(int)Math.Ceiling(end);t++)
            {
                double x=nx*rho+dx*t,y=ny*rho+dy*t;
                bool supported=NearInk(ink,width,height,x,y,1);
                if(supported){if(!active){runStart=t;active=true;}lastHit=t;hits++;}
                else if(active&&t-lastHit>4)Finish();
            }
            Finish();
        }
        var selected=new List<InkLine>();
        foreach(var candidate in candidates.OrderByDescending(x=>x.LengthPixels*x.SupportFraction).ThenBy(x=>x.X1).ThenBy(x=>x.Y1))
        {
            if(selected.Any(line=>Equivalent(line,candidate)))continue;
            selected.Add(candidate);
            if(selected.Count>maximumLines)break;
        }
        return new(selected.Take(maximumLines).ToArray(),foreground,sampled,selected.Count>maximumLines||peakBudgetReached,clock.Elapsed.TotalMilliseconds);
    }
    private static InkLine Refine(byte[] ink,int width,int height,double nx,double ny,double rho,double dx,double dy,int start,int end)
    {
        var points=new List<(double X,double Y)>();
        for(int t=start;t<=end;t++)
        {
            double x=nx*rho+dx*t,y=ny*rho+dy*t;var support=new List<(double X,double Y)>();
            for(int offset=-4;offset<=4;offset++)
            {
                int px=(int)Math.Round(x+nx*offset),py=(int)Math.Round(y+ny*offset);
                if(px>=0&&py>=0&&px<width&&py<height&&ink[py*width+px]!=0)support.Add((px,py));
            }
            if(support.Count>0)points.Add((support.Average(p=>p.X),support.Average(p=>p.Y)));
        }
        if(points.Count<2)return new(0,0,0,0,0,0);
        double cx=points.Average(p=>p.X),cy=points.Average(p=>p.Y),xx=0,xy=0,yy=0;
        foreach(var p in points){double x=p.X-cx,y=p.Y-cy;xx+=x*x;xy+=x*y;yy+=y*y;}
        double angle=.5*Math.Atan2(2*xy,xx-yy);dx=Math.Cos(angle);dy=Math.Sin(angle);
        double lo=points.Min(p=>(p.X-cx)*dx+(p.Y-cy)*dy),hi=points.Max(p=>(p.X-cx)*dx+(p.Y-cy)*dy);
        double x1=Math.Clamp(cx+lo*dx,0,width-1),y1=Math.Clamp(cy+lo*dy,0,height-1),x2=Math.Clamp(cx+hi*dx,0,width-1),y2=Math.Clamp(cy+hi*dy,0,height-1);
        int count=Math.Max(2,(int)Math.Ceiling(hi-lo)+1),hits=0;
        for(int i=0;i<count;i++){double f=i/(double)(count-1);if(NearInk(ink,width,height,x1+(x2-x1)*f,y1+(y2-y1)*f,1))hits++;}
        return new(x1,y1,x2,y2,hits/(double)count,Math.Sqrt((x2-x1)*(x2-x1)+(y2-y1)*(y2-y1)));
    }
    private static bool NearInk(byte[] ink,int width,int height,double x,double y,int tolerance)
    {
        int px=(int)Math.Round(x),py=(int)Math.Round(y);
        for(int oy=-tolerance;oy<=tolerance;oy++)for(int ox=-tolerance;ox<=tolerance;ox++)
        {int xx=px+ox,yy=py+oy;if(xx>=0&&yy>=0&&xx<width&&yy<height&&ink[yy*width+xx]!=0)return true;}
        return false;
    }
    private static bool Equivalent(InkLine a,InkLine b)
    {
        double dx=(a.X2-a.X1)/a.LengthPixels,dy=(a.Y2-a.Y1)/a.LengthPixels;
        double distance1=Math.Abs((b.X1-a.X1)*dy-(b.Y1-a.Y1)*dx),distance2=Math.Abs((b.X2-a.X1)*dy-(b.Y2-a.Y1)*dx);
        if(distance1>3.5||distance2>3.5)return false;
        double t1=(b.X1-a.X1)*dx+(b.Y1-a.Y1)*dy,t2=(b.X2-a.X1)*dx+(b.Y2-a.Y1)*dy;
        return Math.Min(a.LengthPixels,Math.Max(t1,t2))-Math.Max(0,Math.Min(t1,t2))>=.8*b.LengthPixels;
    }
}
