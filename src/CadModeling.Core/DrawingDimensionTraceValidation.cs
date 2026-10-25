using CadModeling.Ir;
namespace CadModeling.Core;

/// <summary>Checks source-pixel support and explicit measurement semantics. Never measures millimetres from pixels.</summary>
public static class DrawingDimensionTraceValidation
{
    public const string Revision = "source-trace-4";
    private const double PixelTolerance = 3.0;
    public static IReadOnlyList<ModelingDiagnostic> Validate(DrawingPlanContext context)
    {
        var errors = new List<ModelingDiagnostic>();
        var required = SourceWorkflow.RequiresSourceReview(context.WorkflowRevision);
        if (!required && context.Dimensions.All(f => f.BindingEvidence?.Trace is null)) return errors;
        void Error(string code, string id, string message) => errors.Add(new(code, DiagnosticSeverity.Error, message, "drawing_context.dimensions." + id));
        var critical = context.Features.Where(f => f.Critical).SelectMany(f => f.CriticalParameters).Select(p => p.DimensionId).ToHashSet(StringComparer.Ordinal);
        var anchors = context.GeometryAnchors.GroupBy(a => a.Id).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var masks = new Dictionary<int, InkPage>();
        var pageScales = new Dictionary<int, double>();
        DrawingOmissionInventory? inventory = null;
        try
        {
            if (SourceWorkflow.Evidence(context) is { } review)
            {
                inventory = DrawingOmissionValidation.Load(review.InventoryPath, review.InventorySha256);
                if (!string.Equals(Path.GetFullPath(inventory.SourcePath), Path.GetFullPath(context.SourcePath), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("笔画库存不属于当前原图。");
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or System.Text.Json.JsonException)
        { Error("DIMENSION_TRACE_SOURCE", "", ex.Message); return errors; }
        var priorMeasurements = new Dictionary<(string, string), (DrawingQuantityKind Kind, DrawingMeasurementMode Mode, Vector3 Direction)>();
        foreach (var fact in context.Dimensions)
        {
            var e = fact.BindingEvidence;
            var compound = SourceAnnotationValidation.CompoundAnnotation(context, fact);
            var chamfer = SourceAnnotationValidation.ChamferAnnotation(context, fact);
            var sharedLeader = compound ?? chamfer;
            var traceRequired = required && fact.Status == DrawingFactStatus.Stated && (fact.Critical || critical.Contains(fact.Id)) &&
                e?.QuantityKind is DrawingQuantityKind.Linear or DrawingQuantityKind.Angle or DrawingQuantityKind.Radius or DrawingQuantityKind.Diameter;
            if ((sharedLeader is not null ? sharedLeader.LeaderTrace : e?.Trace) is not { } trace || e is null)
            { if (traceRequired) Error("DIMENSION_TRACE_REQUIRED", fact.Id, "关键源尺寸需要实际引线/箭头/界线路径；不得用相交包围框替代附着证据。"); continue; }
            var pageNumber = context.Views.FirstOrDefault(v => v.Id == e.ViewId)?.PageNumber;
            var region = inventory?.Regions.FirstOrDefault(r => r.PageNumber == pageNumber && r.Purpose == "overview" && r.Bounds == new DrawingReviewBox(0, 0, 1, 1));
            if (pageNumber is null || region?.InkMaskPath is null)
            { Error("DIMENSION_TRACE_INK_REQUIRED", fact.Id, "当前来源缺少完整原页笔画掩码；用新版 cad_read_drawing 重建库存，不复用标记叠加图。"); continue; }
            if (!masks.TryGetValue(pageNumber.Value, out var page))
            {
                if(region.PixelWidth<2 || region.PixelHeight<2)
                { Error("DIMENSION_TRACE_SOURCE", fact.Id, "原页栅格尺寸不足以核对路径。"); continue; }
                try { page = new InkPage(region.PixelWidth, region.PixelHeight, File.ReadAllBytes(region.InkMaskPath)); }
                catch(Exception ex) when(ex is IOException or UnauthorizedAccessException)
                { Error("DIMENSION_TRACE_SOURCE",fact.Id,ex.Message);continue; }
                masks[pageNumber.Value] = page;
            }
            if (!pageScales.TryGetValue(pageNumber.Value, out var xScale))
            {
                try { xScale = Calibrate(context, pageNumber.Value, page); }
                catch (InvalidDataException ex) { Error("DRAWING_PAGE_METRIC", fact.Id, ex.Message); xScale = double.NaN; }
                pageScales[pageNumber.Value] = xScale;
            }
            if (!double.IsFinite(xScale)) continue;
            if (!Enum.IsDefined(trace.Kind) || !Enum.IsDefined(trace.MeasurementMode) || string.IsNullOrWhiteSpace(trace.Evidence) || !ValidPath(trace.Path))
            { Error("DIMENSION_TRACE_SHAPE", fact.Id, "路径需要2至128个有效原页坐标点、明确种类和来源说明。"); continue; }
            if(trace.StartArrowPoint is not null||trace.EndArrowPoint is not null)
            {
                if(trace.Kind!=DrawingDimensionTraceKind.LinearDimension||e.QuantityKind!=DrawingQuantityKind.Linear||!ValidPoint(trace.StartArrowPoint)||!ValidPoint(trace.EndArrowPoint)||
                    page.DistanceToPath(trace.StartArrowPoint!,trace.Path)>PixelTolerance||page.DistanceToPath(trace.EndArrowPoint!,trace.Path)>PixelTolerance||
                    !page.InkAt(trace.StartArrowPoint!)||!page.InkAt(trace.EndArrowPoint!))
                {Error("DIMENSION_ARROW_TIPS",fact.Id,"外置箭头须成对提供真实墨迹支持的端点，位于当前线性尺寸路径上；不能借此移动被测端点。");continue;}
            }
            bool Supported(IReadOnlyList<DrawingPagePoint> path, string label, DrawingReviewBox? ignore = null)
            {
                if (!ValidPath(path)) { Error("DIMENSION_TRACE_SHAPE", fact.Id, label + "路径无效。"); return false; }
                var support = page.Support(path, ignore);
                if (support.Samples < 3 || support.Fraction < .6)
                { Error("DIMENSION_TRACE_INK", fact.Id, $"{label}与原图笔画不符（支持率{support.Fraction:P0}，{support.Samples}个采样）；复查原图坐标及路径，不能放大区域代替。"); return false; }
                return true;
            }
            var constructionIssue = "";
            DrawingGeometryAnchor? Anchor(string? id)
            {
                if (id is null || !anchors.TryGetValue(id, out var a) || a.ViewId != e.ViewId || !ValidPath(a.SourcePath))
                { Error("DIMENSION_TRACE_ANCHOR_PATH", fact.Id, "所选锚点需要同视图的具体源轮廓路径。"); return null; }
                if (a.SourcePath.Any(p => !page.InBox(p, a.Bounds, PixelTolerance)))
                { Error("DIMENSION_TRACE_ANCHOR_PATH", fact.Id, "源轮廓路径超出其锚点区域。"); return null; }
                if (a.Construction is not null)
                {
                    if (!Constructed(a)) { Error("DIMENSION_ANCHOR_CONSTRUCTION", fact.Id, "隐含基准须由同视图、同特征的两个真实直线轮廓证明；检查中轴/交点、有限延伸和支持路径。" + constructionIssue); return null; }
                    return a;
                }
                return Supported(a.SourcePath, "锚点轮廓") ? a : null;
            }
            bool Constructed(DrawingGeometryAnchor a)
            {
                bool Fail(string why){constructionIssue=why;return false;}
                var c=a.Construction!;
                if (!Enum.IsDefined(c.Kind) || string.IsNullOrWhiteSpace(c.Evidence))return Fail("构造类型或说明无效。");
                if(c.Kind==DrawingAnchorConstructionKind.LineExtension)
                {
                    if(c.SupportAnchorIds.Count!=1||!anchors.TryGetValue(c.SupportAnchorIds[0],out var line)||line.Id==a.Id||line.Construction is not null||line.ViewId!=a.ViewId||line.FeatureId!=a.FeatureId||line.State!=DrawingReviewState.Reviewed||line.Kind!=DrawingAnchorKind.Line||a.Kind is not(DrawingAnchorKind.Line or DrawingAnchorKind.Plane)||!ValidPath(line.SourcePath)||line.SourcePath.Any(p=>!page.InBox(p,line.Bounds,PixelTolerance))||!Supported(line.SourcePath,"延伸支持轮廓"))return Fail("有限延伸需要同视图同特征的一条已复核真实直线。");
                    var lineStart=page.Pixels(line.SourcePath[0]);var lineEnd=page.Pixels(line.SourcePath[^1]);var dx=lineEnd.X-lineStart.X;var dy=lineEnd.Y-lineStart.Y;var length=Math.Sqrt(dx*dx+dy*dy);
                    if(length<6||line.SourcePath.Any(x=>page.DistanceToPath(x,[line.SourcePath[0],line.SourcePath[^1]])>PixelTolerance))return Fail("支持轮廓不够长或不共线。");
                    var allowance=Math.Max(12,length*.25);
                    bool OnFiniteExtension(DrawingPagePoint point){var x=page.Pixels(point);var along=((x.X-lineStart.X)*dx+(x.Y-lineStart.Y)*dy)/length;var across=Math.Abs((x.X-lineStart.X)*dy-(x.Y-lineStart.Y)*dx)/length;return across<=PixelTolerance&&along>=-allowance&&along<=length+allowance;}
                    return a.SourcePath.All(OnFiniteExtension)||Fail("声明的平面/直线偏离原轮廓或超出有限延伸范围。");
                }
                if(c.SupportAnchorIds.Count!=2 || c.SupportAnchorIds.Distinct().Count()!=2) return Fail("构造支持ID无效。");
                var supports=new List<DrawingGeometryAnchor>();
                foreach(var id in c.SupportAnchorIds)
                {
                    if (!anchors.TryGetValue(id,out var support) || support.Id==a.Id || support.Construction is not null ||
                        support.ViewId!=a.ViewId || support.FeatureId!=a.FeatureId || support.State!=DrawingReviewState.Reviewed ||
                        support.Kind!=DrawingAnchorKind.Line || !ValidPath(support.SourcePath) ||
                        support.SourcePath.Any(p=>!page.InBox(p,support.Bounds,PixelTolerance)) ||
                        !Supported(support.SourcePath,"构造支持轮廓") || page.Distance(support.SourcePath[0],support.SourcePath[^1])<6 ||
                        support.SourcePath.Any(p=>page.DistanceToPath(p,[support.SourcePath[0],support.SourcePath[^1]])>PixelTolerance)) return Fail("支持轮廓的身份、类型、笔画或直线范围无效："+id);
                    supports.Add(support);
                }
                var p=page.Pixels(supports[0].SourcePath[0]);var q=page.Pixels(supports[1].SourcePath[0]);
                var p1=page.Pixels(supports[0].SourcePath[^1]);var q1=page.Pixels(supports[1].SourcePath[^1]);
                var u=(X:p1.X-p.X,Y:p1.Y-p.Y);var v=(X:q1.X-q.X,Y:q1.Y-q.Y);
                var lu=Math.Sqrt(u.X*u.X+u.Y*u.Y);var lv=Math.Sqrt(v.X*v.X+v.Y*v.Y);
                var cross=u.X*v.Y-u.Y*v.X;
                if(c.Kind==DrawingAnchorConstructionKind.LineIntersection)
                {
                    if(a.Kind!=DrawingAnchorKind.Point || Math.Abs(cross)/(lu*lv)<.1)return false;
                    var t=((q.X-p.X)*v.Y-(q.Y-p.Y)*v.X)/cross;
                    var vertex=page.Normalized(new(p.X+t*u.X,p.Y+t*u.Y));
                    var limit=Math.Min(64,Math.Min(lu,lv)*.5+PixelTolerance);
                    return ValidPoint(vertex) && supports.All(s=>page.DistanceToPath(vertex,s.SourcePath)<=limit) &&
                        a.SourcePath.All(x=>page.Distance(x,vertex)<=PixelTolerance);
                }
                if(a.Kind!=DrawingAnchorKind.Axis || Math.Abs(cross)/(lu*lv)>.01 || page.Distance(a.SourcePath[0],a.SourcePath[^1])<6)return Fail("中轴类型、长度或平行性无效。");
                u=(u.X/lu,u.Y/lu);
                double Projection(DrawingPagePoint x)=>(x.X-p.X)*u.X+(x.Y-p.Y)*u.Y;
                var offset=(q.X-p.X)*(-u.Y)+(q.Y-p.Y)*u.X;
                if(Math.Abs(offset)<6 || Math.Abs(offset)>Math.Min(lu,lv)*4)return Fail("两支持线间距无效。");
                var overlapStart=Math.Max(0,Math.Min(Projection(q),Projection(q1)));
                var overlapEnd=Math.Min(lu,Math.Max(Projection(q),Projection(q1)));
                if(overlapEnd-overlapStart<6)return Fail("两支持线没有足够重叠区间。");
                var extension=Math.Min(32,Math.Min(lu,lv)*.25);
                var validMidline=a.SourcePath.All(x=>
                {
                    var px=page.Pixels(x);var along=Projection(px);var across=(px.X-p.X)*(-u.Y)+(px.Y-p.Y)*u.X;
                    return Math.Abs(across-offset/2)<=PixelTolerance && along>=overlapStart-extension && along<=overlapEnd+extension;
                });
                return validMidline || Fail("声明中轴偏离真实两壁中线或越过有限延伸区间。");
            }
            Supported(trace.Path, "尺寸线/引线", trace.Kind == DrawingDimensionTraceKind.Leader ? null : e.TextBounds);
            if (sharedLeader is not null || e.QuantityKind is DrawingQuantityKind.Radius or DrawingQuantityKind.Diameter or DrawingQuantityKind.ThreadPitch)
            {
                if (trace.Kind != DrawingDimensionTraceKind.Leader)
                { Error("DIMENSION_TRACE_KIND", fact.Id, "圆径/螺距附着需用 Leader 路径。"); continue; }
                var target = Anchor(sharedLeader?.LeaderTargetAnchorId ?? e.TargetAnchorId);
                if (chamfer is not null && (target?.FeatureId!=e.FeatureId || target.Construction is not null || target.Kind is not (DrawingAnchorKind.Line or DrawingAnchorKind.Contour)))
                    Error("DIMENSION_TRACE_ATTACHMENT", fact.Id, "C倒角公共引线须落在同一特征的真实倒角边轮廓。");
                if (compound is not null && (target?.FeatureId != e.FeatureId || target.Kind is not (DrawingAnchorKind.Circle or DrawingAnchorKind.Arc)))
                    Error("DIMENSION_TRACE_ATTACHMENT", fact.Id, "复合孔标注的公共引线必须落在同一孔族的具体圆/弧轮廓。");
                if (compound is not null && e.QuantityKind is DrawingQuantityKind.Linear or DrawingQuantityKind.Angle &&
                    (trace.ModelDirection is not { } axis || !ModelVerification.Finite(axis) || ModelVerification.Norm(axis) < 1e-9))
                    Error("DIMENSION_MEASUREMENT_DIRECTION", fact.Id, "复合孔深/锪孔角明确实际孔轴；不编造二维尺寸界线。");
                if (!page.InBox(trace.Path[0], e.TextBounds, PixelTolerance) || target is not null && page.DistanceToPath(trace.Path[^1], target.SourcePath) > PixelTolerance)
                    Error("DIMENSION_TRACE_ATTACHMENT", fact.Id, "引线须从标注区域走向所选源轮廓；箭头末端未落在轮廓附近。");
                continue;
            }
            if (e.QuantityKind is not (DrawingQuantityKind.Linear or DrawingQuantityKind.Angle))
            { Error("DIMENSION_TRACE_KIND", fact.Id, "此物理量尚不支持该路径合同；数量用原标注分项复核。"); continue; }
            if (trace.ModelDirection is not { } direction || !ModelVerification.Finite(direction) || ModelVerification.Norm(direction) < 1e-9)
            { Error("DIMENSION_MEASUREMENT_DIRECTION", fact.Id, "明确实际模型中的测量方向或角度平面法向，不从图像像素推断毫米尺寸。"); continue; }
            direction = ModelVerification.Unit(direction);
            var key = (e.FeatureId, e.MeasurementId);
            if (priorMeasurements.TryGetValue(key, out var prior) && (prior.Kind != e.QuantityKind ||
                Math.Abs(ModelVerification.Dot(prior.Direction, direction)) < 1 - 1e-7 ||
                e.QuantityKind == DrawingQuantityKind.Angle && (prior.Mode != trace.MeasurementMode || ModelVerification.Dot(prior.Direction, direction) < 1 - 1e-7)))
                Error("DIMENSION_MEASUREMENT_CONFLICT", fact.Id, "同一测量的物理方向、角度类型或有向法向不一致；回查跨视图对应。");
            else priorMeasurements.TryAdd(key, (e.QuantityKind, trace.MeasurementMode, direction));
            var start = Anchor(e.StartAnchorId); var end = Anchor(e.EndAnchorId);
            if (e.QuantityKind == DrawingQuantityKind.Linear)
            {
                if (trace.Kind != DrawingDimensionTraceKind.LinearDimension || trace.MeasurementMode is not (DrawingMeasurementMode.Horizontal or DrawingMeasurementMode.Vertical or DrawingMeasurementMode.Aligned or DrawingMeasurementMode.Projected))
                { Error("DIMENSION_MEASUREMENT_MODE", fact.Id, "线性尺寸明确选择 Horizontal、Vertical、Aligned 或 Projected。"); continue; }
                var firstArrow=trace.StartArrowPoint??trace.Path[0];var lastArrow=trace.EndArrowPoint??trace.Path[^1];
                var first = page.Pixels(firstArrow); var last = page.Pixels(lastArrow);
                if(page.Distance(firstArrow,lastArrow)<4)
                    Error("DIMENSION_TRACE_SHAPE",fact.Id,"尺寸线两端必须可分辨，不接受重合箭头。");
                if (trace.MeasurementMode == DrawingMeasurementMode.Horizontal && Math.Abs(last.Y - first.Y) > PixelTolerance ||
                    trace.MeasurementMode == DrawingMeasurementMode.Vertical && Math.Abs(last.X - first.X) > PixelTolerance ||
                    trace.Path.Any(p => page.DistanceToPath(p, [trace.Path[0], trace.Path[^1]]) > PixelTolerance))
                    Error("DIMENSION_MEASUREMENT_MODE", fact.Id, "声明的距离方向与尺寸线不一致，或尺寸线不是直线。");
                var startValid = Supported(trace.StartExtension, "起点界线"); var endValid = Supported(trace.EndExtension, "终点界线");
                if (startValid && endValid)
                {
                    if (page.Distance(trace.StartExtension[^1], firstArrow) > PixelTolerance || page.Distance(trace.EndExtension[^1], lastArrow) > PixelTolerance ||
                        start is not null && page.DistanceToPath(trace.StartExtension[0], start.SourcePath) > PixelTolerance ||
                        end is not null && page.DistanceToPath(trace.EndExtension[0], end.SourcePath) > PixelTolerance)
                        Error("DIMENSION_TRACE_ATTACHMENT", fact.Id, "界线需要连接真实被测轮廓和各自的尺寸箭头端点。");
                    if (trace.MeasurementMode == DrawingMeasurementMode.Aligned)
                    {
                        var a = page.Pixels(trace.StartExtension[0]); var b = page.Pixels(trace.EndExtension[0]);
                        var u = (X: last.X-first.X,Y:last.Y-first.Y); var v=(X:b.X-a.X,Y:b.Y-a.Y);
                        var denominator=Math.Sqrt((u.X*u.X+u.Y*u.Y)*(v.X*v.X+v.Y*v.Y));
                        if(denominator<1 || Math.Abs(u.X*v.Y-u.Y*v.X)/denominator>.02)
                            Error("DIMENSION_MEASUREMENT_MODE",fact.Id,"斜距尺寸线须与两被测点连线平行；投影距离应显式声明 Projected。");
                    }
                }
            }
            else
            {
                if (trace.Kind != DrawingDimensionTraceKind.AngularDimension || trace.MeasurementMode is not (DrawingMeasurementMode.MinorAngle or DrawingMeasurementMode.ReflexAngle or DrawingMeasurementMode.DirectedAngle) ||
                    trace.AngularVertex is not { } vertex || !ValidPoint(vertex) || trace.Path.Count < 3)
                { Error("DIMENSION_MEASUREMENT_MODE", fact.Id, "角度需要顶点、至少三个弧线点及 MinorAngle、ReflexAngle 或 DirectedAngle。"); continue; }
                if (start is not null && page.DistanceToPath(trace.Path[0], start.SourcePath) > PixelTolerance ||
                    end is not null && page.DistanceToPath(trace.Path[^1], end.SourcePath) > PixelTolerance)
                    Error("DIMENSION_TRACE_ATTACHMENT", fact.Id, "角度弧线的两个箭头须落在各自的基准射线上。");
                DrawingPagePoint Metric(DrawingPagePoint p) { var px=page.Pixels(p); return new(px.X*xScale,px.Y); }
                var v = Metric(vertex); var sweep=0.0; var sign=0;
                var p0=Metric(trace.Path[0]); var radius=Math.Sqrt(Math.Pow(p0.X-v.X,2)+Math.Pow(p0.Y-v.Y,2));
                for(var i=1;i<trace.Path.Count;i++)
                {
                    var a=Metric(trace.Path[i-1]);var b=Metric(trace.Path[i]);
                    var delta=Math.Atan2((a.X-v.X)*(b.Y-v.Y)-(a.Y-v.Y)*(b.X-v.X),(a.X-v.X)*(b.X-v.X)+(a.Y-v.Y)*(b.Y-v.Y));
                    if(Math.Abs(delta)>.0001){if(sign!=0 && Math.Sign(delta)!=sign)Error("DIMENSION_ANGLE_PATH",fact.Id,"角度弧线不能反向折返。");sign=Math.Sign(delta);}
                    sweep+=delta;
                    if(Math.Abs(Math.Sqrt(Math.Pow(b.X-v.X,2)+Math.Pow(b.Y-v.Y,2))-radius)>PixelTolerance)Error("DIMENSION_ANGLE_PATH",fact.Id,"角度弧线与声明顶点不共心。");
                }
                if(radius<4 || Math.Abs(sweep)<.01 || Math.Abs(sweep)>2*Math.PI+.01 ||
                    trace.MeasurementMode==DrawingMeasurementMode.MinorAngle && Math.Abs(sweep)>Math.PI+.02 ||
                    trace.MeasurementMode==DrawingMeasurementMode.ReflexAngle && Math.Abs(sweep)<=Math.PI-.02)
                    Error("DIMENSION_ANGLE_PATH",fact.Id,"内角/优角种类与原图弧线范围不一致。");
                if(trace.MeasurementMode==DrawingMeasurementMode.DirectedAngle &&
                    (trace.ClockwiseInPage is null || trace.ClockwiseInPage.Value!=(sweep>0)))
                    Error("DIMENSION_ANGLE_DIRECTION",fact.Id,"有向角须声明原页顺/逆时针方向并与弧线路径一致。");
                // This is a source-attachment consistency test, never a pixel-derived model dimension.
                if (fact.Unit==DrawingValueUnit.Degree && Math.Abs(Math.Abs(sweep)*180/Math.PI-Math.Abs(fact.NominalValue??fact.Value))>2)
                    Error("DIMENSION_ANGLE_VALUE",fact.Id,"弧线范围与抄录的角度标称值不一致（容差2°）；检查选中的射线与页面拉伸校准，不能从弧线改写标称值。");
                if(start is not null && page.DistanceToPath(vertex,start.SourcePath)>PixelTolerance || end is not null && page.DistanceToPath(vertex,end.SourcePath)>PixelTolerance)
                    Error("DIMENSION_ANGLE_PATH",fact.Id,"角度顶点须位于两条基准射线；轮廓路径需包括交点。");
            }
        }
        return errors;
    }
    private static bool ValidPoint(DrawingPagePoint? p) => p is not null && double.IsFinite(p.X) && double.IsFinite(p.Y) && p.X>=0 && p.X<=1 && p.Y>=0 && p.Y<=1;
    private static bool ValidPath(IReadOnlyList<DrawingPagePoint>? p) => p is { Count: >=2 and <=128 } && p.All(ValidPoint);

    internal static DrawingPagePoint CalibratedRay(DrawingPlanContext context, string viewId, DrawingPagePoint origin, DrawingPagePoint tip)
    {
        if (!ValidPoint(origin) || !ValidPoint(tip)) throw new InvalidDataException("方向射线需要有效原页坐标。");
        var evidence = SourceWorkflow.Evidence(context) ?? throw new InvalidDataException("缺少原图笔画证据。");
        var inventory = DrawingOmissionValidation.Load(evidence.InventoryPath, evidence.InventorySha256);
        if (!string.Equals(Path.GetFullPath(inventory.SourcePath), Path.GetFullPath(context.SourcePath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("方向证据不属于当前原图。");
        var pageNumber = context.Views.FirstOrDefault(v => v.Id == viewId)?.PageNumber;
        var region = inventory.Regions.FirstOrDefault(r => r.PageNumber == pageNumber && r.Purpose == "overview" && r.Bounds == new DrawingReviewBox(0,0,1,1));
        if (pageNumber is null || region?.InkMaskPath is null || region.PixelWidth < 2 || region.PixelHeight < 2)
            throw new InvalidDataException("缺少方向射线的原页栅格。");
        var page = new InkPage(region.PixelWidth, region.PixelHeight, File.ReadAllBytes(region.InkMaskPath));
        return new((tip.X-origin.X)*(region.PixelWidth-1)*Calibrate(context,pageNumber.Value,page), -(tip.Y-origin.Y)*(region.PixelHeight-1));
    }

    private static double Calibrate(DrawingPlanContext context, int pageNumber, InkPage page)
    {
        var metrics=context.PageMetrics.Where(m=>m.PageNumber==pageNumber).ToArray();
        if(metrics.Length==0)return 1;
        if(metrics.Length!=1)throw new InvalidDataException("同页只能声明一份横纵拉伸校准。");
        var metric=metrics[0];
        if(metric.State!=DrawingReviewState.Reviewed || string.IsNullOrWhiteSpace(metric.Evidence) ||
            metric.NormalCircleAnchorIds.Count is <2 or >8 || metric.NormalCircleAnchorIds.Distinct().Count()!=metric.NormalCircleAnchorIds.Count)
            throw new InvalidDataException("页面校准需要2至8个独立正视圆锚点和明确的人工复核说明；不允许使用待验角度弧线反推比例。");
        var ratios=new List<double>();var physical=new HashSet<string>();var views=new HashSet<string>();
        foreach(var id in metric.NormalCircleAnchorIds)
        {
            var a=context.GeometryAnchors.FirstOrDefault(a=>a.Id==id);
            if(a is null || a.State!=DrawingReviewState.Reviewed || a.Construction is not null ||
                a.Kind is not(DrawingAnchorKind.Circle or DrawingAnchorKind.Arc) || !ValidPath(a.SourcePath) || a.SourcePath.Count<24 ||
                string.IsNullOrWhiteSpace(a.PhysicalEntityId) || !physical.Add(a.PhysicalEntityId) ||
                !context.Views.Any(v=>v.Id==a.ViewId&&v.PageNumber==pageNumber&&v.Kind!=ModelDrawingView.Isometric) ||
                a.SourcePath.Any(p=>!page.InBox(p,a.Bounds,PixelTolerance)) || page.Distance(a.SourcePath[0],a.SourcePath[^1])>3 ||
                page.Support(a.SourcePath,null).Fraction<.85)
                throw new InvalidDataException("校准锚点必须来自同页不同实体的已复核完整正视圆，并有真实闭合笔画支持："+id);
            views.Add(a.ViewId);
            var ps=a.SourcePath.Select(page.Pixels).ToArray();var rx=(ps.Max(p=>p.X)-ps.Min(p=>p.X))/2;var ry=(ps.Max(p=>p.Y)-ps.Min(p=>p.Y))/2;
            var cx=(ps.Max(p=>p.X)+ps.Min(p=>p.X))/2;var cy=(ps.Max(p=>p.Y)+ps.Min(p=>p.Y))/2;
            if(Math.Min(rx,ry)<12 || rx/ry is <.5 or >2 || ps.Any(p=>Math.Abs(Math.Sqrt(Math.Pow((p.X-cx)/rx,2)+Math.Pow((p.Y-cy)/ry,2))-1)*Math.Min(rx,ry)>2))
                throw new InvalidDataException("校准轮廓不支持统一轴向拉伸圆："+id);
            var sweep=0.0;var sign=0;
            for(var i=1;i<ps.Length;i++)
            {
                var ax=(ps[i-1].X-cx)/rx;var ay=(ps[i-1].Y-cy)/ry;var bx=(ps[i].X-cx)/rx;var by=(ps[i].Y-cy)/ry;
                var delta=Math.Atan2(ax*by-ay*bx,ax*bx+ay*by);
                if(Math.Abs(delta)>.5 || Math.Abs(delta)>1e-4&&sign!=0&&Math.Sign(delta)!=sign)throw new InvalidDataException("校准轮廓必须连续覆盖完整圆周："+id);
                if(Math.Abs(delta)>1e-4)sign=Math.Sign(delta);sweep+=delta;
            }
            if(Math.Abs(Math.Abs(sweep)-2*Math.PI)>.15)throw new InvalidDataException("校准轮廓未覆盖完整圆周："+id);
            ratios.Add(rx/ry);
        }
        if(views.Count<2 || ratios.Max()/ratios.Min()>1.04)throw new InvalidDataException("校准至少覆盖两个视图，且独立圆的横纵比例须一致（相差不超过4%）。");
        ratios.Sort();var ratio=ratios.Count%2==0?(ratios[ratios.Count/2-1]+ratios[ratios.Count/2])/2:ratios[ratios.Count/2];
        return 1/ratio;
    }

    private sealed class InkPage(int width,int height,byte[] data)
    {
        public bool InkAt(DrawingPagePoint p)
        {
            var q=Pixels(p);int x=(int)Math.Round(q.X),y=(int)Math.Round(q.Y);
            for(int yy=Math.Max(0,y-2);yy<=Math.Min(height-1,y+2);yy++)for(int xx=Math.Max(0,x-2);xx<=Math.Min(width-1,x+2);xx++){int n=yy*width+xx;if((data[n/8]&(1<<(n%8)))!=0)return true;}
            return false;
        }
        public DrawingPagePoint Normalized(DrawingPagePoint p)=>new(p.X/(width-1),p.Y/(height-1));
        public DrawingPagePoint Pixels(DrawingPagePoint p)=>new(p.X*(width-1),p.Y*(height-1));
        public double Distance(DrawingPagePoint a,DrawingPagePoint b){a=Pixels(a);b=Pixels(b);return Math.Sqrt(Math.Pow(a.X-b.X,2)+Math.Pow(a.Y-b.Y,2));}
        public bool InBox(DrawingPagePoint p,DrawingReviewBox b,double padding)=>p.X>=b.Left-padding/(width-1) && p.X<=b.Right+padding/(width-1) && p.Y>=b.Top-padding/(height-1) && p.Y<=b.Bottom+padding/(height-1);
        public double DistanceToPath(DrawingPagePoint p,IReadOnlyList<DrawingPagePoint> path)
        {
            p=Pixels(p);var best=double.PositiveInfinity;
            for(var i=1;i<path.Count;i++)
            {
                var a=Pixels(path[i-1]);var b=Pixels(path[i]);var dx=b.X-a.X;var dy=b.Y-a.Y;var length=dx*dx+dy*dy;
                var t=length==0?0:Math.Clamp(((p.X-a.X)*dx+(p.Y-a.Y)*dy)/length,0,1);
                best=Math.Min(best,Math.Sqrt(Math.Pow(p.X-a.X-t*dx,2)+Math.Pow(p.Y-a.Y-t*dy,2)));
            }
            return best;
        }
        public (int Samples,double Fraction) Support(IReadOnlyList<DrawingPagePoint> path,DrawingReviewBox? ignore)
        {
            var tested=0;var ink=0;
            for(var i=1;i<path.Count;i++)
            {
                var a=path[i-1];var b=path[i];var steps=Math.Clamp((int)Math.Ceiling(Distance(a,b)),1,4096);
                for(var s=0;s<=steps;s++)
                {
                    var p=new DrawingPagePoint(a.X+(b.X-a.X)*s/steps,a.Y+(b.Y-a.Y)*s/steps);
                    if(ignore is not null && InBox(p,ignore,2))continue;
                    var pixel=Pixels(p);tested++;var hit=false;
                    for(var y=Math.Max(0,(int)Math.Round(pixel.Y)-2);y<=Math.Min(height-1,(int)Math.Round(pixel.Y)+2)&&!hit;y++)
                    for(var x=Math.Max(0,(int)Math.Round(pixel.X)-2);x<=Math.Min(width-1,(int)Math.Round(pixel.X)+2);x++)
                    {var n=y*width+x;if((data[n/8]&(1<<(n%8)))!=0){hit=true;break;}}
                    if(hit)ink++;
                }
            }
            return(tested,tested==0?0:(double)ink/tested);
        }
    }
}
