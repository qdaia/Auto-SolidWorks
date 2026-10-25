using CadModeling.Ir;
namespace CadModeling.Core;

/// <summary>Checks declared source decomposition, not image truth or native material occupancy.</summary>
public static class DrawingShapeDecompositionValidation
{
    public const string Revision = "shape-decomposition-1";
    private const double Epsilon = 1e-7;

    public static IReadOnlyList<ModelingDiagnostic> Validate(DrawingPlanContext context,
        IReadOnlyList<string> operationIds, ModelVerificationSpec verification)
    {
        var result = new List<ModelingDiagnostic>();
        const string root = "drawing_context.shape_decomposition";
        void Error(string code, string message, string path = root) => result.Add(new(code, DiagnosticSeverity.Error, message, path));
        var required = context.RequireShapeDecomposition || SourceWorkflow.RequiresDecomposition(context.WorkflowRevision);
        if (context.ShapeDecomposition is not { } decomposition)
        {
            if (required) Error("SOURCE_SHAPE_REQUIRED", "先从原图逐视图登记轮廓，再分解保留材料、开口和台阶并关联操作；不能仅提交尺寸和笼统主体。");
            return result;
        }
        if (decomposition.Revision != Revision || decomposition.State != DrawingReviewState.Reviewed)
            Error("SOURCE_SHAPE_UNREVIEWED", "形状分解需要受支持的修订和实际完成的原图复核；Unresolved/Conflict 不能编译。");
        if (decomposition.Features is null || decomposition.Features.Any(f => f is null))
        { Error("SOURCE_SHAPE_INVENTORY", "形状分解的特征列表不能为null或包含null。"); return result; }
        if (decomposition.Features.Count == 0 || !Unique(decomposition.Features.Select(f => f.FeatureId)))
            Error("SOURCE_SHAPE_FEATURES", "形状分解需要非空且唯一的源特征ID。");
        foreach (var feature in context.Features.Where(f => f.Critical))
            if (!decomposition.Features.Any(f => f.FeatureId == feature.Id))
                Error("SOURCE_SHAPE_FEATURE_MISSING", "关键源特征未分解：" + feature.Id);
        var checks = verification.CylinderGroups.Select(c => c.Id).Concat(verification.NativeDimensions.Select(c => c.Id))
            .Concat(verification.Bounds.Select(c => c.Id)).Concat(verification.SurfaceSamples.Select(c => c.Id))
            .Concat(verification.BoundaryClearances.Select(c => c.Id)).Concat(verification.WholeModelChecks.Select(c => c.Id))
            .Concat(verification.EdgeShapes.Select(c => c.Id)).Concat(verification.SurfaceContinuity.Select(c => c.Id))
            .ToHashSet(StringComparer.Ordinal);
        foreach (var item in decomposition.Features)
        {
            var path = root + ".features." + item.FeatureId;
            var feature = context.Features.FirstOrDefault(f => f.Id == item.FeatureId);
            if (feature is null) { Error("SOURCE_SHAPE_FEATURE_UNKNOWN", "分解引用未知或延后源特征：" + item.FeatureId, path); continue; }
            if (item.Contours is null || item.Regions is null || item.AxialMaps is null ||
                item.Contours.Any(c => c is null || c.RegionIds is null) ||
                item.Regions.Any(r => r is null || r.SourceContourIds is null || r.OperationIds is null || r.VerificationCheckIds is null) ||
                item.AxialMaps.Any(m => m is null || m.Layers is null || m.Layers.Any(l => l is null || l.Sectors is null || l.Sectors.Any(s => s is null))))
            { Error("SOURCE_SHAPE_INVENTORY", "轮廓、区域、径向带、分层和扇区必须使用非null列表和记录。", path); continue; }
            if (!Enum.IsDefined(item.Kind) || item.State != DrawingReviewState.Reviewed || string.IsNullOrWhiteSpace(item.TopologyReview))
                Error("SOURCE_SHAPE_UNREVIEWED", "逐特征说明台阶、连接、局部补料/去料与备选拓扑的原图反证。", path);
            if (item.Contours.Count == 0 || !Unique(item.Contours.Select(c => c.Id)) ||
                item.Regions.Count == 0 || !Unique(item.Regions.Select(r => r.Id)))
                Error("SOURCE_SHAPE_INVENTORY", "轮廓与材料/空缺区域清单均须非空，且各自ID唯一。", path);
            foreach (var view in feature.ViewIds)
                if (!item.Contours.Any(c => c.ViewId == view))
                    Error("SOURCE_SHAPE_VIEW_MISSING", "缺少源视图轮廓解释：" + view, path);
            foreach (var contour in item.Contours)
            {
                if (!Enum.IsDefined(contour.Role) || !context.Views.Any(v => v.Id == contour.ViewId) ||
                    !feature.ViewIds.Contains(contour.ViewId) || !Box(contour.Bounds) || string.IsNullOrWhiteSpace(contour.Evidence))
                    Error("SOURCE_SHAPE_CONTOUR", "轮廓须定位到该特征的真实源视图和有限归一化区域，并解释实线/虚线/圆弧的物理含义。", path);
                if (contour.Role == DrawingContourRole.ReferenceOnly)
                {
                    if (string.IsNullOrWhiteSpace(contour.NonMaterialReason) || contour.RegionIds.Count != 0)
                        Error("SOURCE_SHAPE_REFERENCE", "参考线需说明不产生材料边界的原因，不可关联材料区域。", path);
                }
                else if (contour.RegionIds.Count == 0 || !Unique(contour.RegionIds) ||
                    contour.RegionIds.Any(id => !item.Regions.Any(r => r.Id == id && r.SourceContourIds.Contains(contour.Id))))
                    Error("SOURCE_SHAPE_CONTOUR_UNACCOUNTED", "实体边界/开口/台阶轮廓没有双向关联到材料或空缺区域：" + contour.Id, path);
            }
            foreach (var region in item.Regions)
            {
                if (!Enum.IsDefined(region.MaterialState) || string.IsNullOrWhiteSpace(region.Description) || string.IsNullOrWhiteSpace(region.Realization) ||
                    region.SourceContourIds.Count == 0 || !Unique(region.SourceContourIds) ||
                    region.SourceContourIds.Any(id => !item.Contours.Any(c => c.Id == id && c.Role != DrawingContourRole.ReferenceOnly && c.RegionIds.Contains(region.Id))))
                    Error("SOURCE_SHAPE_REGION_EVIDENCE", "材料与空缺区均需原图轮廓支撑及实现解释；不可从模型回填：" + region.Id, path);
                if (region.OperationIds.Count == 0 || !Unique(region.OperationIds) ||
                    region.OperationIds.Any(id => !operationIds.Contains(id) || !feature.OperationIds.Contains(id)))
                    Error("SOURCE_SHAPE_REGION_OPERATION", "材料/空缺区没有关联到该源特征的实际操作；开口可由草图轮廓或切除实现：" + region.Id, path);
                if (region.VerificationCheckIds.Count == 0 || !Unique(region.VerificationCheckIds) ||
                    region.VerificationCheckIds.Any(id => !checks.Contains(id) || !feature.VerificationCheckIds.Contains(id)))
                    Error("SOURCE_SHAPE_REGION_CHECK", "区域须关联该特征已有的独立检查；引用检查不证明其覆盖整块区域：" + region.Id, path);
            }
            if (item.Kind == DrawingShapeKind.AxialAngular && item.AxialMaps.Count == 0 ||
                item.Kind != DrawingShapeKind.AxialAngular && item.AxialMaps.Count != 0)
                Error("SOURCE_SHAPE_AXIAL_MAP_REQUIRED", "圆筒/局部圆环须按径向带、轴向层和360度材料/空缺分区；其他形状不应伪造回转分层。", path);
            if (!Unique(item.AxialMaps.Select(m => m.Id)))
                Error("SOURCE_SHAPE_AXIAL_MAP", "径向带ID必须唯一。", path);
            foreach (var map in item.AxialMaps)
                ValidateMap(map, item, (code, message) => Error(code, message, path));
            if (item.Kind == DrawingShapeKind.AxialAngular)
                foreach (var region in item.Regions)
                    if (!item.AxialMaps.SelectMany(m => m.Layers).SelectMany(l => l.Sectors).Any(s => s.RegionId == region.Id))
                        Error("SOURCE_SHAPE_REGION_UNMAPPED", "轴向/周向材料或空缺区域没有出现在任何分层扇区：" + region.Id, path);
            // Two radial bands on the same frame must not declare contradictory overlapping material volumes.
            for (var i = 0; i < item.AxialMaps.Count; i++)
                for (var j = i + 1; j < item.AxialMaps.Count; j++)
                {
                    var a = item.AxialMaps[i]; var b = item.AxialMaps[j];
                    if (Same(a.OriginMm, b.OriginMm) && Same(a.AxisDirection, b.AxisDirection) &&
                        Overlap(a.InnerRadiusMm, a.OuterRadiusMm, b.InnerRadiusMm, b.OuterRadiusMm) &&
                        Overlap(a.MinimumMm, a.MaximumMm, b.MinimumMm, b.MaximumMm))
                        Error("SOURCE_SHAPE_RADIAL_OVERLAP", "同坐标系的径向带和轴向范围重叠；拆成互不重叠的声明。", path);
                }
        }
        return result;
    }

