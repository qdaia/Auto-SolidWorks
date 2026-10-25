using System.Text.Json;
using System.Diagnostics;
using System.Runtime.InteropServices;
using CadModeling.Drawing.Ingestion;
using OpenCvSharp;
using OpenCvSharp.XImgProc;

Cv2.SetNumThreads(1);
var json=new JsonSerializerOptions{WriteIndented=true,PropertyNamingPolicy=JsonNamingPolicy.SnakeCaseLower};
if(args.Length==3&&args[0]=="--batch")
{
    var cases=JsonDocument.Parse(File.ReadAllText(args[1])).RootElement;var rows=new List<object>();
    foreach(var c in cases.EnumerateArray())
    {
        using var src=Cv2.ImRead(c.GetProperty("image").GetString()!,ImreadModes.Grayscale);var ink=new byte[src.Rows*src.Cols];Marshal.Copy(src.Data,ink,0,ink.Length);for(int i=0;i<ink.Length;i++)ink[i]=ink[i]<128?(byte)1:(byte)0;
        var actual=InkCircleDetector.Detect(ink,src.Cols,src.Rows,8,128);
        var t=Stopwatch.StartNew();using var ed=EdgeDrawing.Create();ed.DetectEdges(src);var raw=ed.DetectEllipses();var ms=t.Elapsed.TotalMilliseconds;
        t.Restart();using var blurred=new Mat();Cv2.GaussianBlur(src,blurred,new Size(5,5),1);var hough=Cv2.HoughCircles(blurred,HoughModes.Gradient,1,8,100,30,8,220);var hms=t.Elapsed.TotalMilliseconds;
        rows.Add(new {id=c.GetProperty("id").GetString(),actual,ed_ms=ms,ed_circles=raw.Where(e=>e.Item2>0).Select(e=>new[]{e.Item0,e.Item1,e.Item2}),hough_ms=hms,hough_circles=hough.Select(e=>new[]{(double)e.Center.X,e.Center.Y,e.Radius})});
    }
    File.WriteAllText(args[2],JsonSerializer.Serialize(rows,json));return;
}
var output=Path.GetFullPath(args[0]);Directory.CreateDirectory(output);var checks=new List<string>();
void Check(bool value,string name){if(!value)throw new Exception(name);checks.Add(name);Console.WriteLine("PASS "+name);}
byte[] Canvas(params (double X,double Y,double R,double Start,double Sweep,int Width)[] arcs)
{
    // Independent analytic annulus rasterization, rather than the detector's OpenCV rasterizer.
    var a=new byte[512*512];
    for(int y=0;y<512;y++)for(int x=0;x<512;x++)foreach(var c in arcs)
    {
        double dx=x-c.X,dy=y-c.Y,r=double.Hypot(dx,dy),angle=(Math.Atan2(dy,dx)*180/Math.PI-c.Start+720)%360;
        if(Math.Abs(r-c.R)<=c.Width/2d&&angle<=c.Sweep)a[y*512+x]=1;
    }
    return a;
}
bool Match(InkCircle p,double x,double y,double r)=>double.Hypot(p.CenterX-x,p.CenterY-y)<=2&&Math.Abs(p.RadiusPixels-r)<=2;
foreach(int width in new[]{1,2,3,5})foreach(double radius in new[]{10d,25,60,130})
{
    var ink=Canvas((240.3,250.7,radius,0,360,width));var result=InkCircleDetector.Detect(ink,512,512);
    Check(result.Circles.Any(p=>Match(p,240.3,250.7,radius)),"full circle r="+radius+" width="+width);
    Check(result.Circles.Count==1,"single circle no duplicate r="+radius+" width="+width);
}
foreach(double start in new[]{0d,37,121,270})foreach(double sweep in new[]{90d,180,270})
{
    var result=InkCircleDetector.Detect(Canvas((250.3,250.7,80,start,sweep,2)),512,512);
    var match=result.Circles.FirstOrDefault(p=>Match(p,250.3,250.7,80));
    if(match is null)Console.WriteLine(JsonSerializer.Serialize(result,json));
    Check(match is not null,"partial arc start="+start+" sweep="+sweep);
    Check(match!.ObservedArcs.Sum(a=>a.SweepDegrees)<sweep+12&&match.SupportFraction<.85,"partial arc never completed to full circle "+start+"/"+sweep);
}
var concentric=InkCircleDetector.Detect(Canvas((250,250,30,0,360,2),(250,250,38,0,360,2),(250,250,90,0,360,2)),512,512);
Check(new[]{30d,38,90}.All(r=>concentric.Circles.Any(p=>Match(p,250,250,r))),"concentric radii retained");
var crossing=Canvas((250,250,60,0,360,2));for(int i=100;i<400;i++){crossing[250*512+i]=1;crossing[i*512+250]=1;}
Check(InkCircleDetector.Detect(crossing,512,512).Circles.Any(p=>Match(p,250,250,60)),"circle with center lines");
Check(InkCircleDetector.Detect(new byte[512*512],512,512).Circles.Count==0,"blank image yields no circles");
var rect=new byte[512*512];for(int i=100;i<=400;i++){rect[100*512+i]=1;rect[400*512+i]=1;rect[i*512+100]=1;rect[i*512+400]=1;}
Check(InkCircleDetector.Detect(rect,512,512).Circles.Count==0,"rectangle not circle");
using(var cancel=new CancellationTokenSource()){cancel.Cancel();try{InkCircleDetector.Detect(rect,512,512,token:cancel.Token);throw new Exception("Cancellation failed");}catch(OperationCanceledException){Check(true,"pre-cancel rejected");}}
var repeat=InkCircleDetector.Detect(crossing,512,512);Check(JsonSerializer.Serialize(repeat.Circles,json)==JsonSerializer.Serialize(InkCircleDetector.Detect(crossing,512,512).Circles,json),"deterministic coordinates");
foreach(int periods in new[]{12,20})foreach(int thickness in new[]{1,3})foreach(double radius in new[]{60d,100})
{
    var dashed=Canvas((250.3,250.7,radius,0,360,thickness));
    for(int y=0;y<512;y++)for(int x=0;x<512;x++)
    {
        double cycle=((Math.Atan2(y-250.7,x-250.3)+Math.PI)*periods/(2*Math.PI)+.23)%1;
        if(cycle>.65)dashed[y*512+x]=0;
    }
    var found=InkCircleDetector.Detect(dashed,512,512);var match=found.Circles.FirstOrDefault(c=>Match(c,250.3,250.7,radius));
    Check(match is not null,$"dashed circle periods={periods} thickness={thickness} radius={radius}");
    Check(match!.ObservedArcs.Count>=6&&match.ObservedArcs.Sum(a=>a.SweepDegrees)<310,"dashed gaps remain visible");
    Check(JsonSerializer.Serialize(found.Circles,json)==JsonSerializer.Serialize(InkCircleDetector.Detect(dashed,512,512).Circles,json),"dashed hypothesis determinism");
}
var specks=new byte[512*512];for(int y=0;y<512;y+=2)for(int x=0;x<512;x+=2)specks[y*512+x]=1;
Check(InkCircleDetector.Detect(specks,512,512).Truncated,"component budget truncation is explicit");
File.WriteAllText(Path.Combine(output,"result.json"),JsonSerializer.Serialize(new{success=true,count=checks.Count,checks},json));
