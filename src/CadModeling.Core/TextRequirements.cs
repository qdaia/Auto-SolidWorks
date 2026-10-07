using System.Globalization;
using System.Text.RegularExpressions;
using CadModeling.Ir;

namespace CadModeling.Core;

public sealed partial class RuleBasedTextCompiler
{
    private const string QuantityNumber = @"(?:\d+(?:\.\d+)?|\.\d+)";
    // Long spellings precede short ones. Boundaries prevent m from consuming mm/unknown units.
    private const string QuantityUnit = @"(?:millimet(?:er|re)s?|centimet(?:er|re)s?|met(?:er|re)s?|inches|inch|mm|cm|in|m|毫米|厘米|公分|英寸|米)(?![a-z])";
    private static string NormalizeQuantities(string text, List<ModelingDiagnostic> diagnostics)
    {
        if(Regex.IsMatch(text,$@"{QuantityNumber}\s*{QuantityUnit}\s+{QuantityUnit}",RegexOptions.IgnoreCase))
        {diagnostics.Add(new("NL_UNIT",DiagnosticSeverity.Error,"长度不能有多个单位。","text"));return text;}
        if (Regex.IsMatch(text,@"(?:^|[^\w.])-\s*\d|\d\s*(?:ft\b|feet\b|yards?\b|yd\b|um\b|μm|微米|英尺|码)|(?:units?|单位)\s*[:=：]?\s*(?:cm|m|in|厘米|米|英寸)\b"))
        {
            diagnostics.Add(new("NL_UNIT",DiagnosticSeverity.Error,"不支持、负数或全局限定的量。给每个长度或尺寸元组附加上 mm/cm/m/in。","text"));
            return text;
        }
        static double Factor(string unit) => unit.StartsWith("cm")||unit.StartsWith("centimet")||unit is "厘米" or "公分"?10:
            unit is "m" or "米"||unit.StartsWith("met")?1000:unit is "in" or "英寸"||unit.StartsWith("inch")?25.4:1;
        static string ConvertLength(string value,string unit) => (double.Parse(value,CultureInfo.InvariantCulture)*Factor(unit)).ToString("G17",CultureInfo.InvariantCulture)+" mm ";
        text=Regex.Replace(text,$@"(?<lx>长(?:度)?)\s*(?<nx>{QuantityNumber})\s*(?<ux>{QuantityUnit})?\s*(?<ly>宽(?:度)?)\s*(?<ny>{QuantityNumber})\s*(?<uy>{QuantityUnit})?\s*(?<lz>厚(?:度)?|高(?:度)?)\s*(?<nz>{QuantityNumber})\s*(?<uz>{QuantityUnit})?",m=>
        {
            var shared=m.Groups["uz"].Value;
            return string.Join(" ",new[]{"x","y","z"}.Select(axis=>m.Groups["l"+axis].Value+ConvertLength(m.Groups["n"+axis].Value,m.Groups["u"+axis].Success?m.Groups["u"+axis].Value:shared)));
        });
        var q=$@"(?<n>{QuantityNumber})\s*(?<u>{QuantityUnit})?";
        // A trailing unit qualifies every unqualified component of THIS tuple, never unrelated holes/angles.
        text=Regex.Replace(text,$@"(?<![\d.]){q}(?:\s*x\s*{q}){{1,2}}",m=>
        {
            var last=Regex.Match(m.Value,$@"(?<u>{QuantityUnit})\s*$").Groups["u"].Value;
            var parts=Regex.Matches(m.Value,q).Cast<Match>().ToArray();
            return string.Join(" x ",parts.Select(p=>ConvertLength(p.Groups["n"].Value,p.Groups["u"].Success?p.Groups["u"].Value:last)));
        });
        text=Regex.Replace(text,$@"(?<![\d.])(?<n>{QuantityNumber})\s*(?<u>{QuantityUnit})",m=>ConvertLength(m.Groups["n"].Value,m.Groups["u"].Value));
        return text;
    }

