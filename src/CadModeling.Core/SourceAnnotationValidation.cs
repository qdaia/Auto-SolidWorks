using CadModeling.Ir;
namespace CadModeling.Core;

public static class SourceAnnotationValidation
{
    public static DrawingSourceAnnotation? CompoundAnnotation(DrawingPlanContext context, DrawingDimensionFact fact)
    {
        if (!SourceWorkflow.UsesAnnotationComponents(context.WorkflowRevision) || fact.Status != DrawingFactStatus.Stated) return null;
        var a = context.AnnotationLedger?.Annotations.FirstOrDefault(a => a.Id == fact.SourceAnnotationId && a.SourceLiteral == fact.SourceLiteral && !a.Deferred);
        if (a is null) return null;
        var parsed = HoleCalloutParser.Parse(a.SourceLiteral, a.LengthUnit);
        return parsed.Complete && parsed.Components.Any(c => c.Key == fact.SourceComponentKey) &&
            parsed.Components.Any(c => IsHoleRole(c.Role) || c.Role is "bore_through" or "equal_spacing") ? a : null;
    }
    public static DrawingSourceAnnotation? ChamferAnnotation(DrawingPlanContext context, DrawingDimensionFact fact)
    {
        if (!SourceWorkflow.UsesAnnotationComponents(context.WorkflowRevision) || fact.Status != DrawingFactStatus.Stated) return null;
        var a = context.AnnotationLedger?.Annotations.FirstOrDefault(a => !a.Deferred && a.Id == fact.SourceAnnotationId && a.SourceLiteral == fact.SourceLiteral);
        return a is not null && fact.SourceComponentKey == "chamfer_distance" && HoleCalloutParser.Parse(a.SourceLiteral, a.LengthUnit).Complete &&
            HoleCalloutParser.Parse(a.SourceLiteral, a.LengthUnit).Components.Any(c => c.Role == "chamfer_distance") ? a : null;
    }
    public static IReadOnlyList<ModelingDiagnostic> ValidateOperations(DrawingPlanContext context, IReadOnlyList<GenericOperationDraft> operations)
    {
        var errors = new List<ModelingDiagnostic>();
        if (!SourceWorkflow.UsesAnnotationComponents(context.WorkflowRevision) || context.AnnotationLedger is not { } ledger) return errors;
        foreach (var a in ledger.Annotations.Where(a => !a.Deferred))
            ValidateHoleMode(a, HoleCalloutParser.Parse(a.SourceLiteral, a.LengthUnit), operations,
                (code, text, id) => errors.Add(new(code, DiagnosticSeverity.Error, text, "drawing_context.annotation_ledger." + id)));
        return errors;
    }
    public static IReadOnlyList<ModelingDiagnostic> Validate(DrawingPlanContext context, IReadOnlyList<GenericOperationDraft>? operations = null)
    {
        var errors = new List<ModelingDiagnostic>();
        if (!SourceWorkflow.UsesAnnotationComponents(context.WorkflowRevision)) return errors;
        void Error(string code, string text, string id = "ledger") => errors.Add(new(code, DiagnosticSeverity.Error, text, "drawing_context.annotation_ledger." + id));
        if (context.AnnotationLedger is not { } ledger)
        {
            if(SourceWorkflow.RequiresAnnotationLedger(context.WorkflowRevision))
                Error("SOURCE_ANNOTATION_LEDGER_REQUIRED", "先逐视图登记全部尺寸/孔标注，再绑定操作；完成两次区域反查。");
            else foreach(var fact in context.Dimensions.Where(f=>f.Status==DrawingFactStatus.Stated && NeedsComponents(f)))
                Error("SOURCE_ANNOTATION_COMPONENTS_REQUIRED","复合孔、倒角或多分项标注需要明确的分项到参数绑定；不需要双遍标注扫描。",fact.Id);
            return errors;
        }
        if (!File.Exists(context.SourcePath) || !string.Equals(ledger.SourceSha256, DrawingPlanValidation.FileHash(context.SourcePath), StringComparison.OrdinalIgnoreCase))
            Error("SOURCE_ANNOTATION_STALE", "标注账本不属于当前源图哈希。");
        var annotations = ledger.Annotations;
        if (annotations.Count > 10000 || annotations.Any(a => string.IsNullOrWhiteSpace(a.Id)) || annotations.Select(a => a.Id).Distinct().Count() != annotations.Count)
        { Error("SOURCE_ANNOTATION_ID", "标注ID必须唯一、非空且总数不超过10000。"); return errors; }
        var dimensions = context.Dimensions.GroupBy(d => d.Id).ToDictionary(g => g.Key, g => g.ToArray());
        var consumed = new HashSet<string>();
        foreach (var a in annotations)
        {
            if (a.Deferred)
            {
                if (context.StageScope is not { } stage || !stage.PendingFeatureIds.Contains(a.FeatureId) || context.Features.Any(f => f.Id == a.FeatureId) ||
                    context.Dimensions.Any(f => f.SourceAnnotationId == a.Id) || !context.Views.Any(v => v.Id == a.ViewId) ||
                    !DrawingOmissionValidation.ValidBox(a.TextBounds) || string.IsNullOrWhiteSpace(a.SourceLiteral) || string.IsNullOrWhiteSpace(a.Evidence))
                    Error("SOURCE_ANNOTATION_DEFERRED", "仅显式阶段的待建特征可保留未完成标注，不能作为最终接受或隐去已建尺寸。", a.Id);
                continue;
            }
            var feature = context.Features.FirstOrDefault(f => f.Id == a.FeatureId);
            if (a.State != DrawingReviewState.Reviewed || string.IsNullOrWhiteSpace(a.Evidence) ||
                !DrawingOmissionValidation.ValidBox(a.TextBounds) || !context.Views.Any(v => v.Id == a.ViewId) || feature is null ||
                !feature.OperationIds.Contains(a.OperationId) || !feature.ViewIds.Contains(a.ViewId) ||
                a.LengthUnit is not (DrawingValueUnit.Millimeter or DrawingValueUnit.Inch or DrawingValueUnit.Meter))
                Error("SOURCE_ANNOTATION_REVIEW", "标注须有有效源视图、文本范围、特征/操作归属与实际看图证据。", a.Id);
            var parsed = HoleCalloutParser.Parse(a.SourceLiteral, a.LengthUnit);
            if (!parsed.Complete) Error("SOURCE_ANNOTATION_UNPARSED", "整条标注存在未知、重复或无效分项；保留待审，不允许以单值复核跳过：" + parsed.UnresolvedSuffix, a.Id);
            if (a.Components.Select(c => c.ComponentKey).Distinct().Count() != a.Components.Count ||
                a.Components.Any(c => !parsed.Components.Any(p => p.Key == c.ComponentKey)))
                Error("SOURCE_ANNOTATION_COMPONENT_ID", "分项键重复或不属于当前标注解析。", a.Id);
            foreach (var component in parsed.Components)
            {
                var bindings = a.Components.Where(b => b.ComponentKey == component.Key).ToArray();
                if (bindings.Length != 1)
                { Error("SOURCE_ANNOTATION_COMPONENT_MISSING", "标注分项未唯一入账：" + component.Key, a.Id); continue; }
                var binding = bindings[0];
                if (component.QuantityKind is null || component.Role is "thread_pitch" or "pitch_circle_diameter" && binding.DimensionId is null)
                {
                    if (binding.DimensionId is not null || binding.RetainedLiteral != component.Literal || string.IsNullOrWhiteSpace(binding.Evidence))
                        Error("SOURCE_ANNOTATION_METADATA", "非数值制造要求须逐项保留原文及复核证据；保留不等于制造认证。", a.Id);
                    continue;
                }
                if (binding.DimensionId is null || !dimensions.TryGetValue(binding.DimensionId, out var facts) || facts.Length != 1)
                { Error("SOURCE_ANNOTATION_DIMENSION_MISSING", "每个数值分项必须绑定唯一尺寸事实：" + component.Key, a.Id); continue; }
                var fact = facts[0];
                if (!consumed.Add(fact.Id)) Error("SOURCE_ANNOTATION_REUSED_DIMENSION", "不同标注分项不能共用一个尺寸事实。", a.Id);
                if (fact.SourceAnnotationId != a.Id || fact.SourceComponentKey != component.Key || fact.SourceLiteral != a.SourceLiteral || fact.Status != DrawingFactStatus.Stated ||
                    component.Value is not { } value || !DimensionExpressions.Equal(value, SourceInterpretationValidation.SourceLiteralValue(fact)) || fact.Unit != component.Unit ||
                    fact.BindingEvidence is not { } e || e.FeatureId != a.FeatureId || e.ViewId != a.ViewId || e.QuantityKind != component.QuantityKind || e.TextBounds != a.TextBounds ||
                    feature is null || !feature.OperationIds.Contains(fact.OperationId))
                    Error("SOURCE_ANNOTATION_BINDING", "分项身份、原文、数值、单位、视图或目标特征不一致。", a.Id);
                if (!AllowedPath(component.Role, fact.ParameterPath) || ((IsHoleRole(component.Role) || component.Role is "chamfer_distance" or "count") && fact.OperationId != a.OperationId))
                    Error("SOURCE_ANNOTATION_ROLE_TARGET", "分项语义与真实参数槽不符（孔径/深度/沉孔/锪孔不得互换）：" + component.Role, a.Id);
                if(component.Role=="count" && operations is not null)
                {
                    var kind=operations.FirstOrDefault(o=>o.Id==a.OperationId)?.Feature?.Kind;
                    var path=kind==NativeFeatureKind.Hole?"feature.hole_center_count":
                        kind is NativeFeatureKind.Chamfer or NativeFeatureKind.Fillet?"feature.selection_count":"feature.count";
                    if(fact.ParameterPath!=path)Error("SOURCE_ANNOTATION_ROLE_TARGET","数量须绑定实际执行列表的只读计数，不能用相等的未使用字段代替。",a.Id);
                }
            }
            if (operations is not null) ValidateHoleMode(a, parsed, operations, Error);
        }
        foreach (var f in context.Dimensions.Where(f => f.Status == DrawingFactStatus.Stated))
            if (!consumed.Contains(f.Id) && (SourceWorkflow.RequiresAnnotationLedger(context.WorkflowRevision) || f.SourceAnnotationId is not null || NeedsComponents(f)))
                Error("SOURCE_ANNOTATION_FACT_UNACCOUNTED", "明示尺寸未在源标注中找到对应分项绑定：" + f.Id, f.Id);
        if(SourceWorkflow.IsFourStage(context.WorkflowRevision))return errors;
        try
        {
            if (context.OmissionReview is not { } review) { Error("SOURCE_ANNOTATION_SWEEP_REQUIRED", "需要原图库存区域才能执行全页/细节两遍标注反查。"); return errors; }
            var inventory = DrawingOmissionValidation.Load(review.InventoryPath, review.InventorySha256);
            var regions = inventory.Regions;
            foreach (var sweep in ledger.Sweeps)
                if (!regions.Any(r => r.Id == sweep.RegionId) || sweep.Pass is not (1 or 2)) Error("SOURCE_ANNOTATION_SWEEP_ID", "未知区域或扫描轮次。");
            foreach (var region in regions)
            {
                var expected = annotations.Where(a => context.Views.Any(v => v.Id == a.ViewId && v.PageNumber == region.PageNumber) && Intersects(a.TextBounds, region.Bounds))
                    .Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
                foreach (var pass in new[] { 1, 2 })
                {
                    var sweeps = ledger.Sweeps.Where(s => s.RegionId == region.Id && s.Pass == pass).ToArray();
                    if (sweeps.Length != 1 || sweeps[0].State != DrawingReviewState.Reviewed || string.IsNullOrWhiteSpace(sweeps[0].Evidence) ||
                        sweeps[0].AnnotationIds.Distinct().Count() != sweeps[0].AnnotationIds.Count || !expected.SetEquals(sweeps[0].AnnotationIds))
                        Error("SOURCE_ANNOTATION_SWEEP_MISMATCH", "逐区登记所见标注ID；正向普查与绑定后逆向复查必须各自完整。此检查不证明AI没有漏看。", region.Id);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or System.Text.Json.JsonException)
        { Error("SOURCE_ANNOTATION_INVENTORY", ex.Message); }
        return errors;
    }
    private static bool Intersects(DrawingReviewBox a, DrawingReviewBox b) => a.Left < b.Right && a.Right > b.Left && a.Top < b.Bottom && a.Bottom > b.Top;
    private static bool NeedsComponents(DrawingDimensionFact fact)
    {
        var parsed=HoleCalloutParser.Parse(fact.SourceLiteral,fact.Unit is DrawingValueUnit.Inch or DrawingValueUnit.Meter?fact.Unit:DrawingValueUnit.Millimeter);
        return !parsed.Complete || parsed.Components.Count>1 || parsed.Components.Any(c=>c.Role=="chamfer_distance");
    }
    private static bool IsHoleRole(string role) => role.StartsWith("counter", StringComparison.Ordinal) || role.StartsWith("spotface", StringComparison.Ordinal) || role.StartsWith("thread", StringComparison.Ordinal) || role is "bore_depth";
    private static bool AllowedPath(string role, string path) => role switch
    {
        "bore_diameter" => path == "feature.diameter_mm" || System.Text.RegularExpressions.Regex.IsMatch(path, @"^primitives\.\d+\.diameter_mm$"),
        "bore_depth" => path == "feature.depth_mm",
        "counterbore_diameter" or "spotface_diameter" => path == "feature.counterbore_diameter_mm",
        "counterbore_depth" or "spotface_depth" => path == "feature.counterbore_depth_mm",
        "countersink_diameter" => path == "feature.countersink_diameter_mm",
        "countersink_angle" => path == "feature.countersink_angle_degrees",
        "thread_major_diameter" => path == "feature.thread_major_diameter_mm",
        "thread_depth" => path == "feature.thread_depth_mm",
        "thread_pitch" => false, // Hole execution uses ThreadDesignation; pitch must be retained and checked there.
        "count" => path is "feature.hole_center_count" or "feature.count" or "feature.selection_count",
        "chamfer_distance" => path == "feature.distance_mm",
        "radius" => path.EndsWith("radius_mm", StringComparison.Ordinal),
        "pitch_circle_diameter" => false, // Bound to actual HoleCenters by ValidateHoleMode; no auxiliary model circle.
        _ => true
    };
    private static void ValidateHoleMode(DrawingSourceAnnotation a, HoleCalloutParser.Result parsed, IReadOnlyList<GenericOperationDraft> ops, Action<string,string,string> error)
    {
        var roles = parsed.Components.Select(c => c.Role).ToHashSet();
        var hole = ops.FirstOrDefault(o => o.Id == a.OperationId)?.Feature;
        if (parsed.Components.FirstOrDefault(c => c.Role == "count") is { Value: { } declaredCount })
        {
            var executed = hole?.Kind == NativeFeatureKind.Hole ? hole.HoleCenters.Count :
                hole?.Kind is NativeFeatureKind.Chamfer or NativeFeatureKind.Fillet && !hole.TangentPropagation && hole.VariableFillet is null && hole.Selections.All(s => s.Kind == EntityKind.Edge && !s.AllMatches) &&
                    hole.Selections.Select(s => System.Text.Json.JsonSerializer.Serialize(s, ModelingIrJson.Options)).Distinct().Count() == hole.Selections.Count ? hole.SelectionCount :
                hole?.Kind is NativeFeatureKind.CircularPattern or NativeFeatureKind.LinearPattern ? hole.Count : (int?)null;
            if (executed != declaredCount)
                error("SOURCE_ANNOTATION_EXECUTED_COUNT", "数量必须由实际孔心、阵列或无切向扩展的唯一选边消费；通用未使用count字段不能证明实际数量。", a.Id);
        }
        if (roles.Contains("chamfer_distance") && (hole is null || hole.Kind != NativeFeatureKind.Chamfer || !Enum.IsDefined(hole.ChamferMode) ||
            hole.ChamferMode == ChamferMode.DistanceAngle && (!double.IsFinite(hole.AngleDegrees) || Math.Abs(hole.AngleDegrees - 45) > 1e-7) ||
            hole.ChamferMode == ChamferMode.TwoDistances && !DimensionExpressions.Equal(hole.DistanceMm, hole.SecondDistanceMm)))
            error("SOURCE_ANNOTATION_CHAMFER_MODE", "C标注须执行等距45度倒角；不能换成任意角度、非等距或其他特征。", a.Id);
        if (roles.Any(r => IsHoleRole(r) || r is "bore_through" or "equal_spacing") && (hole is null || hole.Kind != NativeFeatureKind.Hole))
        { error("SOURCE_ANNOTATION_HOLE_OPERATION", "复杂孔分项必须绑定当前支持的原生孔操作；未实现的映射不能冒称通过。", a.Id); return; }
        if (hole is null) return;
        var expectedKind = roles.Contains("thread_major_diameter") ? HoleKind.Tapped : roles.Contains("countersink_mode") ? HoleKind.Countersink :
            roles.Any(r => r.StartsWith("counterbore_") || r.StartsWith("spotface_")) ? HoleKind.Counterbore : HoleKind.Simple;
        if (roles.Any(r => IsHoleRole(r) || r is "bore_through" or "equal_spacing") && hole.HoleKind != expectedKind)
            error("SOURCE_ANNOTATION_HOLE_MODE", "原生孔类型增加或替换了整条源标注所指的孔结构。", a.Id);
        if (roles.Contains("bore_through") && !hole.ThroughAll || roles.Contains("bore_depth") && hole.ThroughAll ||
            roles.Any(r => r.StartsWith("counterbore_") || r.StartsWith("spotface_")) && hole.HoleKind != HoleKind.Counterbore ||
            roles.Contains("countersink_mode") && hole.HoleKind != HoleKind.Countersink || roles.Contains("thread_major_diameter") && hole.HoleKind != HoleKind.Tapped)
            error("SOURCE_ANNOTATION_HOLE_MODE", "通盲孔或沉孔/锪孔/螺纹类型与源标注冲突。", a.Id);
        if (roles.Contains("thread_major_diameter"))
        {
            var designation = HoleCalloutParser.Parse(hole.ThreadDesignation ?? "", a.LengthUnit);
            foreach (var role in new[] { "thread_major_diameter", "thread_pitch", "thread_class" })
            {
                var source = parsed.Components.FirstOrDefault(c => c.Role == role);
                var target = designation.Components.FirstOrDefault(c => c.Role == role);
                if (source is not null && (target is null || source.Value != target.Value || source.Value is null && source.Literal != target.Literal))
                    error("SOURCE_ANNOTATION_THREAD_DESIGNATION", "螺纹公称直径、显式螺距/等级与原生螺纹标记不一致。", a.Id);
            }
            if (!designation.Complete) error("SOURCE_ANNOTATION_THREAD_DESIGNATION", "原生螺纹标记含未解析内容。", a.Id);
        }
        if (roles.Contains("equal_spacing"))
        {
            var centers = hole.HoleCenters;
            var expected = parsed.Components.First(c => c.Role == "pitch_circle_diameter").Value!.Value * DimensionExpressions.Scale(a.LengthUnit);
            var count = parsed.Components.FirstOrDefault(c => c.Role == "count")?.Value;
            var valid = centers.Count >= 2 && (count is null || count == centers.Count);
            if (valid)
            {
                var x = centers.Average(p => p.Xmm); var y = centers.Average(p => p.Ymm);
                valid = centers.All(p => double.IsFinite(p.Xmm + p.Ymm) && Math.Abs(Math.Sqrt(Math.Pow(p.Xmm-x,2)+Math.Pow(p.Ymm-y,2))-expected/2) <= 1e-6);
                var angles = centers.Select(p => Math.Atan2(p.Ymm-y,p.Xmm-x)).Order().ToArray();
                valid &= Enumerable.Range(0,angles.Length).All(i => Math.Abs((i+1<angles.Length?angles[i+1]:angles[0]+2*Math.PI)-angles[i]-2*Math.PI/angles.Length)<=1e-7);
            }
            if (!valid) error("SOURCE_ANNOTATION_PATTERN_LAYOUT", "实际孔心数量、PCD或等角分布与源孔标注不一致。", a.Id);
        }
    }
}
