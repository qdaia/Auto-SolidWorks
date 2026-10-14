using System.Text.Json.Serialization;
using CadModeling.Ir;
namespace CadModeling.Core;
[JsonConverter(typeof(JsonStringEnumConverter<MechanicalRecipeKind>))]
public enum MechanicalRecipeKind { Flange, HolePlate, HoleFamily }
[JsonConverter(typeof(JsonStringEnumConverter<RecipeHoleKind>))]
public enum RecipeHoleKind { Through, Blind, Counterbore }
public sealed record MechanicalRecipeSpec
{
    public MechanicalRecipeKind Kind { get; init; }
    public double? OuterDiameterMm { get; init; }
    public double? InnerDiameterMm { get; init; }
    public double? WidthMm { get; init; }
    public double? HeightMm { get; init; }
    public double? ThicknessMm { get; init; }
    public double? HoleDiameterMm { get; init; }
    public double? PitchCircleDiameterMm { get; init; }
    public int? HoleCount { get; init; }
    public double? StartAngleDegrees { get; init; }
    public IReadOnlyList<ProfilePoint> HoleCenters { get; init; }=[];
    public RecipeHoleKind? HoleKind { get; init; }
    public double? DepthMm { get; init; }
    public double? CounterboreDiameterMm { get; init; }
    public double? CounterboreDepthMm { get; init; }
}
public sealed record RecipeExpansion(GenericModelDraft? Draft,IReadOnlyList<ModelingDiagnostic> Diagnostics);
public static class MechanicalRecipes
{
    public static CompilationResult? TryCompileText(string text,string? output)
    {
        if(string.IsNullOrWhiteSpace(text))return null;
        var header=System.Text.RegularExpressions.Regex.Match(text,@"^\s*(?:(?:创建|生成|制作)\s*)?(?:一个\s*)?法兰",System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if(!header.Success)return null;
        if(text.Length>2000)return new(null,[new("RECIPE_REQUIREMENT",DiagnosticSeverity.Error,"配方文字超出2000字符。","text")]);
        var diagnostics=new List<ModelingDiagnostic>();var remaining=text[header.Length..];
        double? Field(string label,string field,bool angle=false,bool count=false)
        {
            var pattern="(?:"+label+@")\s*[:：]?\s*(?<value>[+-]?\d+(?:\.\d+)?)\s*"+(angle?@"(?:度|°)?":count?"":@"(?:mm|毫米)?");
            var matches=System.Text.RegularExpressions.Regex.Matches(remaining,pattern,System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if(matches.Count!=1){diagnostics.Add(new("RECIPE_REQUIREMENT",DiagnosticSeverity.Error,"参数缺失或重复："+field,"recipe."+field));return null;}
            var value=double.Parse(matches[0].Groups["value"].Value,System.Globalization.CultureInfo.InvariantCulture);
            remaining=remaining.Remove(matches[0].Index,matches[0].Length).Insert(matches[0].Index," ");
            return value;
        }
        var outer=Field("外径","outer_diameter_mm");var inner=Field("中心孔(?:径)?","inner_diameter_mm");
        var thickness=Field("厚(?:度)?","thickness_mm");var hd=Field("安装孔径|孔径","hole_diameter_mm");
        var pcd=Field("分度圆(?:直径)?|PCD","pitch_circle_diameter_mm");var n=Field("孔数","hole_count",count:true);
        var start=Field("起始角(?:度)?","start_angle_degrees",angle:true);
        if(n is {} number&&(number!=Math.Truncate(number)||number<2||number>200))
            diagnostics.Add(new("RECIPE_REQUIREMENT",DiagnosticSeverity.Error,"孔数必须为整数2..200。","recipe.hole_count"));
        if(!string.IsNullOrWhiteSpace(System.Text.RegularExpressions.Regex.Replace(remaining,@"[\s,，;；。]+","")))
            diagnostics.Add(new("NL_UNCONSUMED_REQUIREMENT",DiagnosticSeverity.Error,"法兰配方有未消费的要求；改用完整类型化草案。","text"));
        if(diagnostics.Count>0)return new(null,diagnostics);
        return new GenericPlanCompiler().Compile(new(){SourceText=text,Operations=[],Recipe=new(){Kind=MechanicalRecipeKind.Flange,
            OuterDiameterMm=outer,InnerDiameterMm=inner,ThicknessMm=thickness,HoleDiameterMm=hd,PitchCircleDiameterMm=pcd,HoleCount=(int)n!.Value,StartAngleDegrees=start},
            Assumptions=["未声明长度单位按毫米；法兰配方参数全部来自明确字段。"]},output);
    }
    public static RecipeExpansion Expand(GenericModelDraft input)
    {
        var s=input.Recipe!;var errors=new List<ModelingDiagnostic>();
        void Error(string field,string text)=>errors.Add(new("RECIPE_REQUIREMENT",DiagnosticSeverity.Error,text,"recipe."+field));
        double Need(double? value,string field)
        {
            if(value is not >0||!double.IsFinite(value.Value)){Error(field,"需要明确的有限正尺寸："+field);return 0;}return value.Value;
        }
        if(input.Operations.Count!=0||input.DrawingContext is not null||input.SourceModelPath is not null||input.DesignIntent is not null||ModelVerification.HasChecks(input.Verification))
            Error("kind","配方不能与操作、图纸、已有模型、设计方程或手写验收混用；使用展开后的草案进行复杂建模。");
        if(!Enum.IsDefined(s.Kind))Error("kind","未知机械配方。");
        var allowed=s.Kind==MechanicalRecipeKind.Flange?new[]{"Kind","OuterDiameterMm","InnerDiameterMm","ThicknessMm","HoleDiameterMm","PitchCircleDiameterMm","HoleCount","StartAngleDegrees"}:
            s.Kind==MechanicalRecipeKind.HolePlate?new[]{"Kind","WidthMm","HeightMm","ThicknessMm","HoleDiameterMm","HoleCenters"}:
            new[]{"Kind","WidthMm","HeightMm","ThicknessMm","HoleDiameterMm","HoleCenters","HoleKind","DepthMm","CounterboreDiameterMm","CounterboreDepthMm"};
        foreach(var property in typeof(MechanicalRecipeSpec).GetProperties().Where(p=>!allowed.Contains(p.Name)))
            if(property.GetValue(s) is {} value && (value is not IReadOnlyList<ProfilePoint> list||list.Count>0))Error(property.Name,"配方不消费此参数，不能静默忽略。");
        if(s.Kind==MechanicalRecipeKind.HoleFamily&&s.HoleKind!=RecipeHoleKind.Blind&&s.DepthMm is not null)Error("depth_mm","非盲孔不能声明盲孔深度。");
        if(s.Kind==MechanicalRecipeKind.HoleFamily&&s.HoleKind!=RecipeHoleKind.Counterbore&&(s.CounterboreDiameterMm is not null||s.CounterboreDepthMm is not null))
            Error("counterbore_depth_mm","非沉孔不能声明沉孔参数。");
        var t=Need(s.ThicknessMm,"thickness_mm");var hd=Need(s.HoleDiameterMm,"hole_diameter_mm");
        var operations=new List<GenericOperationDraft>();var groups=new List<CylinderGroupCheck>();
        var centers=new List<ProfilePoint>();double volume=0,area=0;int faces=0,edges=0,planeFaces=0,cylinderFaces=0;
        BoundingBoxSpec? bounds=null;
        GenericOperationDraft Sketch(string id,string name,IReadOnlyList<GenericPrimitiveDraft> primitives)=>new(){Id=id,Name=name,Type=GenericOperationKind.ProfileSketch,Plane=ReferencePlane.Front,
            AutoDimensionPrimitives=true,RequireFullyDefined=true,RequireEditable=true,Primitives=primitives};
        GenericPrimitiveDraft Circle(double d,ProfilePoint p,ContourRole role=ContourRole.Outer)=>new(){Type=GenericPrimitiveKind.Circle,CenterXmm=p.Xmm,CenterYmm=p.Ymm,DiameterMm=d,Role=role};
        void Cut(string id,string name,string sketch,ExtrudeEndCondition end,double? depth=null)=>operations.Add(new(){Id=id,Name=name,Type=GenericOperationKind.ExtrudeCut,SketchId=sketch,EndCondition=end,DepthMm=depth,ReverseDirection=false});
        CylinderGroupCheck Group(string id,double d,IEnumerable<ProfilePoint> points,double z,double length,bool interior)=>new(){Id=id,SourceLiteral=input.SourceText,
            DiameterMm=d,AxisStartsMm=points.Select(p=>new Vector3(p.Xmm,p.Ymm,z)).ToArray(),Direction=new(0,0,1),
            LengthMm=length,Interior=interior,ExactCount=true,ToleranceMm=.02,DirectionToleranceDegrees=.1,Coverage=CylinderCoverage.CompleteWall};
        if(s.Kind==MechanicalRecipeKind.Flange)
        {
            var outer=Need(s.OuterDiameterMm,"outer_diameter_mm");var inner=Need(s.InnerDiameterMm,"inner_diameter_mm");var pcd=Need(s.PitchCircleDiameterMm,"pitch_circle_diameter_mm");
            if(s.HoleCount is not >=2 or >200)Error("hole_count","均布孔数需要明确整数2..200。");
            if(s.StartAngleDegrees is not {} angle||!double.IsFinite(angle)||angle<0||angle>=360)Error("start_angle_degrees","必须声明起始角0..360度，不默认推断。");
            var count=s.HoleCount??0;var start=s.StartAngleDegrees??0;
            if(inner>=outer||pcd/2-hd/2<=inner/2||pcd/2+hd/2>=outer/2)Error("pitch_circle_diameter_mm","孔族必须有明确正内外净距，不接受相切/重叠。");
            if(count>=2&&pcd*Math.Sin(Math.PI/count)<=hd)Error("hole_count","相邻安装孔相切或重叠。");
            if(errors.Count>0)return new(null,errors);
            for(var i=0;i<count;i++){var a=(start+i*360d/count)*Math.PI/180;centers.Add(new(pcd/2*Math.Cos(a),pcd/2*Math.Sin(a)));}
            operations.Add(Sketch("base","法兰轮廓",[Circle(outer,new(0,0)),Circle(inner,new(0,0),ContourRole.Inner)]));
            operations.Add(new(){Id="boss",Name="法兰主体",Type=GenericOperationKind.ExtrudeBoss,SketchId="base",DepthMm=t,EndCondition=ExtrudeEndCondition.Blind});
            // Native circular pattern retains an editable instance count. The seed remains explicitly located.
            operations.Add(Sketch("holes","安装孔定位",[Circle(hd,centers[0])]));
            Cut("cut","安装孔贯穿","holes",ExtrudeEndCondition.ThroughAll);
            operations.Add(new(){Id="axis",Name="孔族基准轴",Type=GenericOperationKind.NativeFeature,Feature=new(){Kind=NativeFeatureKind.ReferenceAxis,AxisStartMm=new(0,0,0),AxisEndMm=new(0,0,t)}});
            operations.Add(new(){Id="pattern",Name="孔圆周阵列",Type=GenericOperationKind.NativeFeature,Feature=new(){Kind=NativeFeatureKind.CircularPattern,Count=count,AngleDegrees=360,
                CountDimensionName="孔数",Selections=[new(){Kind=EntityKind.Axis,FeatureId="axis",SelectionMark=1},new(){Kind=EntityKind.Feature,FeatureId="cut",SelectionMark=4}]}});
            volume=Math.PI*(outer*outer-inner*inner-count*hd*hd)/4*t;
            area=2*volume/t+Math.PI*(outer+inner+count*hd)*t;
            bounds=new(outer,outer,t);faces=count+4;edges=2*(count+2);planeFaces=2;cylinderFaces=count+2;
            groups.Add(Group("外径",outer,[new(0,0)],0,t,false));groups.Add(Group("中心通孔",inner,[new(0,0)],0,t,true));
            // Position values have a single editable native radius parameter for the axis-aligned seed.
            if(Math.Abs(start%90)<1e-10)
            {
                var axis=Math.Abs(centers[0].Xmm)>1e-8?"X":"Y";
                operations[2]=operations[2] with { CenterPositionDimensionName="分度圆半径",CenterPositionDimensionAxis=axis };
            }
        }
        else
        {
            var w=Need(s.WidthMm,"width_mm");var h=Need(s.HeightMm,"height_mm");
            if(s.HoleCenters.Count is <1 or >200)Error("hole_centers","孔板/孔族必须声明1..200个孔心，不推断位置。");
            centers.AddRange(s.HoleCenters);
            var kind=s.Kind==MechanicalRecipeKind.HolePlate?RecipeHoleKind.Through:s.HoleKind;
            if(kind is null||!Enum.IsDefined(kind.Value))Error("hole_kind","孔族需要明确Through/Blind/Counterbore。");
            var d=kind==RecipeHoleKind.Blind?Need(s.DepthMm,"depth_mm"):t;
            var cb=kind==RecipeHoleKind.Counterbore?Need(s.CounterboreDiameterMm,"counterbore_diameter_mm"):hd;
            var cbd=kind==RecipeHoleKind.Counterbore?Need(s.CounterboreDepthMm,"counterbore_depth_mm"):0;
            if(kind==RecipeHoleKind.Blind&&d>=t)Error("depth_mm","盲孔深度必须严格小于厚度。");
            if(kind==RecipeHoleKind.Counterbore&&(cb<=hd||cbd>=t))Error("counterbore_depth_mm","沉孔直径须大于通孔且深度严格小于厚度。");
            for(var i=0;i<centers.Count;i++)
            {
                var p=centers[i];
                if(!double.IsFinite(p.Xmm)||!double.IsFinite(p.Ymm)||Math.Abs(p.Xmm)+cb/2>=w/2||Math.Abs(p.Ymm)+cb/2>=h/2)
                    Error("hole_centers","孔心必须有限，且孔口不能接触外边界。");
                if(centers.Take(i).Any(q=>Math.Sqrt(Math.Pow(q.Xmm-p.Xmm,2)+Math.Pow(q.Ymm-p.Ymm,2))<=cb))
                    Error("hole_centers","孔口相切、重复或重叠。");
            }
            if(errors.Count>0)return new(null,errors);
            operations.Add(Sketch("base","板件轮廓",[new(){Type=GenericPrimitiveKind.CenteredRectangle,CenterXmm=0,CenterYmm=0,WidthMm=w,HeightMm=h}]));
            operations.Add(new(){Id="boss",Name="板件主体",Type=GenericOperationKind.ExtrudeBoss,SketchId="base",DepthMm=t,EndCondition=ExtrudeEndCondition.Blind});
            operations.Add(Sketch("holes","孔族定位",centers.Select(p=>Circle(hd,p)).ToArray()));
            Cut("cut",kind==RecipeHoleKind.Blind?"盲孔":"贯穿孔","holes",kind==RecipeHoleKind.Blind?ExtrudeEndCondition.Blind:ExtrudeEndCondition.ThroughAll,kind==RecipeHoleKind.Blind?d:null);
            groups.Add(Group("孔族",hd,centers,kind==RecipeHoleKind.Counterbore?cbd:0,kind==RecipeHoleKind.Counterbore?t-cbd:d,true));
            var n=centers.Count;volume=w*h*t-n*Math.PI*hd*hd/4*d;area=2*w*h+2*t*(w+h)+n*Math.PI*hd*d;
            planeFaces=6+(kind==RecipeHoleKind.Blind?n:0);cylinderFaces=n;faces=planeFaces+cylinderFaces;edges=12+2*n;
            if(kind!=RecipeHoleKind.Blind)area-=n*Math.PI*hd*hd/2;
            if(kind==RecipeHoleKind.Counterbore)
            {
                operations.Add(Sketch("counterbores","沉孔定位",centers.Select(p=>Circle(cb,p)).ToArray()));Cut("counterbore_cut","沉孔切除","counterbores",ExtrudeEndCondition.Blind,cbd);
                groups.Add(Group("沉孔壁",cb,centers,0,cbd,true));volume-=n*Math.PI*(cb*cb-hd*hd)/4*cbd;
                area+=n*Math.PI*(cb-hd)*cbd;planeFaces+=n;cylinderFaces+=n;faces+=2*n;edges+=2*n;
            }
            bounds=new(w,h,t);
        }
        if(s.Kind==MechanicalRecipeKind.Flange)groups.Add(Group("安装孔",hd,centers,0,t,true));
        var verification=new ModelVerificationSpec{RequireWholeModelInventory=true,CylinderGroups=groups,WholeModelChecks=[new(){Id="配方完整库存",SourceLiteral=input.SourceText,
            SolidBodyCount=1,SurfaceBodyCount=0,FaceCount=faces,EdgeCount=edges,OpenEdgeCount=0,
            SurfaceFaceCounts=new Dictionary<LocalSurfaceKind,int>{{LocalSurfaceKind.Plane,planeFaces},{LocalSurfaceKind.Cylinder,cylinderFaces}},
            VolumeMm3=volume,SurfaceAreaMm2=area,AreaToleranceMm2=.2,VolumeToleranceMm3=.2}]};
        var openingSideRequired=s.Kind==MechanicalRecipeKind.HoleFamily && s.HoleKind is RecipeHoleKind.Blind or RecipeHoleKind.Counterbore;
        var previews=input.PreviewViews ?? (openingSideRequired ? new[]{"isometric","front","top","right","left","back"} : null);
        if(openingSideRequired && previews is not null && !previews.Contains("back"))
            previews=previews.Concat(["back"]).ToArray();
        return new(input with {Recipe=null,Operations=operations,Verification=verification,PreviewViews=previews,ExpectedBoundingBoxMm=bounds,ExpectedVolumeMm3=volume,VolumeTolerancePercent=.001,
            Assumptions=input.Assumptions.Concat(["配方库存来自输入解析公式；周期圆柱壁仅计两条圆边，SolidWorks 2025实测合同。"]).ToArray()},errors);
    }
}
