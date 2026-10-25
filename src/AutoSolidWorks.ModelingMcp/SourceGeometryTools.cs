using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CadModeling.Core;
using CadModeling.Ir;
using CadModeling.Drawing.Ingestion;
using ModelContextProtocol.Server;

[McpServerToolType]
public sealed class SourceGeometryTools
{
    private static readonly ConcurrentDictionary<string,InkLineDetection> Cache=new();
    [McpServerTool(Name="cad_find_source_geometry",ReadOnly=false,Destructive=false,Idempotent=false,OpenWorld=false,UseStructuredContent=true)]
    [Description("在绑定工作区frameId中提议任意方向的源像素直线及像素坐标，帮助定位斜尺寸线/引线/轮廓。只读取已校验的原图墨迹，不识字、不猜尺寸或归属、不自动Reviewed。返回原像素和原页归一化路径、可视化编号叠图及同页重叠证据候选；AI看图选择实际对象后再填写绑定。不启动SolidWorks。")]
    public static object Find(CompactArtifactStore artifacts,string workspaceId,string frameId,double minimumLengthPixels=24,int maximumLines=64,CancellationToken cancellationToken=default)
    {
        var w=SourceBindingTools.Workspace(artifacts,workspaceId);
        var frame=w.Frames.SingleOrDefault(f=>f.Id==frameId)??throw new ArgumentException("Unknown frameId.");
        var inventory=DrawingOmissionValidation.Load(w.InventoryPath,w.InventorySha256);
        var page=inventory.Regions.SingleOrDefault(r=>r.PageNumber==frame.PageNumber&&r.Purpose=="overview")??throw new ArgumentException("Missing source overview.");
        if(page.InkMaskPath is null||page.InkMaskSha256 is null||page.InkMaskEncoding!="bitset-row-major-lsb0-v1")throw new ArgumentException("Source ink mask unavailable.");
        if(!string.Equals(BindingWorkspaceImages.Hash(page.InkMaskPath),page.InkMaskSha256,StringComparison.OrdinalIgnoreCase)||frame.PageWidth!=page.PixelWidth||frame.PageHeight!=page.PixelHeight)throw new InvalidDataException("Source mask identity mismatch.");
        if(!double.IsFinite(minimumLengthPixels)||minimumLengthPixels<8||maximumLines<1||maximumLines>128)throw new ArgumentException("minimumLengthPixels >= 8 and maximumLines 1..128 required.");
        string key=string.Join("|",InkLineDetector.Revision,page.InkMaskSha256,frame.Left,frame.Top,frame.Width,frame.Height,minimumLengthPixels.ToString("R",System.Globalization.CultureInfo.InvariantCulture),maximumLines);
        bool cacheHit=Cache.TryGetValue(key,out var detection);
        if(!cacheHit)
        {
            if((long)frame.Width*frame.Height>16_000_000)throw new ArgumentException("Select a crop of at most 16 million pixels.");
            var packed=File.ReadAllBytes(page.InkMaskPath);
            if(packed.LongLength!=((long)frame.PageWidth*frame.PageHeight+7)/8)throw new InvalidDataException("Invalid ink mask length.");
            var ink=new byte[frame.Width*frame.Height];
            for(int y=0;y<frame.Height;y++)for(int x=0;x<frame.Width;x++)
            {int index=(frame.Top+y)*frame.PageWidth+frame.Left+x;ink[y*frame.Width+x]=(byte)((packed[index/8]>>(index%8))&1);}
            detection=InkLineDetector.Detect(ink,frame.Width,frame.Height,minimumLengthPixels,maximumLines,cancellationToken);
            if(Cache.Count<32)Cache.TryAdd(key,detection); // Bounded process cache contains geometry proposals only.
        }
        var lines=detection!.Lines.Select((line,index)=>
        {
            var path=new[]{new DrawingPagePoint((frame.Left+line.X1)/frame.PageWidth,(frame.Top+line.Y1)/frame.PageHeight),new DrawingPagePoint((frame.Left+line.X2)/frame.PageWidth,(frame.Top+line.Y2)/frame.PageHeight)};
            var bounds=new DrawingReviewBox(Math.Max(0,Math.Min(path[0].X,path[1].X)-2d/frame.PageWidth),Math.Max(0,Math.Min(path[0].Y,path[1].Y)-2d/frame.PageHeight),Math.Min(1,Math.Max(path[0].X,path[1].X)+2d/frame.PageWidth),Math.Min(1,Math.Max(path[0].Y,path[1].Y)+2d/frame.PageHeight));
            var eligible=inventory.Candidates.Where(c=>c.PageNumber==frame.PageNumber&&c.Kind is DrawingCandidateKind.Geometry or DrawingCandidateKind.ResidualInk&&c.Bounds.Left<=bounds.Right&&c.Bounds.Right>=bounds.Left&&c.Bounds.Top<=bounds.Bottom&&c.Bounds.Bottom>=bounds.Top).ToArray();
            return new {id="L"+(index+1),frame_path=new[]{new DrawingPagePoint(line.X1,line.Y1),new DrawingPagePoint(line.X2,line.Y2)},source_path=path,bounds,line.SupportFraction,line.LengthPixels,
                overlapping_candidate_ids=eligible.Take(8).Select(c=>c.Id).ToArray(),overlapping_candidates_truncated=eligible.Length>8,eligible_candidate_count=eligible.Length,state="Unresolved"};
        }).ToArray();
        var directory=BindingWorkspaceImages.NewDirectory(Path.Combine(Path.GetDirectoryName(frame.ImagePath)!,"geometry-"+Guid.NewGuid().ToString("N")));
        var overlay=Path.Combine(directory,"line-proposals.png");
        BindingWorkspaceImages.Sta(()=>
        {
            var bitmap=BindingWorkspaceImages.Read(frame.ImagePath);var visual=new DrawingVisual();
            using(var dc=visual.RenderOpen())
            {
                dc.DrawImage(bitmap,new Rect(0,0,frame.Width,frame.Height));
                for(int i=0;i<detection.Lines.Count;i++)
                {
                    var l=detection.Lines[i];dc.DrawLine(new Pen(Brushes.OrangeRed,1),new(l.X1,l.Y1),new(l.X2,l.Y2));
                    BindingWorkspaceImages.Label(dc,"L"+(i+1),(l.X1+l.X2)/2,(l.Y1+l.Y2)/2);
                }
            }
            var target=new RenderTargetBitmap(frame.Width,frame.Height,96,96,PixelFormats.Pbgra32);target.Render(visual);BindingWorkspaceImages.Save(overlay,target);return true;
        });
        var result=new {revision=InkLineDetector.Revision,workspace_id=workspaceId,frame_id=frameId,source_sha256=w.SourceSha256,source_image_path=frame.ImagePath,overlay_path=overlay,overlay_sha256=BindingWorkspaceImages.Hash(overlay),cache_hit=cacheHit,
            detection_milliseconds=detection.Milliseconds,detector_cache_hit=cacheHit,foreground_pixels=detection.ForegroundPixels,sampled_pixels=detection.SampledPixels,line_count=lines.Length,lines_truncated=detection.Truncated,lines,
            coordinates="frame_path: unscaled crop pixels; source_path/bounds: original overview normalized coordinates",semantic_review_complete=false,native_execution=false,
            limitations=new[]{"Proposals may include text strokes, hatch, extension or center lines; AI must identify the real object.","Adjacent or intersecting ink can shift endpoints; inspect the original crop before use.","Straight lines only; arcs, circles and semantic recognition remain separate."}};
        var receipt=artifacts.SaveResult(result);return new {artifact_id=receipt.ArtifactId,artifact_path=receipt.Path,result};
    }
}
