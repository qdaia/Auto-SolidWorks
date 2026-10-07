using System.Text.RegularExpressions;
using CadModeling.Ir;

namespace CadModeling.Core;

public sealed partial class RuleBasedTextCompiler
{
    private static Regex TextHoleDepthRegex() => new(@"(?:\bdepth\b|孔深(?:度)?|深(?:度)?)\s*(?<depth>\d+(?:\.\d+)?)\s*(?:mm|毫米)?", RegexOptions.IgnoreCase);
    private static Regex TextShellThicknessRegex() => new(@"(?:\b(?:wall\s+)?thickness\b|壁厚)\s*(?<wall>\d+(?:\.\d+)?)\s*(?:mm|毫米)?", RegexOptions.IgnoreCase);
    private static bool HasHoleCenterIntent(string text)
    {
        // 中心意图需属于孔所在的短语，不能借用“中心圆柱／中心板件”的定位。
        foreach (Match center in CenterIntentRegex().Matches(text))
            foreach (Match hole in HoleIntentRegex().Matches(text))
            {
                var start = Math.Min(center.Index + center.Length, hole.Index + hole.Length);
                var end = Math.Max(center.Index, hole.Index);
                var between = end > start ? text[start..end] : "";
                if (!Regex.IsMatch(between,@"\b(?:cylinder|plate|rectangular|block|height|width|length|thickness|x)\b|圆柱|板|块|长(?:度)?|宽(?:度)?|高(?:度)?|厚(?:度)?",RegexOptions.IgnoreCase))
                    return true;
            }
        return false;
    }
    private static ModelingPlan ApplyExtendedTextFeatures(string text, ModelingPlan plan, string template,
        List<ModelingDiagnostic> diagnostics)
    {
        var shell = Regex.IsMatch(text, @"\bshell\b|抽壳|壳体", RegexOptions.IgnoreCase);
        var hole = HoleIntentRegex().IsMatch(text); var blind = BlindHoleIntentRegex().IsMatch(text);
        var through = ThroughHoleIntentRegex().IsMatch(text);
        void Reject(string message) => diagnostics.Add(new("NL_FEATURE_CONTRACT", DiagnosticSeverity.Error, message, "text"));
        if (!shell && !(hole && (blind || template == "cylinder"))) return plan;
        var b = plan.Acceptance.ExpectedBoundingBoxMm;
        if (template is not ("rectangular_plate" or "cylinder") || b is null || plan.Operations.LastOrDefault() is not ExtrudeBossOperation boss)
        { Reject("盲孔／抽壳文本当前需要明确尺寸的板件或圆柱；其他模板必须使用类型化草案。"); return plan; }
        if (shell && hole) { Reject("文本抽壳与孔的组合尚无明确操作顺序与壁厚合同，不能忽略其中一个需求。"); return plan; }
        if (hole)
        {
            var bodyDiameter = template == "cylinder" ? CylinderRegex().Match(text).Groups["d"] : null;
            var diameters = HoleDiameterRegex().Matches(text).Cast<Match>().Where(m => bodyDiameter is null
                || m.Groups["d"].Index != bodyDiameter.Index).ToArray();
            var depths = TextHoleDepthRegex().Matches(text).Cast<Match>().ToArray();
            if (!HasHoleCenterIntent(text) || diameters.Length != 1 || blind == through || blind && depths.Length != 1 || through && depths.Length != 0)
            { Reject("孔需准确的中心位置、单一直径及互斥的通孔／盲孔意图；盲孔必须声明唯一深度，通孔不能声明定深。"); return plan; }
            var diameter = Number(diameters[0], "d"); var depth = blind ? Number(depths[0], "depth") : b.Z;
            if (!double.IsFinite(diameter) || !double.IsFinite(depth) || diameter <= 0 || diameter >= Math.Min(b.X,b.Y)
                || depth <= 0 || blind && depth >= b.Z)
            { Reject("孔直径必须在外包络以内；盲孔深度必须为正且严格小于工件厚度，不能变成通孔。"); return plan; }
            var profile = new ProfileSketchOperation { Id = "text_center_hole_profile", Name = "中心孔驱动草图", DependsOn = [boss.Id],
                Frame = new() { OriginMm = new(0,0,b.Z), Normal = new(0,0,1) },
                AutoDimensionPrimitives = true, RequireFullyDefined = true, Primitives = [new CircleProfile { DiameterMm = diameter }] };
            var cut = new ExtrudeCutOperation { Id = "text_center_hole_cut", Name = blind ? "中心盲孔" : "中心通孔",
                DependsOn = [profile.Id,boss.Id], SketchId = profile.Id, DepthMm = depth,
                EndCondition = blind ? ExtrudeEndCondition.Blind : ExtrudeEndCondition.ThroughAll,
                ReverseDirection = true };
            var expectedVolume = plan.Acceptance.Geometry.ExpectedVolumeMm3!.Value - Math.PI * diameter * diameter / 4 * depth;
            var tolerance = Math.Min(.01, Math.Min(diameter, blind ? Math.Min(depth,b.Z-depth) : depth)/100);
            var cylinder = new CylinderGroupCheck { Id = "text_center_hole_wall", SourceLiteral = plan.SourceText,
                DiameterMm = diameter, LengthMm = depth, AxisStartsMm = [new(0,0,b.Z-depth)], Interior = true, ToleranceMm = tolerance };
            var samples = new List<SurfaceSampleCheck>();
            if (blind) samples.Add(new() { Id = "text_blind_floor", SourceLiteral = plan.SourceText, SurfaceKind = LocalSurfaceKind.Plane,
                PointsMm = [new(0,0,b.Z-depth)], OutwardNormals = [new(0,0,1)], ToleranceMm = tolerance });
            return plan with { Operations = [..plan.Operations,profile,cut], Assumptions = [..plan.Assumptions,
                "中心孔入口位于 +Z 顶面，沿 -Z 向材料内切削；盲孔采用平底圆柱，不推断钻尖角。"],
                Acceptance = plan.Acceptance with { ExpectedFeatures = [..plan.Acceptance.ExpectedFeatures,profile.Name,cut.Name],
                    Geometry = plan.Acceptance.Geometry with { ExpectedVolumeMm3 = expectedVolume } },
                Verification = plan.Verification with { CylinderGroups = [..plan.Verification.CylinderGroups,cylinder],
                    SurfaceSamples = [..plan.Verification.SurfaceSamples,..samples] } };
        }
        var walls = TextShellThicknessRegex().Matches(text).Cast<Match>().ToArray();
        var open = Regex.IsMatch(text,@"\bopen\s+top\b|\btop\s+open\b|(?:顶面|顶部)\s*开口",RegexOptions.IgnoreCase);
        var closed = Regex.IsMatch(text,@"\bclosed\b|封闭|闭合",RegexOptions.IgnoreCase);
        if (walls.Length != 1 || open == closed)
        { Reject("抽壳必须声明唯一壁厚，以及互斥的顶部开口或封闭意图；不猜测移除面。"); return plan; }
        var t = Number(walls[0],"wall");
        if (!double.IsFinite(t) || t <= 0 || 2*t >= Math.Min(b.X,Math.Min(b.Y,b.Z)))
        { Reject("向内壁厚必须为正且小于最小包络尺寸的一半。"); return plan; }
        var selections = open ? new EntityQuery[] { new() { Kind = EntityKind.Face, FeatureId = boss.Id, Geometry = GeometryKind.Plane,
            PositionMm = new(0,0,b.Z), Direction = new(0,0,1) } } : [];
        var operation = new NativeFeatureOperation { Id = "text_shell", Name = open ? "顶部开口向内抽壳" : "封闭向内抽壳", DependsOn = [boss.Id],
            Options = new() { Kind = NativeFeatureKind.Shell, ThicknessMm = t, Reverse = false, Selections = selections } };
        var insideHeight = b.Z-(open?t:2*t);
        var cavity = template == "cylinder" ? Math.PI*Math.Pow(b.X/2-t,2)*insideHeight : (b.X-2*t)*(b.Y-2*t)*insideHeight;
        var interior = new SurfaceSampleCheck { Id = "text_shell_floor", SourceLiteral = plan.SourceText, SurfaceKind = LocalSurfaceKind.Plane,
            PointsMm = [new(0,0,t)], OutwardNormals = [new(0,0,1)], ToleranceMm = Math.Min(.01,t/100) };
        var minimumClearance = Math.Min(Math.Min(b.X/2-t,b.Y/2-t),b.Z-t)/2;
        var clearance = new BoundaryClearanceCheck { Id = "text_shell_opening", SourceLiteral = plan.SourceText, PointsMm = [new(0,0,b.Z)],
            MinimumDistanceMm = minimumClearance, ToleranceMm = Math.Min(.01,minimumClearance/100) };
        return plan with { Operations = [..plan.Operations,operation],
            Acceptance = plan.Acceptance with { ExpectedFeatures = [..plan.Acceptance.ExpectedFeatures,operation.Name],
                Geometry = plan.Acceptance.Geometry with { ExpectedVolumeMm3 = plan.Acceptance.Geometry.ExpectedVolumeMm3!.Value-cavity } },
            Verification = plan.Verification with { SurfaceSamples = [..plan.Verification.SurfaceSamples,interior],
                BoundaryClearances = open ? [..plan.Verification.BoundaryClearances,clearance] : plan.Verification.BoundaryClearances } };
    }
}
