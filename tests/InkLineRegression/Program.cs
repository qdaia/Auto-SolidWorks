using CadModeling.Drawing.Ingestion;
using System.Text.Json;
using CadModeling.Drawing.Contracts;
using CadModeling.Drawing.Providers.Abstractions;

if(args.Length==3&&args[0]=="--batch")
{
    using var input=JsonDocument.Parse(File.ReadAllText(args[1]));var results=new List<object>();
    var method=typeof(DeterministicPrimitiveObservationProvider).GetMethod("FindAxisLines",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Static)!;
    foreach(var row in input.RootElement.EnumerateArray())
    {
        var path=row.GetProperty("mask_path").GetString()!;int width=row.GetProperty("width").GetInt32(),height=row.GetProperty("height").GetInt32();var ink=File.ReadAllBytes(path);
        var detected=InkLineDetector.Detect(ink,width,height,24,128);
        var observations=new List<ObservationEntity>();var context=new ProviderPageContext{SourcePath=path,SourceSha256="benchmark-fixture",PageNumber=1,PageArtifactDirectory=Path.GetDirectoryName(path)!,InputKind=DrawingInputKind.Png};
        var clock=System.Diagnostics.Stopwatch.StartNew();
        foreach(bool horizontal in new[]{true,false})method.Invoke(null,[ink,width,height,horizontal,observations,context,"pixels","page"]);
        var baseline=observations.Select(o=>new[]{o.Geometry[0].X,o.Geometry[0].Y,o.Geometry[1].X,o.Geometry[1].Y}).ToArray();double milliseconds=clock.Elapsed.TotalMilliseconds;
        results.Add(new{id=row.GetProperty("id").GetString(),candidate=detected,baseline=new{lines=baseline,milliseconds}});
    }
    File.WriteAllText(args[2],JsonSerializer.Serialize(results));return;
}

var output=Path.GetFullPath(args[0]);Directory.CreateDirectory(output);
var rows=new List<object>();int passed=0;
void Check(bool ok,string name){if(!ok)throw new Exception(name);Console.WriteLine("PASS "+name);passed++;}
void Draw(byte[] ink,int w,int h,double x1,double y1,double x2,double y2,int thickness=1)
{
    int count=(int)Math.Ceiling(Math.Sqrt((x2-x1)*(x2-x1)+(y2-y1)*(y2-y1))*3);
    for(int i=0;i<=count;i++){double f=i/(double)Math.Max(1,count);int x=(int)Math.Round(x1+(x2-x1)*f),y=(int)Math.Round(y1+(y2-y1)*f);
        for(int oy=-(thickness/2);oy<=thickness/2;oy++)for(int ox=-(thickness/2);ox<=thickness/2;ox++)
        {int px=x+ox,py=y+oy;if(px>=0&&py>=0&&px<w&&py<h)ink[py*w+px]=1;}}
}
double Distance(double x,double y,double a,double b)=>Math.Sqrt((x-a)*(x-a)+(y-b)*(y-b));
double EndpointError(InkLine l,double x1,double y1,double x2,double y2)=>Math.Min(Math.Max(Distance(l.X1,l.Y1,x1,y1),Distance(l.X2,l.Y2,x2,y2)),Math.Max(Distance(l.X1,l.Y1,x2,y2),Distance(l.X2,l.Y2,x1,y1)));
foreach(double degrees in new double[]{0,3.7,10,17.3,20,30,42.7,45,60,73.2,89.4,90,105,123.4,135,151.6,170,179})
foreach(int thickness in new[]{1,3})
{
    int w=256,h=256;var ink=new byte[w*h];double a=degrees*Math.PI/180,x1=128-90*Math.Cos(a),y1=128-90*Math.Sin(a),x2=128+90*Math.Cos(a),y2=128+90*Math.Sin(a);
    Draw(ink,w,h,x1,y1,x2,y2,thickness);
    var result=InkLineDetector.Detect(ink,w,h,60,32);double error=result.Lines.Select(l=>EndpointError(l,x1,y1,x2,y2)).DefaultIfEmpty(999).Min();
    rows.Add(new{degrees,thickness,error,result.Milliseconds,count=result.Lines.Count});
    Check(error<=3.2,$"line endpoints {degrees} degrees thickness {thickness}: {error:F2}px");
}
{
    int w=512,h=512;var ink=new byte[w*h];Draw(ink,w,h,40,40,450,450);Draw(ink,w,h,40,450,450,40);
    // Sparse deterministic specks and short text-like fragments should not invent long strokes.
    var random=new Random(471);for(int i=0;i<400;i++)ink[random.Next(ink.Length)]=1;
    for(int i=0;i<14;i++)Draw(ink,w,h,30+i*30,230,35+i*30,237);
    var result=InkLineDetector.Detect(ink,w,h,100,32);
    File.WriteAllText(Path.Combine(output,"crossing.json"),JsonSerializer.Serialize(result));
    Check(result.Lines.Any(l=>EndpointError(l,40,40,450,450)<4)&&result.Lines.Any(l=>EndpointError(l,40,450,450,40)<4),"crossing and text clutter retain both long lines");
    var again=InkLineDetector.Detect(ink,w,h,100,32);Check(result.Lines.SequenceEqual(again.Lines),"deterministic proposals");
}
{
    int w=320,h=240;var ink=new byte[w*h];Draw(ink,w,h,20,80,290,80,3);Draw(ink,w,h,20,88,290,88,3);
    var result=InkLineDetector.Detect(ink,w,h,100,32);
    Check(result.Lines.Any(l=>EndpointError(l,20,80,290,80)<3)&&result.Lines.Any(l=>EndpointError(l,20,88,290,88)<3),"near parallel physical lines stay separate");
}
{
    int w=360,h=240;var ink=new byte[w*h];Draw(ink,w,h,20,80,140,80);Draw(ink,w,h,190,80,320,80);
    var result=InkLineDetector.Detect(ink,w,h,50,32);
    Check(result.Lines.All(l=>l.LengthPixels<150),"large blank gaps are never bridged into a source line");
}
Check(InkLineDetector.Detect(new byte[256*256],256,256).Lines.Count==0,"blank page yields no geometry");
{
    using var source=new CancellationTokenSource();source.Cancel();bool cancelled=false;try{InkLineDetector.Detect(new byte[256*256],256,256,token:source.Token);}catch(OperationCanceledException){cancelled=true;}
    Check(cancelled,"cancellation stops detection");
}
File.WriteAllText(Path.Combine(output,"result.json"),JsonSerializer.Serialize(new{success=true,passed,rows},new JsonSerializerOptions{WriteIndented=true}));