    private static void ValidateMap(DrawingAxialMaterialMap map, DrawingFeatureDecomposition item, Action<string,string> error)
    {
        if (!Finite(map.OriginMm) || !Finite(map.AxisDirection) || !Finite(map.ZeroAngleDirection) ||
            Math.Abs(Norm(map.AxisDirection) - 1) > Epsilon || Math.Abs(Norm(map.ZeroAngleDirection) - 1) > Epsilon ||
            Math.Abs(Dot(map.AxisDirection, map.ZeroAngleDirection)) > Epsilon ||
            !double.IsFinite(map.InnerRadiusMm) || !double.IsFinite(map.OuterRadiusMm) ||
            map.InnerRadiusMm < 0 || map.OuterRadiusMm <= map.InnerRadiusMm ||
            !Interval(map.MinimumMm, map.MaximumMm) || string.IsNullOrWhiteSpace(map.DatumEvidence))
            error("SOURCE_SHAPE_AXIAL_MAP", "径向带需有限的正交单位轴、零角方向、有效半径/高度区间及来源基准；禁止按像素估尺寸。");
        if (map.Layers.Count == 0 || !Unique(map.Layers.Select(l => l.Id)))
            error("SOURCE_SHAPE_LAYER_GAP", "轴向层必须非空且ID唯一，完整覆盖所声明的高度区间。");
        var end = map.MinimumMm;
        var referenced = new HashSet<string>(StringComparer.Ordinal);
        foreach (var layer in map.Layers.OrderBy(l => l.MinimumMm))
        {
            if (!Interval(layer.MinimumMm, layer.MaximumMm) || Math.Abs(layer.MinimumMm - end) > Epsilon ||
                layer.MinimumMm < map.MinimumMm - Epsilon || layer.MaximumMm > map.MaximumMm + Epsilon || string.IsNullOrWhiteSpace(layer.BoundaryEvidence))
                error("SOURCE_SHAPE_LAYER_GAP", "轴向层出现缺口、重叠、越界或缺少台阶高度证据：" + layer.Id);
            end = layer.MaximumMm;
            var angle = 0.0;
            if (layer.Sectors.Count == 0) error("SOURCE_SHAPE_ANGULAR_GAP", "每层须显式分完360度，包含保留区和空缺区：" + layer.Id);
            foreach (var sector in layer.Sectors.OrderBy(s => s.StartDegrees))
            {
                if (!Interval(sector.StartDegrees, sector.EndDegrees) || sector.StartDegrees < 0 || sector.EndDegrees > 360 ||
                    Math.Abs(sector.StartDegrees - angle) > Epsilon)
                    error("SOURCE_SHAPE_ANGULAR_GAP", "周向分区有遗漏、重叠或越界；跨零度的扇区须拆开：" + layer.Id);
                angle = sector.EndDegrees;
                if (!item.Regions.Any(r => r.Id == sector.RegionId))
                    error("SOURCE_SHAPE_SECTOR_REGION", "扇区未关联材料/空缺区：" + sector.RegionId);
                else referenced.Add(sector.RegionId);
            }
            if (Math.Abs(angle - 360) > Epsilon) error("SOURCE_SHAPE_ANGULAR_GAP", "每层必须覆盖到360度：" + layer.Id);
        }
        if (Math.Abs(end - map.MaximumMm) > Epsilon) error("SOURCE_SHAPE_LAYER_GAP", "轴向层未覆盖到声明的终点。");
        // Regions may describe a bore in another radial band; coverage is checked across all maps by the caller.
        foreach (var regionId in referenced)
        {
            var region = item.Regions.First(r => r.Id == regionId);
            var sourceViews = item.Contours.Where(c => region.SourceContourIds.Contains(c.Id)).Select(c => c.ViewId).Distinct().Count();
            if (item.Contours.Select(c => c.ViewId).Distinct().Count() > 1 && sourceViews < 2)
                error("SOURCE_SHAPE_CROSS_VIEW", "轴向/周向区域必须有至少两个已声明源视图交叉支撑：" + region.Id);
        }
    }

