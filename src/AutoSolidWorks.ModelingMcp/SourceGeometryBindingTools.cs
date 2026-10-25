using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using CadModeling.Core;
using CadModeling.Ir;
using ModelContextProtocol.Server;

public sealed record SourceGeometrySelection
{
    public required string ProposalId{get;init;}
    public required string Id{get;init;}
    public required string PhysicalEntityId{get;init;}
    public DrawingAnchorKind? Kind{get;init;}
    public int? ArcIndex{get;init;}
    public DrawingReviewState State{get;init;}=DrawingReviewState.Unresolved;
    public required string Evidence{get;init;}
}

[McpServerToolType]
public sealed class SourceGeometryBindingTools
{
    public const string Revision="source-geometry-selection-1";
    private static readonly JsonSerializerOptions Strict=new(ModelingIrJson.Options){UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow};

    [McpServerTool(Name="cad_bind_source_geometry",ReadOnly=false,Destructive=false,Idempotent=true,OpenWorld=false,UseStructuredContent=true)]
    [Description("将AI明确选中的直线或圆弧候选绑定为已有draft/drawing_context中的源锚点，免抄坐标和路径。selections=[{proposal_id,id,physical_entity_id,evidence,state?,kind?,arc_index?}]，1..64项；state默认Unresolved，只有AI明确提供Reviewed才保留该状态。多段圆弧必须指定从0开始的arc_index，不能自动闭合虚线。复核原图后选择真实对象；工具不识字、不判断孔/轮廓/文字、不猜尺寸。验证候选所属工作区、源图和墨迹，选择覆盖路径的同页几何证据，返回不可变新input_id；后续仍需尺寸附着与完整编译。不启动SolidWorks。")]
    public static object Bind(CompactArtifactStore artifacts,string baseInputId,string kind,string workspaceId,string proposalArtifactId,string viewId,string featureId,string datum,JsonElement selections)
    {
        if(kind is not("draft" or "drawing_context"))throw new ArgumentException("kind must be draft or drawing_context.");
        var selected=selections.Deserialize<SourceGeometrySelection[]>(Strict)??throw new ArgumentException("Selections required.");
        if(selected.Length is <1 or >64||selected.Any(s=>s is null))throw new ArgumentException("Select 1..64 geometry proposals.");
        if(selected.Select(s=>s.Id).Distinct(StringComparer.Ordinal).Count()!=selected.Length)throw new ArgumentException("Anchor IDs must be unique.");
        foreach(var s in selected)
        {
            if(!ValidId(s.Id)||!ValidId(s.PhysicalEntityId)||!ValidId(s.ProposalId)||string.IsNullOrWhiteSpace(s.Evidence)||s.Evidence.Length>4096)
                throw new ArgumentException("Each selection needs bounded IDs and explicit source evidence.");
            if(s.State is not(DrawingReviewState.Unresolved or DrawingReviewState.Reviewed))throw new ArgumentException("Selection state must be Unresolved or explicitly Reviewed.");
        }
        var workspace=SourceBindingTools.Workspace(artifacts,workspaceId);
        var proposal=artifacts.LoadResult(proposalArtifactId);
        if(proposal["workspace_id"]?.GetValue<string>()!=workspaceId||proposal["source_sha256"]?.GetValue<string>()!=workspace.SourceSha256)
            throw new ArgumentException("Geometry proposal and workspace source identity differ.");
        var frame=workspace.Frames.SingleOrDefault(f=>f.Id==proposal["frame_id"]?.GetValue<string>())??throw new ArgumentException("Proposal frame missing from workspace.");
        if(!BindingWorkspaceImages.SamePath(proposal["source_image_path"]?.GetValue<string>()??"",frame.ImagePath))throw new ArgumentException("Proposal source image differs from the frame.");
        var inventory=DrawingOmissionValidation.Load(workspace.InventoryPath,workspace.InventorySha256);
        var page=inventory.Regions.SingleOrDefault(p=>p.PageNumber==frame.PageNumber&&p.Purpose=="overview")??throw new ArgumentException("Source overview unavailable.");
        if(page.InkMaskPath is null||page.InkMaskSha256 is null||page.InkMaskEncoding!="bitset-row-major-lsb0-v1"||
            !string.Equals(BindingWorkspaceImages.Hash(page.InkMaskPath),page.InkMaskSha256,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Source ink identity mismatch.");
        var packed=File.ReadAllBytes(page.InkMaskPath);
        if(page.PixelWidth!=frame.PageWidth||page.PixelHeight!=frame.PageHeight||packed.LongLength!=((long)frame.PageWidth*frame.PageHeight+7)/8)throw new InvalidDataException("Source ink dimensions mismatch.");
        bool lines=proposal["revision"]?.GetValue<string>()==CadModeling.Drawing.Ingestion.InkLineDetector.Revision&&proposal["lines"] is JsonArray;
        bool circles=proposal["revision"]?.GetValue<string>()==CadModeling.Drawing.Ingestion.InkCircleDetector.Revision&&proposal["circles"] is JsonArray;
        if(lines==circles)throw new ArgumentException("Expected a supported line or circle proposal receipt.");
        var candidates=(JsonArray)proposal[lines?"lines":"circles"]!;
        var anchors=new List<BindingAnchorInput>();var receipts=new List<object>();
        foreach(var selection in selected)
        {
            var matches=candidates.Where(p=>p?["id"]?.GetValue<string>()==selection.ProposalId).ToArray();
            if(matches.Length!=1)throw new ArgumentException("Unknown or ambiguous proposal: "+selection.ProposalId);
            var item=matches[0]!;DrawingPagePoint[] path;DrawingAnchorKind anchorKind;
            if(lines)
            {
                if(selection.ArcIndex is not null||selection.Kind is not(null or DrawingAnchorKind.Line or DrawingAnchorKind.Axis or DrawingAnchorKind.Plane))throw new ArgumentException("Line selections allow Line, Axis or Plane and no arc_index.");
                path=Points(item["frame_path"]);anchorKind=selection.Kind??DrawingAnchorKind.Line;
                if(path.Length!=2)throw new ArgumentException("Line proposal needs two endpoints.");
            }
            else
            {
                var arcs=item["observed_arcs"] as JsonArray??throw new ArgumentException("Observed circle intervals missing.");
                if(arcs.Count==0||selection.ArcIndex is null&&arcs.Count!=1)throw new ArgumentException("Select exactly one observed interval with arc_index; do not join gaps.");
                int index=selection.ArcIndex??0;if(index<0||index>=arcs.Count)throw new ArgumentException("Invalid arc_index.");
                var arc=arcs[index]!;path=Points(arc["sampled_frame_path"]);
                bool closed=arcs.Count==1&&arc["sweep_degrees"]!.GetValue<double>()==360&&path.Length>=3&&Distance(path[0],path[^1])<.01;
                anchorKind=closed?DrawingAnchorKind.Circle:DrawingAnchorKind.Arc;
                if(selection.Kind is {} requested&&requested!=anchorKind)throw new ArgumentException("Circle/arc kind must preserve the observed interval; incomplete arcs cannot become circles.");
                if(closed)
                {
                    var center=item["frame_center"]!.Deserialize<DrawingPagePoint>(Strict)!;double radius=item["radius_pixels"]!.GetValue<double>();
                    if(center.X-radius<0||center.Y-radius<0||center.X+radius>frame.Width||center.Y+radius>frame.Height)throw new ArgumentException("Clipped circle cannot become a closed source anchor.");
                }
            }
            if(path.Length is <2 or >129)throw new ArgumentException("Source path must contain 2..129 proposal points.");
            // Existing binding contracts permit at most 128 points. Preserve both endpoints;
            // dropping one interior sample does not close an open arc or bridge separate intervals.
            if(path.Length==129)path=Enumerable.Range(0,128).Select(i=>path[(int)Math.Round(i*128d/127)]).ToArray();
            foreach(var point in path)BindingWorkspaceImages.Point(point,frame);
            var samples=Sample(path,256);double supported=samples.Count(p=>InkNear(p,frame,packed))/ (double)samples.Count;
            if(supported<.85)throw new ArgumentException("Selected source path lacks pixel support; inspect/correct the candidate before binding.");
            var bounds=new DrawingReviewBox(Math.Max(0,path.Min(p=>p.X)-2),Math.Max(0,path.Min(p=>p.Y)-2),Math.Min(frame.Width,path.Max(p=>p.X)+2),Math.Min(frame.Height,path.Max(p=>p.Y)+2));
            var normalized=BindingWorkspaceImages.Box(bounds,frame);
            var eligible=inventory.Candidates.Where(c=>c.PageNumber==frame.PageNumber&&c.Kind is DrawingCandidateKind.Geometry or DrawingCandidateKind.ResidualInk&&Overlap(c.Bounds,normalized)).ToArray();
            var normalizedSamples=samples.Select(p=>BindingWorkspaceImages.Point(p,frame)).ToArray();var covered=new bool[samples.Count];var evidenceIds=new List<string>();
            // A bounded greedy cover chooses source evidence geometrically. It does not
            // assign a semantic role, and it avoids attaching every nearby observation.
            for(int step=0;step<8&&covered.Count(v=>v)<Math.Ceiling(covered.Length*.9);step++)
            {
                var best=eligible.Where(c=>!evidenceIds.Contains(c.Id)).Select(c=>new{Candidate=c,Added=normalizedSamples.Where((p,i)=>!covered[i]&&Inside(p,c.Bounds,frame)).Count()})
                    .OrderByDescending(c=>c.Added).ThenBy(c=>(c.Candidate.Bounds.Right-c.Candidate.Bounds.Left)*(c.Candidate.Bounds.Bottom-c.Candidate.Bounds.Top)).ThenBy(c=>c.Candidate.Id,StringComparer.Ordinal).FirstOrDefault();
                if(best is null||best.Added==0)break;evidenceIds.Add(best.Candidate.Id);
                for(int i=0;i<covered.Length;i++)covered[i]|=Inside(normalizedSamples[i],best.Candidate.Bounds,frame);
            }
            if(evidenceIds.Count==0||covered.Count(v=>v)<Math.Ceiling(covered.Length*.9))throw new ArgumentException("Selected geometry lacks a bounded source-evidence cover; inspect the inventory rather than borrowing nearby text evidence.");
            anchors.Add(new(){Id=selection.Id,PhysicalEntityId=selection.PhysicalEntityId,Kind=anchorKind,Bounds=bounds,SourcePath=path,State=selection.State,CandidateIds=evidenceIds,Evidence=selection.Evidence});
            receipts.Add(new{anchor_id=selection.Id,proposal_id=selection.ProposalId,selection.ArcIndex,kind=anchorKind,state=selection.State,path_points=path.Length,candidate_ids=evidenceIds,source_support_fraction=supported,evidence_path_coverage=covered.Count(v=>v)/(double)covered.Length});
        }
        var batch=new BindingBatch{FrameId=frame.Id,ViewId=viewId,FeatureId=featureId,Datum=datum,Anchors=anchors};
        var bound=SourceBindingTools.Bind(artifacts,baseInputId,kind,workspaceId,JsonSerializer.SerializeToElement(batch,ModelingIrJson.Options));
        var input=JsonSerializer.SerializeToNode(bound,ModelingIrJson.Options)!;
        return new{revision=Revision,input_id=input["input_id"]!.GetValue<string>(),input_sha256=input["input_sha256"]?.GetValue<string>(),input_path=input["input_path"]?.GetValue<string>(),base_input_id=baseInputId,
            workspace_id=workspaceId,proposal_artifact_id=proposalArtifactId,frame_id=frame.Id,anchors=receipts,anchor_count=anchors.Count,coordinate_values_copied_by_caller=0,
            status="stored_not_validated",compiled=false,semantic_review_automatic=false,native_execution=false,
            next="Bind exact source literals and their attachment traces, then run the existing complete compiler. Geometry selection does not establish dimension ownership or model equivalence."};
    }

    private static bool ValidId(string? s)=>!string.IsNullOrWhiteSpace(s)&&s.Length<=256&&!s.Any(char.IsControl);
    private static DrawingPagePoint[] Points(JsonNode? n)=>n?.Deserialize<DrawingPagePoint[]>(Strict)??throw new ArgumentException("Proposal path missing.");
    private static double Distance(DrawingPagePoint a,DrawingPagePoint b)=>double.Hypot(a.X-b.X,a.Y-b.Y);
    private static bool Overlap(DrawingReviewBox a,DrawingReviewBox b)=>a.Left<b.Right&&a.Right>b.Left&&a.Top<b.Bottom&&a.Bottom>b.Top;
    private static bool Inside(DrawingPagePoint p,DrawingReviewBox b,BindingFrame f)=>p.X>=b.Left-1d/f.PageWidth&&p.X<=b.Right+1d/f.PageWidth&&p.Y>=b.Top-1d/f.PageHeight&&p.Y<=b.Bottom+1d/f.PageHeight;
    private static List<DrawingPagePoint> Sample(DrawingPagePoint[] path,int limit)
    {
        double[] length=new double[path.Length];for(int i=1;i<path.Length;i++)length[i]=length[i-1]+Distance(path[i-1],path[i]);
        if(length[^1]<2)throw new ArgumentException("Selected geometry is too short.");
        int count=Math.Clamp((int)Math.Ceiling(length[^1])+1,3,limit);var result=new List<DrawingPagePoint>(count);int segment=1;
        for(int i=0;i<count;i++)
        {
            double at=length[^1]*i/(count-1);while(segment<path.Length-1&&length[segment]<at)segment++;
            double span=length[segment]-length[segment-1],t=span<=1e-9?0:(at-length[segment-1])/span;
            result.Add(new(path[segment-1].X+t*(path[segment].X-path[segment-1].X),path[segment-1].Y+t*(path[segment].Y-path[segment-1].Y)));
        }
        return result;
    }
    private static bool InkNear(DrawingPagePoint p,BindingFrame frame,byte[] bits)
    {
        int x=frame.Left+(int)Math.Round(p.X),y=frame.Top+(int)Math.Round(p.Y);
        for(int dy=-2;dy<=2;dy++)for(int dx=-2;dx<=2;dx++)
        {
            int xx=x+dx,yy=y+dy;if(xx<0||yy<0||xx>=frame.PageWidth||yy>=frame.PageHeight)continue;
            int index=yy*frame.PageWidth+xx;if(((bits[index/8]>>(index%8))&1)!=0)return true;
        }
        return false;
    }
}
