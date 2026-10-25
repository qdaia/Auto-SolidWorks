using System.Collections.Concurrent;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CadModeling.Core;
using CadModeling.Drawing.Ingestion;
using CadModeling.Ir;
using ModelContextProtocol.Server;

[McpServerToolType]
public sealed class SourceCircleTools
{
    private static readonly ConcurrentDictionary<string,InkCircleDetection> Cache=new();
    [McpServerTool(Name="cad_find_source_circles",ReadOnly=false,Destructive=false,Idempotent=false,OpenWorld=false,UseStructuredContent=true)]
    [Description("在绑定工作区frameId的原图墨迹中提议圆/圆弧的像素圆心、半径和实际有墨迹支持的角度区间。保留同心候选，不把半圆补成整圆，不读取尺寸、不判断孔/凸台/文字归属、不自动Reviewed。返回源图、仅绘制观测圆弧的叠图和原页坐标。OpenCV EDCircles及原像素拟合校验；不启动SolidWorks。")]
    public static object Find(CompactArtifactStore artifacts,string workspaceId,string frameId,double minimumRadiusPixels=8,int maximumCircles=64,CancellationToken cancellationToken=default)
    {
        if(!double.IsFinite(minimumRadiusPixels)||minimumRadiusPixels<4||maximumCircles<1||maximumCircles>128)throw new ArgumentException("minimumRadiusPixels >= 4 and maximumCircles 1..128 required.");
        var w=SourceBindingTools.Workspace(artifacts,workspaceId);
        var frame=w.Frames.SingleOrDefault(f=>f.Id==frameId)??throw new ArgumentException("Unknown frameId.");
        if(frame.Width<3||frame.Height<3||(long)frame.Width*frame.Height>16_000_000)throw new ArgumentException("Select a frame at least 3x3 and at most 16 million pixels.");
        var inventory=DrawingOmissionValidation.Load(w.InventoryPath,w.InventorySha256);
        var page=inventory.Regions.SingleOrDefault(r=>r.PageNumber==frame.PageNumber&&r.Purpose=="overview")??throw new ArgumentException("Missing source overview.");
        if(page.InkMaskPath is null||page.InkMaskSha256 is null||page.InkMaskEncoding!="bitset-row-major-lsb0-v1")throw new ArgumentException("Source ink mask unavailable.");
        if(!string.Equals(BindingWorkspaceImages.Hash(page.InkMaskPath),page.InkMaskSha256,StringComparison.OrdinalIgnoreCase)||frame.PageWidth!=page.PixelWidth||frame.PageHeight!=page.PixelHeight)throw new InvalidDataException("Source mask identity mismatch.");
        string key=string.Join("|",InkCircleDetector.Revision,page.InkMaskSha256,frame.Left,frame.Top,frame.Width,frame.Height,minimumRadiusPixels.ToString("R",System.Globalization.CultureInfo.InvariantCulture),maximumCircles);
        bool cacheHit=Cache.TryGetValue(key,out var detection);
        cancellationToken.ThrowIfCancellationRequested();
        if(!cacheHit)
        {
            var packed=File.ReadAllBytes(page.InkMaskPath);
            if(packed.LongLength!=((long)frame.PageWidth*frame.PageHeight+7)/8)throw new InvalidDataException("Invalid ink mask length.");
            var ink=new byte[frame.Width*frame.Height];
            for(int y=0;y<frame.Height;y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                for(int x=0;x<frame.Width;x++){int index=(frame.Top+y)*frame.PageWidth+frame.Left+x;ink[y*frame.Width+x]=(byte)((packed[index/8]>>(index%8))&1);}
            }
            detection=InkCircleDetector.Detect(ink,frame.Width,frame.Height,minimumRadiusPixels,maximumCircles,cancellationToken);
            if(Cache.Count<32)Cache.TryAdd(key,detection);
        }
        var circles=detection!.Circles.Select((c,index)=>new {
            id="C"+(index+1),frame_center=new DrawingPagePoint(c.CenterX,c.CenterY),source_center=new DrawingPagePoint((frame.Left+c.CenterX)/frame.PageWidth,(frame.Top+c.CenterY)/frame.PageHeight),
            c.RadiusPixels,source_radius_x=c.RadiusPixels/frame.PageWidth,source_radius_y=c.RadiusPixels/frame.PageHeight,c.SupportFraction,c.RadialErrorPixels,
            observed_arcs=c.ObservedArcs.Select(a=>new {a.StartDegrees,a.SweepDegrees,
                sampled_frame_path=Points(c,a,frame),sampled_source_path=Points(c,a,frame).Select(p=>new DrawingPagePoint((frame.Left+p.X)/frame.PageWidth,(frame.Top+p.Y)/frame.PageHeight)).ToArray()}).ToArray(),
            state="Unresolved",is_hole=(bool?)null,dimension_value=(double?)null
        }).ToArray();
        var directory=BindingWorkspaceImages.NewDirectory(Path.Combine(Path.GetDirectoryName(frame.ImagePath)!,"circles-"+Guid.NewGuid().ToString("N")));
        var overlay=Path.Combine(directory,"circle-proposals.png");
        BindingWorkspaceImages.Sta(()=>
        {
            var bitmap=BindingWorkspaceImages.Read(frame.ImagePath);var visual=new DrawingVisual();
            using(var dc=visual.RenderOpen())
            {
                dc.DrawImage(bitmap,new Rect(0,0,frame.Width,frame.Height));
                for(int i=0;i<detection.Circles.Count;i++)
                {
                    var c=detection.Circles[i];
                    foreach(var arc in c.ObservedArcs)
                    {
                        var points=Points(c,arc,frame);
                        for(int j=1;j<points.Length;j++)dc.DrawLine(new Pen(Brushes.OrangeRed,1),new(points[j-1].X,points[j-1].Y),new(points[j].X,points[j].Y));
                    }
                    dc.DrawLine(new Pen(Brushes.DodgerBlue,1),new(c.CenterX-3,c.CenterY),new(c.CenterX+3,c.CenterY));
                    dc.DrawLine(new Pen(Brushes.DodgerBlue,1),new(c.CenterX,c.CenterY-3),new(c.CenterX,c.CenterY+3));
                    BindingWorkspaceImages.Label(dc,"C"+(i+1),c.CenterX+4,c.CenterY+4);
                }
            }
            var target=new RenderTargetBitmap(frame.Width,frame.Height,96,96,PixelFormats.Pbgra32);target.Render(visual);BindingWorkspaceImages.Save(overlay,target);return true;
        });
        var result=new {revision=InkCircleDetector.Revision,workspace_id=workspaceId,frame_id=frameId,source_sha256=w.SourceSha256,source_image_path=frame.ImagePath,overlay_path=overlay,overlay_sha256=BindingWorkspaceImages.Hash(overlay),cache_hit=cacheHit,
            detection_milliseconds=detection.Milliseconds,engine="OpenCV EdgeDrawing/EDCircles + source-ink circle fit",engine_version=detection.EngineVersion,raw_circle_count=detection.RawCircleCount,segment_circle_count=detection.SegmentCircleCount,fragment_circle_count=detection.FragmentCircleCount,dash_circle_count=detection.DashCircleCount,excluded_ellipse_count=detection.ExcludedEllipseCount,
            circle_count=circles.Length,circles_truncated=detection.Truncated,circles,
            coordinates="Frame center/radius/path use unscaled source pixels. Source center/path are original-page normalized. source_radius_x and source_radius_y differ on nonsquare pages. Angles are clockwise from image +X; sweeps may cross 360 degrees.",
            semantic_review_complete=false,native_execution=false,image_engine_native=true,
            limitations=new[]{"Circle/arc proposals can be dimension graphics or text, not necessarily holes or part edges.","Observed intervals describe ink support within raster tolerance; they do not prove arc endpoints, continuity, or design intent.","Ellipses, very short arcs and centers outside the crop are excluded; overlapping strokes may bias a fit.","Native image detection checks cancellation before/after each native call; it cannot interrupt OpenCV mid-call."}};
        var receipt=artifacts.SaveResult(result);return new {artifact_id=receipt.ArtifactId,artifact_path=receipt.Path,result};
    }
    private static DrawingPagePoint[] Points(InkCircle c,InkArcRange arc,BindingFrame frame)
    {
        int count=Math.Clamp((int)Math.Ceiling(c.RadiusPixels*arc.SweepDegrees*Math.PI/180/3)+1,2,129);
        return Enumerable.Range(0,count).Select(i=>
        {double angle=(arc.StartDegrees+arc.SweepDegrees*i/(count-1d))*Math.PI/180;return new DrawingPagePoint(c.CenterX+c.RadiusPixels*Math.Cos(angle),c.CenterY+c.RadiusPixels*Math.Sin(angle));})
            .Where(p=>p.X>=0&&p.Y>=0&&p.X<frame.Width&&p.Y<frame.Height).ToArray();
    }
}