    private static bool Unique(IEnumerable<string> values)
    { var a = values.ToArray(); return a.All(x => !string.IsNullOrWhiteSpace(x)) && a.Distinct(StringComparer.Ordinal).Count() == a.Length; }
    private static bool Interval(double a, double b) => double.IsFinite(a) && double.IsFinite(b) && b > a;
    private static bool Box(DrawingReviewBox? b) => b is not null && double.IsFinite(b.Left) && double.IsFinite(b.Right) && double.IsFinite(b.Top) && double.IsFinite(b.Bottom) && b.Left >= 0 && b.Top >= 0 && b.Right <= 1 && b.Bottom <= 1 && b.Right > b.Left && b.Bottom > b.Top;
    private static bool Finite(Vector3? v) => v is not null && double.IsFinite(v.X) && double.IsFinite(v.Y) && double.IsFinite(v.Z);
    private static double Norm(Vector3 v) => Math.Sqrt(Dot(v,v));
    private static double Dot(Vector3 a, Vector3 b) => a.X*b.X + a.Y*b.Y + a.Z*b.Z;
    private static bool Same(Vector3? a, Vector3? b) => a is not null && b is not null && Norm(new(a.X-b.X,a.Y-b.Y,a.Z-b.Z)) <= Epsilon;
    private static bool Overlap(double a, double b, double c, double d) => Math.Min(b,d)-Math.Max(a,c) > Epsilon;
}