    private static ModelingPlan ApplyTextRequirements(string text,ModelingPlan plan,List<ModelingDiagnostic> diagnostics)
    {
        var templateName=GeneratedChineseText.ModelTemplate(plan.Name);
        plan = ApplyExtendedTextFeatures(text, plan, templateName, diagnostics);
        void Reject(string code,string message)=>diagnostics.Add(new(code,DiagnosticSeverity.Error,message,"text","使用类型化草案对每个请求的特征和明确的选择进行操作。"));
        // Common requirement audit applies after every shape parser. Adding a new template cannot bypass it.
        var fillet=Regex.IsMatch(text,@"fillet|\bround(?:ed|ing)?\b|圆角|倒圆",RegexOptions.IgnoreCase);
        if(fillet)
        {
            var radius=Regex.Match(text,@"(?:radius|半径|\br)\s*(?<r>\d+(?:\.\d+)?)\s*(?:mm|毫米)?",RegexOptions.IgnoreCase);
            var all=Regex.IsMatch(text,@"all\s+(?:the\s+)?edges|(?:所有|全部|全)\s*(?:棱边|边缘|边)",RegexOptions.IgnoreCase);
            var b=plan.Acceptance.ExpectedBoundingBoxMm;
            if(!all||!radius.Success||b is null||templateName is not ("rectangular_plate" or "cylinder")||plan.Operations.LastOrDefault() is not ExtrudeBossOperation boss||plan.Operations.OfType<ProfileSketchOperation>().Any(s=>s.Primitives.Count!=1))
                Reject("NL_REQUIREMENT_FILLET","圆角需要一个受支持的平面拉伸、一个显式的边缘范围以及一个半径；无法省略的选择是不明确的。");
            else
            {
                var r=double.Parse(radius.Groups["r"].Value,CultureInfo.InvariantCulture);
                if(r<=0||!double.IsFinite(r)||2*r>=Math.Min(b.X,Math.Min(b.Y,b.Z)))
                    Reject("NL_REQUIREMENT_FILLET","圆角半径必须为正数且小于最小挤出包络尺寸的一半。");
                else
                {
                    var operation=new NativeFeatureOperation{Id="fillet_all_edges",Name="全部边线圆角",DependsOn=[boss.Id],Options=new(){Kind=NativeFeatureKind.Fillet,RadiusMm=r,TangentPropagation=false,Selections=[new(){Kind=EntityKind.Edge,FeatureId=boss.Id,AllMatches=true}]}};
                    // Source-derived rounded geometry expectation; never retain the sharp solid's volume.
                    var a=b.X-2*r;var c=b.Z-2*r;var d=b.Y-2*r;
                    var volume=templateName=="rectangular_plate"?a*d*c+2*r*(a*d+a*c+d*c)+Math.PI*r*r*(a+d+c)+4*Math.PI*r*r*r/3:
                        Math.PI*Math.Pow(b.X/2,2)*c+2*Math.PI*(Math.Pow(b.X/2-r,2)*r+(b.X/2-r)*Math.PI*r*r/2+2*r*r*r/3);
                    plan=plan with {Operations=[..plan.Operations,operation],Acceptance=plan.Acceptance with {ExpectedFeatures=[..plan.Acceptance.ExpectedFeatures,operation.Name],Geometry=plan.Acceptance.Geometry with {ExpectedVolumeMm3=volume}}};
                }
            }
        }
        if(Regex.IsMatch(text,@"chamfer|倒角|pocket|凹槽|螺纹|thread|countersink|counterbore|沉孔|沉头|pattern|阵列|锥度|taper|loft|放样|sweep|扫描|mirror|镜像",RegexOptions.IgnoreCase))
            Reject("NL_UNCONSUMED_REQUIREMENT","请求的建模特征超出了确定性文本语法规则，必须不能默默地被忽略。");
        // Ordinary plate/cylinder text has a closed vocabulary and every number must belong to an executed requirement.
        if(templateName is "rectangular_plate" or "cylinder" or "fork_bracket" or "variable_l_channel")
        {
            var consumed=new bool[text.Length];
            void Consume(Regex regex, bool allMatches = false)
            {
                foreach(var match in regex.Matches(text).Cast<Match>().Take(allMatches ? int.MaxValue : 1))
                {
                // Consume named numeric captures only; .{0,N} in old grammars must not hide requirements.
                foreach(var groupName in regex.GetGroupNames().Where(n=>n!="0"))
                foreach(Capture capture in match.Groups[groupName].Captures)
                    for(int i=capture.Index;i<capture.Index+capture.Length;i++)consumed[i]=true;
                }
            }
            var hasHole=plan.Operations.OfType<ProfileSketchOperation>().Any(s=>s.Primitives.Any(p=>p is CircleProfile&&p.Role==ContourRole.Inner))
                || plan.Operations.Any(o=>o.Id=="text_center_hole_cut");
            switch(templateName)
            {
                case "rectangular_plate":Consume(ChinesePlateRegex().IsMatch(text)?ChinesePlateRegex():DimensionTripletRegex());if(hasHole)Consume(HoleDiameterRegex());break;
                case "cylinder":Consume(CylinderRegex());if(hasHole)Consume(HoleDiameterRegex(),allMatches:true);break;
                case "fork_bracket":
                    foreach(var r in new[]{ForkBaseDimensionsRegex(),ForkMountingHoleDiameterRegex(),ForkMountingHoleSpacingRegex(),ForkEarOuterWidthRegex(),ForkEarGapRegex(),ForkPinHoleDiameterRegex(),ForkPinCenterHeightRegex(),ForkHeadRadiusRegex(),ForkPinToEarEndRegex(),ForkStepToBaseEndRegex(),ForkEarEndHeightRegex()})Consume(r);break;
                case "variable_l_channel":foreach(var r in new[]{LChannelEnvelopeRegex(),LChannelLeftSectionRegex(),LChannelTransitionRegex(),LChannelRightSectionRegex()})Consume(r);break;
            }
            if(fillet)Consume(new Regex(@"(?:radius|半径|\br)\s*(?<r>\d+(?:\.\d+)?)\s*(?:mm|毫米)?",RegexOptions.IgnoreCase));
            if(plan.Operations.Any(o=>o.Id=="text_center_hole_cut"))Consume(TextHoleDepthRegex());
            if(plan.Operations.Any(o=>o.Id=="text_shell"))Consume(TextShellThicknessRegex());
            var remaining=new string(text.Select((c,i)=>consumed[i]?' ':c).ToArray());
            // The radius number was consumed above; retain the recognized R shorthand as part of that requirement.
            if(fillet)remaining=Regex.Replace(remaining,@"\br(?=\s|$)"," ",RegexOptions.IgnoreCase);
            if(templateName is "rectangular_plate" or "cylinder"&&HoleIntentRegex().IsMatch(text)&&!hasHole)
                Reject("NL_UNCONSUMED_HOLE","请求创建的孔在编译后的几何体中未被表示。");
            if(templateName=="fork_bracket")remaining=Regex.Replace(remaining,@"双耳外宽|耳板外宽|耳外宽|内间距|内距|槽宽|安装孔|底座孔|中心距|孔距|销孔中心距底面|孔心距底面|孔心到耳片右端|孔心到右端|孔心距耳片右端|台阶距底座右端|台阶到底座右端|台阶距右端|耳片右端高度|末端高度|右端高度|耳孔|销孔|圆头|双耳|叉形|叉耳|底座|尺寸|支架|r(?=\s|$)|clevis|fork[\s-]*bracket|base"," ",RegexOptions.IgnoreCase);
            if(templateName=="variable_l_channel")remaining=Regex.Replace(remaining,@"变截面|l|形|角形|槽|开口|外包络|外形|左段|起始段|右段|末段|过渡段|内部|方形|envelope|variable|section|channel"," ",RegexOptions.IgnoreCase);
            if(plan.Operations.Any(o=>o.Id=="text_center_hole_cut"))
                remaining=Regex.Replace(remaining,@"\b(?:blind|depth|top)\b|盲孔|孔深(?:度)?|深(?:度)?|顶面|顶部"," ",RegexOptions.IgnoreCase);
            if(plan.Operations.Any(o=>o.Id=="text_shell"))
                remaining=Regex.Replace(remaining,@"\b(?:shell|closed|open|top|inward|wall)\b|抽壳|壳体|封闭|闭合|顶部|顶面|开口|向内|壁厚"," ",RegexOptions.IgnoreCase);
            remaining=Regex.Replace(remaining,@"\b(?:make|create|a|an|the|with|and|of|by|plate|rectangular|block|cylinder|diameter|height|width|length|thickness|x|mm|millimet(?:er|re)s?|center|centered|central|through|hole|all|edges?|fillet|filleted|rounded|round|radius)\b|制作|创建|生成|一个|一块|矩形|长方体|板|块|圆柱|毫米|中心|中央|通孔|直径|长(?:度)?|宽(?:度)?|高(?:度)?|厚(?:度)?|带有|带|有|和|的|所有|全部|全|棱边|边缘|边|圆角|倒圆|半径|[ø⌀]"," ",RegexOptions.IgnoreCase);
            remaining=Regex.Replace(remaining,@"[\s\p{P}\p{S}]"," ").Trim();
            if(Regex.IsMatch(remaining,@"[\p{L}\p{N}]"))Reject("NL_UNCONSUMED_TEXT","未满足的文本需求："+remaining);
        }
        return plan;
    }
}
