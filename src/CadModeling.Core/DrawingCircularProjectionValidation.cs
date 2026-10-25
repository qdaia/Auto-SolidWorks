using CadModeling.Ir;

namespace CadModeling.Core;

/// <summary>Checks explicitly declared cross-view correspondences against actual source ink.
/// This is not automatic semantic matching and does not infer dimensions from pixel scale.</summary>
public static class DrawingCircularProjectionValidation
{
    public const string Revision = "circular-projection-1";

    public static DrawingGeometryAnchor? CircularTarget(DrawingPlanContext context, DrawingGeometryAnchor? target)
    {
        if(target is null)return null;
        if(target.Kind is DrawingAnchorKind.Circle or DrawingAnchorKind.Arc)return target;
        if(target.Kind!=DrawingAnchorKind.Line)return null;
        var links=context.CircularProjections.Where(p=>p.SilhouetteAnchorIds.Contains(target.Id)).ToArray();
        if(links.Length!=1)return null;
        return context.GeometryAnchors.FirstOrDefault(a=>a.Id==links[0].CircleAnchorId&&a.Kind==DrawingAnchorKind.Circle&&a.FeatureId==target.FeatureId&&a.ViewId!=target.ViewId);
    }

    public static IReadOnlyList<ModelingDiagnostic> Validate(DrawingPlanContext context)
    {
        var errors=new List<ModelingDiagnostic>();
        void Error(string id,string message)=>errors.Add(new("CIRCULAR_PROJECTION_INVALID",DiagnosticSeverity.Error,message,"drawing_context.circular_projections."+id));
        if(context.CircularProjections.Count==0)return errors;
        if(context.CircularProjections.Count>128){Error("","At most 128 explicit circular projections are supported.");return errors;}
        DrawingOmissionInventory inventory;
        try
        {
            var evidence=SourceWorkflow.Evidence(context)??throw new ArgumentException("Source evidence required.");
            inventory=DrawingOmissionValidation.Load(evidence.InventoryPath,evidence.InventorySha256);
            if(!string.Equals(Path.GetFullPath(inventory.SourcePath),Path.GetFullPath(context.SourcePath),StringComparison.OrdinalIgnoreCase))throw new ArgumentException("Source identity differs.");
        }
        catch(Exception e)when(e is IOException or InvalidDataException or ArgumentException or UnauthorizedAccessException or System.Text.Json.JsonException){Error("",e.Message);return errors;}
        var anchors=context.GeometryAnchors.GroupBy(a=>a.Id).ToDictionary(g=>g.Key,g=>g.First(),StringComparer.Ordinal);
        var usedIds=new HashSet<string>(StringComparer.Ordinal);var usedSilhouettes=new HashSet<string>(StringComparer.Ordinal);
        var pages=new Dictionary<int,(int W,int H,byte[] Bits)>();var axes=new Dictionary<string,Vector3>(StringComparer.Ordinal);
        foreach(var link in context.CircularProjections)
        {
            if(string.IsNullOrWhiteSpace(link.Id)||!usedIds.Add(link.Id)||link.State!=DrawingReviewState.Reviewed||string.IsNullOrWhiteSpace(link.Evidence)||
                link.AxisDirection is null||!ModelVerification.Finite(link.AxisDirection)||ModelVerification.Norm(link.AxisDirection)<1e-9||
                link.SilhouetteAnchorIds.Count!=2||link.SilhouetteAnchorIds.Distinct(StringComparer.Ordinal).Count()!=2||
                !anchors.TryGetValue(link.CircleAnchorId,out var circle)||circle.Kind!=DrawingAnchorKind.Circle||circle.Construction is not null||circle.State!=DrawingReviewState.Reviewed)
            {Error(link.Id,"Declare a reviewed circle, two distinct silhouette lines, physical axis and explicit cross-view evidence.");continue;}
            var pair=link.SilhouetteAnchorIds.Select(id=>anchors.GetValueOrDefault(id)).ToArray();
            if(pair.Any(a=>a is null||a.Kind!=DrawingAnchorKind.Line||a.Construction is not null||a.State!=DrawingReviewState.Reviewed||a.FeatureId!=circle.FeatureId||a.ViewId==circle.ViewId)||
                pair[0]!.ViewId!=pair[1]!.ViewId||pair[0]!.PhysicalEntityId==pair[1]!.PhysicalEntityId||link.SilhouetteAnchorIds.Any(id=>!usedSilhouettes.Add(id)))
            {Error(link.Id,"Silhouette lines must be distinct real contours in one other view of the same feature and belong to one correspondence.");continue;}
            var direction=ModelVerification.Unit(link.AxisDirection);
            if(axes.TryGetValue(circle.PhysicalEntityId,out var previous)&&Math.Abs(ModelVerification.Dot(direction,previous))<1-1e-7)Error(link.Id,"The same circular entity has conflicting model axes across views.");
            else axes.TryAdd(circle.PhysicalEntityId,direction);
            bool Pixels(DrawingGeometryAnchor a,out DrawingPagePoint[] p)
            {
                p=[];var view=context.Views.FirstOrDefault(v=>v.Id==a.ViewId);if(view is null)return false;
                if(!pages.TryGetValue(view.PageNumber,out var page))
                {
                    var region=inventory.Regions.FirstOrDefault(r=>r.PageNumber==view.PageNumber&&r.Purpose=="overview");
                    if(region?.InkMaskPath is null||region.PixelWidth<2||region.PixelHeight<2)return false;
                    var bytes=File.ReadAllBytes(region.InkMaskPath);
                    if(bytes.LongLength!=((long)region.PixelWidth*region.PixelHeight+7)/8)return false;
                    page=(region.PixelWidth,region.PixelHeight,bytes);pages[view.PageNumber]=page;
                }
                if(a.SourcePath.Count is <2 or >128||a.SourcePath.Any(q=>!double.IsFinite(q.X)||!double.IsFinite(q.Y)||q.X<0||q.X>1||q.Y<0||q.Y>1||q.X<a.Bounds.Left-3d/page.W||q.X>a.Bounds.Right+3d/page.W||q.Y<a.Bounds.Top-3d/page.H||q.Y>a.Bounds.Bottom+3d/page.H))return false;
                p=a.SourcePath.Select(q=>new DrawingPagePoint(q.X*page.W,q.Y*page.H)).ToArray();int tested=0,hits=0;
                for(int i=1;i<p.Length;i++)
                {
                    int steps=Math.Clamp((int)Math.Ceiling(Distance(p[i-1],p[i])),1,4096);
                    for(int k=0;k<=steps;k++)
                    {
                        int x=(int)Math.Round(p[i-1].X+(p[i].X-p[i-1].X)*k/steps),y=(int)Math.Round(p[i-1].Y+(p[i].Y-p[i-1].Y)*k/steps);bool ink=false;tested++;
                        for(int yy=Math.Max(0,y-2);yy<=Math.Min(page.H-1,y+2)&&!ink;yy++)for(int xx=Math.Max(0,x-2);xx<=Math.Min(page.W-1,x+2);xx++){long n=(long)yy*page.W+xx;if((page.Bits[n/8]&(1<<(int)(n%8)))!=0){ink=true;break;}}
                        if(ink)hits++;
                    }
                }
                return tested>=3&&hits/(double)tested>=.6;
            }
            if(!Pixels(circle,out var cp)||!Pixels(pair[0]!,out var p0)||!Pixels(pair[1]!,out var p1))
            {Error(link.Id,"Every linked contour must retain a bounded path supported by its own source-page ink.");continue;}
            if(cp.Length<9||Distance(cp[0],cp[^1])>3){Error(link.Id,"Circular reference must be a closed observed circle, not a partial arc or line.");continue;}
            double cx=(cp.Max(p=>p.X)+cp.Min(p=>p.X))/2,cy=(cp.Max(p=>p.Y)+cp.Min(p=>p.Y))/2;
            double radius=cp.Take(cp.Length-1).Average(p=>double.Hypot(p.X-cx,p.Y-cy));
            if(radius<4||cp.Any(p=>Math.Abs(double.Hypot(p.X-cx,p.Y-cy)-radius)>Math.Max(3,radius*.03)))Error(link.Id,"Circular reference is not circular in source pixels; use a separate supported projection contract for ellipses.");
            var u=new DrawingPagePoint(p0[^1].X-p0[0].X,p0[^1].Y-p0[0].Y);var v=new DrawingPagePoint(p1[^1].X-p1[0].X,p1[^1].Y-p1[0].Y);
            double lu=double.Hypot(u.X,u.Y),lv=double.Hypot(v.X,v.Y);
            double Offset(DrawingPagePoint p,DrawingPagePoint a,DrawingPagePoint d,double length)=>Math.Abs((p.X-a.X)*d.Y-(p.Y-a.Y)*d.X)/Math.Max(length,1e-9);
            if(lu<4||lv<4||Math.Abs(u.X*v.Y-u.Y*v.X)/Math.Max(lu*lv,1e-9)>.03||p0.Any(p=>Offset(p,p0[0],u,lu)>3)||p1.Any(p=>Offset(p,p1[0],v,lv)>3)||Offset(p1[0],p0[0],u,lu)<4)
                Error(link.Id,"Axial silhouettes must be two separated, parallel straight source contours.");
            double Project(DrawingPagePoint p)=>(p.X-p0[0].X)*u.X/Math.Max(lu,1e-9)+(p.Y-p0[0].Y)*u.Y/Math.Max(lu,1e-9);
            double overlap=Math.Min(lu,Math.Max(Project(p1[0]),Project(p1[^1])))-Math.Max(0,Math.Min(Project(p1[0]),Project(p1[^1])));
            if(overlap<Math.Min(lu,lv)*.5)Error(link.Id,"Silhouette axial extents do not overlap; do not link neighboring stepped cylinders by equal diameter text alone.");
        }
        return errors;
    }
    private static double Distance(DrawingPagePoint a,DrawingPagePoint b)=>double.Hypot(a.X-b.X,a.Y-b.Y);
}
