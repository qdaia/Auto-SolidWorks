using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CadModeling.Core;
using CadModeling.Ir;

public sealed record BindingErrorRegion(string Kind,string Id,string? ImagePath,int[] DiagnosticIndices,string Status, string? SourceCropPath=null, int? PageNumber=null, int? Left=null,int? Top=null,int? Width=null,int? Height=null,int? PageWidth=null,int? PageHeight=null);
public sealed record BindingErrorReport(string Status,int ErrorCount,int ImageCount,IReadOnlyList<BindingErrorRegion> Regions,
    IReadOnlyList<ModelingDiagnostic> Diagnostics,bool SemanticReviewComplete=false);
public static class BindingErrorImages
{
    public static BindingErrorReport Render(BindingWorkspace w,DrawingPlanContext context,ModelingDiagnostic[] diagnostics,string directory)
    {
        if(diagnostics.Length==0)return new("no_compile_errors",0,0,[],diagnostics);
        var output=BindingWorkspaceImages.NewDirectory(directory);
        return BindingWorkspaceImages.Sta(()=>
        {
            var records=new List<BindingErrorRegion>();var used=new HashSet<int>();var imageCount=0;
            // Group by exact source identity; matching numeric values never combines errors.
            foreach(var item in context.Dimensions.Select(d=>(Kind:"dimension",Id:d.Id,View:d.BindingEvidence?.ViewId,Fact:(DrawingDimensionFact?)d,Anchor:(DrawingGeometryAnchor?)null))
                .Concat(context.GeometryAnchors.Select(a=>(Kind:"anchor",Id:a.Id,View:(string?)a.ViewId,Fact:(DrawingDimensionFact?)null,Anchor:(DrawingGeometryAnchor?)a))))
            {
                var prefix="drawing_context."+(item.Kind=="dimension"?"dimensions":"geometry_anchors")+"."+item.Id;
                var indices=diagnostics.Select((d,i)=>(d,i)).Where(x=>x.d.Path==prefix||x.d.Path?.StartsWith(prefix+".",StringComparison.Ordinal)==true).Select(x=>x.i).ToArray();
                if(indices.Length==0)continue;
                foreach(var i in indices)used.Add(i);
                var page=context.Views.FirstOrDefault(v=>v.Id==item.View)?.PageNumber;
                var frame=w.Frames.FirstOrDefault(f=>f.PageNumber==page&&f.Left==0&&f.Top==0&&f.Width==f.PageWidth&&f.Height==f.PageHeight);
                if(frame is null||imageCount>=80){records.Add(new(item.Kind,item.Id,null,indices,frame is null?"page_unresolved":"image_limit_reached"));continue;}
                var anchors=item.Anchor is {} a?new[]{a}:context.GeometryAnchors.Where(a=>a.ViewId==item.View&&
                    new[]{item.Fact!.BindingEvidence?.StartAnchorId,item.Fact.BindingEvidence?.EndAnchorId,item.Fact.BindingEvidence?.TargetAnchorId}.Contains(a.Id)).ToArray();
                var trace=item.Fact?.BindingEvidence?.Trace;
                if(item.Fact?.SourceAnnotationId is {} annotationId)
                    trace=context.AnnotationLedger?.Annotations.FirstOrDefault(a=>a.Id==annotationId)?.LeaderTrace??trace;
                var paths=anchors.Select(a=>a.SourcePath).ToList();if(trace is not null){paths.Add(trace.Path);paths.Add(trace.StartExtension);paths.Add(trace.EndExtension);}
                var boxes=anchors.Select(a=>a.Bounds).ToList();if(item.Fact?.BindingEvidence is {} evidence){boxes.Add(evidence.TextBounds);boxes.Add(evidence.TargetBounds);}
                var points=paths.SelectMany(p=>p).Concat(boxes.SelectMany(b=>new[]{new DrawingPagePoint(b.Left,b.Top),new(b.Right,b.Bottom)}))
                    .Where(p=>double.IsFinite(p.X)&&double.IsFinite(p.Y)&&p.X>=0&&p.X<=1&&p.Y>=0&&p.Y<=1).ToArray();
                int left=0,top=0,right=frame.PageWidth,bottom=frame.PageHeight;
                if(points.Length>0){left=Math.Max(0,(int)Math.Floor(points.Min(p=>p.X)*frame.PageWidth)-24);top=Math.Max(0,(int)Math.Floor(points.Min(p=>p.Y)*frame.PageHeight)-24);
                    right=Math.Min(frame.PageWidth,(int)Math.Ceiling(points.Max(p=>p.X)*frame.PageWidth)+24);bottom=Math.Min(frame.PageHeight,(int)Math.Ceiling(points.Max(p=>p.Y)*frame.PageHeight)+24);}
                if(right<=left||bottom<=top){left=top=0;right=frame.PageWidth;bottom=frame.PageHeight;}
                var bitmap=BindingWorkspaceImages.Read(frame.ImagePath);var visual=new DrawingVisual();
                using(var dc=visual.RenderOpen())
                {
                    dc.PushClip(new RectangleGeometry(new Rect(0,0,right-left,bottom-top)));
                    dc.DrawImage(bitmap,new Rect(-left,-top,frame.PageWidth,frame.PageHeight));
                    Point Pixel(DrawingPagePoint p)=>new(p.X*frame.PageWidth-left,p.Y*frame.PageHeight-top);
                    bool Valid(DrawingPagePoint p)=>double.IsFinite(p.X)&&double.IsFinite(p.Y)&&p.X>=0&&p.X<=1&&p.Y>=0&&p.Y<=1;
                    void Line(IReadOnlyList<DrawingPagePoint> path,Brush color)
                    {
                        for(int i=1;i<path.Count;i++)if(Valid(path[i-1])&&Valid(path[i]))dc.DrawLine(new Pen(color,1.8),Pixel(path[i-1]),Pixel(path[i]));
                        foreach(var p in path.Where(Valid))dc.DrawEllipse(Brushes.Red,null,Pixel(p),2,2);
                    }
                    foreach(var anchor in anchors)Line(anchor.SourcePath,Brushes.Green);
                    if(trace is not null){Line(trace.Path,Brushes.OrangeRed);Line(trace.StartExtension,Brushes.Blue);Line(trace.EndExtension,Brushes.Blue);}
                    if(item.Fact?.BindingEvidence is {} e && e.TextBounds is {} b && new[]{b.Left,b.Top,b.Right,b.Bottom}.All(double.IsFinite)&&b.Right>b.Left&&b.Bottom>b.Top)
                        dc.DrawRectangle(null,new Pen(Brushes.Magenta,1),new Rect(b.Left*frame.PageWidth-left,b.Top*frame.PageHeight-top,(b.Right-b.Left)*frame.PageWidth,(b.Bottom-b.Top)*frame.PageHeight));
                    dc.Pop();
                }
                var image=Path.Combine(output,$"issue-{imageCount++:D3}.png");var rendered=new RenderTargetBitmap(right-left,bottom-top,96,96,PixelFormats.Pbgra32);rendered.Render(visual);BindingWorkspaceImages.Save(image,rendered);
                var sourceCrop=Path.ChangeExtension(image,"source.png");
                BindingWorkspaceImages.Save(sourceCrop,new CroppedBitmap(bitmap,new Int32Rect(left,top,right-left,bottom-top)));
                records.Add(new(item.Kind,item.Id,image,indices,"source_geometry_overlay_not_automatic_correction",sourceCrop,page,left,top,right-left,bottom-top,frame.PageWidth,frame.PageHeight));
            }
            var remaining=Enumerable.Range(0,diagnostics.Length).Where(i=>!used.Contains(i)).ToArray();
            if(remaining.Length>0)records.Add(new("unlocalized","",null,remaining,"read_complete_diagnostics"));
            return new BindingErrorReport("compile_errors_require_review",diagnostics.Length,imageCount,records,diagnostics);
        });
    }
}
