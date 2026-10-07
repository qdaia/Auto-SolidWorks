using System.Globalization;
using CadModeling.Ir;

namespace CadModeling.Core;

public static partial class DesignIntentContract
{
    public static CompiledDesignIntent Compile(DesignIntentSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        Require(spec.Configurations is {Count:>0 and <=64} && spec.GlobalVariables is {Count:<=256}
            && spec.Equations is {Count:<=256} && spec.DimensionInputs is {Count:<=256}
            && spec.RequireFullyDefinedSketches is {Count:<=256},"配置、变量、方程或输入列表无效／超限。");
        var globals=spec.GlobalVariables.ToDictionaryChecked(g=>g.Name,"全局变量");
        var targets=spec.Equations.ToDictionaryChecked(e=>e.DimensionName,"方程目标");
        var inputs=spec.DimensionInputs.ToDictionaryChecked(d=>d.DimensionName,"尺寸输入");
        var configurations=spec.Configurations.ToDictionaryChecked(c=>c.Name,"配置");
        Require(configurations.ContainsKey(spec.ActiveConfiguration),"活动配置必须准确引用声明配置。");
        foreach(var name in globals.Keys)Require(!name.Contains('@'),"全局变量名称不能包含 @。");
        foreach(var name in targets.Keys)DimensionName(name);
        foreach(var input in inputs.Values)
        {
            DimensionName(input.DimensionName);CheckValue(input.Value);CheckDimensionValue(input.Value);
            Require(!targets.Keys.Any(n=>n.Equals(input.DimensionName,StringComparison.OrdinalIgnoreCase)),"方程控制的尺寸应直接引用依赖图，不能同时声明外部输入。");
        }
        foreach(var config in configurations.Values)
        {
            Require(config.ReuseExisting?config.CreateFromConfiguration is null:ValidName(config.CreateFromConfiguration),
                "新配置必须声明来源；复用配置不能声明创建来源。");
            Require(config.Dimensions is {Count:<=256} && config.GlobalVariables is {Count:<=256}
                && config.EquationValues is {Count:<=256} && config.DimensionInputValues is {Count:<=256}
                && config.FeatureSuppression is {Count:<=256},"配置控制列表无效。");
            foreach(var feature in config.FeatureSuppression.ToDictionaryChecked(f=>f.FeatureName,"配置抑制特征").Values)
            {
                Require(!feature.FeatureName.Contains('@'),"抑制目标必须为准确特征名。");
                if(feature.Suppressed)
                {
                    Require(!spec.RequireFullyDefinedSketches.Contains(feature.FeatureName,StringComparer.OrdinalIgnoreCase),"不能抑制必须完全定义的草图。");
                    var controlled=targets.Keys.Concat(inputs.Keys).Concat(config.Dimensions.Select(d=>d.DimensionName));
                    Require(!controlled.Any(n=>n.Split('@').Last().Equals(feature.FeatureName,StringComparison.OrdinalIgnoreCase)),"不能抑制方程、输入或驱动尺寸所属特征。");
                }
            }
            foreach(var dim in config.Dimensions.ToDictionaryChecked(d=>d.DimensionName,"配置尺寸").Values)
            {
                DimensionName(dim.DimensionName);CheckValue(dim.Value);CheckDimensionValue(dim.Value);
                Require(!inputs.TryGetValue(dim.DimensionName,out var input) || input.Value.Unit==dim.Value.Unit,"配置输入覆盖不能改变量纲。");
                Require(!targets.Keys.Any(n=>n.Equals(dim.DimensionName,StringComparison.OrdinalIgnoreCase)),"同一尺寸不能同时接受方程和数值覆盖。");
                Require(!inputs.Keys.Any(n=>n!=dim.DimensionName && n.Equals(dim.DimensionName,StringComparison.OrdinalIgnoreCase)),"配置尺寸与输入名称大小写不一致。");
                Require(dim.Value.Unit!=DrawingValueUnit.Millimeter || dim.Value.Value>=0,"长度尺寸不能为负。");
            }
            foreach(var variable in config.GlobalVariables.ToDictionaryChecked(v=>v.Name,"配置变量").Values)
            {Require(globals.ContainsKey(variable.Name),"配置变量必须准确引用已声明全局变量。");CheckValue(variable.ExpectedValue);}
            foreach(var value in config.EquationValues.ToDictionaryChecked(v=>v.DimensionName,"配置方程预期值").Values)
            {Require(targets.ContainsKey(value.DimensionName),"配置方程预期必须准确引用方程目标。");CheckValue(value.Value);CheckDimensionValue(value.Value);}
            foreach(var value in config.DimensionInputValues.ToDictionaryChecked(v=>v.DimensionName,"配置尺寸输入").Values)
            {Require(inputs.ContainsKey(value.DimensionName),"配置输入必须准确引用已声明的尺寸输入。");CheckValue(value.Value);CheckDimensionValue(value.Value);}
            var g=config.Geometry;
            Require(g is not null && g.ExpectedSolidBodyCount>=0 && g.ExpectedSurfaceBodyCount is null or >=0
                && g.RequireValidTopology && (!g.RequirePositiveVolume || g.ExpectedSolidBodyCount>0)
                && (g.ExpectedVolumeMm3 is null || double.IsFinite(g.ExpectedVolumeMm3.Value) && g.ExpectedVolumeMm3>0)
                && double.IsFinite(g.VolumeTolerancePercent) && g.VolumeTolerancePercent>=0
                && double.IsFinite(g.CenterOfMassToleranceMm) && g.CenterOfMassToleranceMm>=0
                && (g.ExpectedCenterOfMassMm is null || NativeFeatureValidation.Finite(g.ExpectedCenterOfMassMm)),
                "配置几何合同必须有效且不关闭拓扑检查。");
        }
        spec.RequireFullyDefinedSketches.ToDictionaryChecked(s=>s,"完全定义草图");
        bool degrees=false; bool dimensionReferences=false;
        IReadOnlyList<CompiledDesignEquation> CompileContext(DesignConfiguration? config)
        {
            var overrides=(config?.GlobalVariables??[]).ToDictionary(v=>v.Name,StringComparer.Ordinal);
            var expectedDimensions=(config?.EquationValues??[]).ToDictionary(v=>v.DimensionName,StringComparer.Ordinal);
            var values=new Dictionary<string,DesignValue>(StringComparer.Ordinal);var visiting=new HashSet<string>(StringComparer.Ordinal);
            var compiled=new List<CompiledDesignEquation>();
            DesignValue Resolve(string name,bool global)
            {
                if(!global && !targets.ContainsKey(name))
                {
                    DimensionName(name);Require(inputs.ContainsKey(name),"未声明的尺寸输入："+name);
                    return config?.Dimensions.SingleOrDefault(v=>v.DimensionName==name)?.Value
                        ?? config?.DimensionInputValues.SingleOrDefault(v=>v.DimensionName==name)?.Value ?? inputs[name].Value;
                }
                string key=(global?"global:":"dimension:")+name;
                if(values.TryGetValue(key,out var value))return value;
                Require(global?globals.ContainsKey(name):targets.ContainsKey(name),"未声明或大小写不匹配的变量／尺寸："+name);
                Require(visiting.Count<256 && visiting.Add(key),"变量与尺寸的联合依赖图存在循环或过深依赖："+name);
                var expression=global?(overrides.TryGetValue(name,out var local)?local.Expression:globals[name].Expression):targets[name].Expression;
                DesignValue Reference(string target,bool isGlobal)
                { if(!isGlobal)dimensionReferences=true; return Resolve(target,isGlobal); }
                int nodes=0;value=Evaluate(expression,Reference,ref nodes,0,ref degrees);
                if(global && overrides.TryGetValue(name,out var requested))
                    Require(value.Unit==requested.ExpectedValue.Unit && Close(value.Value,requested.ExpectedValue.Value),"配置变量不符合独立预期："+name);
                if(!global)
                {
                    var expected=expectedDimensions.TryGetValue(name,out var scoped)?scoped.Value:targets[name].ExpectedValue;
                    CheckValue(expected);CheckDimensionValue(expected);
                    Require(value.Unit==expected.Unit && Close(value.Value,expected.Value),"方程计算结果不符合独立目标预期："+name);
                    Require(value.Unit!=DrawingValueUnit.Millimeter || value.Value>=0,"方程目标长度不能为负。");
                }
                visiting.Remove(key);values.Add(key,value);
                compiled.Add(new(name,Quote(name)+" = "+Render(expression),global,global?globals[name].ReplaceExisting:targets[name].ReplaceExisting,value));
                return value;
            }
            foreach(var global in spec.GlobalVariables)Resolve(global.Name,true);
            foreach(var equation in spec.Equations)Resolve(equation.DimensionName,false);
            return compiled;
        }
        var common=CompileContext(null);
        var contexts=configurations.Values.ToDictionary(c=>c.Name,CompileContext,StringComparer.Ordinal);
        foreach(var context in contexts.Values)
        foreach(var equation in context)
            Require(common.Single(e=>e.Target==equation.Target).Value.Unit==equation.Value.Unit,"变量／方程不能跨配置改变量纲："+equation.Target);
        foreach(var config in configurations.Values)
        foreach(var input in config.DimensionInputValues)
            Require(input.Value.Unit==inputs[input.DimensionName].Value.Unit,"尺寸输入不能跨配置改变量纲。");
        return new(spec,common,contexts,degrees,dimensionReferences || spec.Configurations.Any(c=>c.GlobalVariables.Count>0));
    }
    public static DesignValue DimensionInputValue(DesignIntentSpec spec,string configuration,string name,bool final)
    {
        var config=spec.Configurations.SingleOrDefault(c=>c.Name==configuration);
        return (final?config?.Dimensions.SingleOrDefault(v=>v.DimensionName==name)?.Value:null)
            ?? config?.DimensionInputValues.SingleOrDefault(v=>v.DimensionName==name)?.Value
            ?? spec.DimensionInputs.Single(v=>v.DimensionName==name).Value;
    }
    private static DesignValue Evaluate(DesignExpression expression,Func<string,bool,DesignValue> reference,
        ref int nodes,int depth,ref bool degrees)
    {
        Require(expression is not null && ++nodes<=256 && depth<=32 && Enum.IsDefined(expression.Kind)
            && expression.Arguments is not null,"表达式为空、未知或超出复杂度限制。");
        var e=expression;
        if(e.Kind==DesignExpressionKind.Literal)
        {Require(e.Variable is null && e.DimensionName is null && e.Function is null && e.Left is null && e.Right is null && e.Arguments.Count==0,"字面量含有未消费字段。");CheckValue(e.Literal);degrees|=e.Literal!.Unit==DrawingValueUnit.Degree;return e.Literal!;}
        if(e.Kind is DesignExpressionKind.Variable or DesignExpressionKind.Dimension)
        {
            Require(e.Literal is null && e.Function is null && e.Left is null && e.Right is null && e.Arguments.Count==0
                && (e.Kind==DesignExpressionKind.Variable?ValidName(e.Variable) && e.DimensionName is null:ValidName(e.DimensionName) && e.Variable is null),"引用含有无效或未消费字段。");
            var value=reference(e.Kind==DesignExpressionKind.Variable?e.Variable!:e.DimensionName!,e.Kind==DesignExpressionKind.Variable);
            degrees|=value.Unit==DrawingValueUnit.Degree;return value;
        }
        if(e.Kind is DesignExpressionKind.Function or DesignExpressionKind.Negate)
        {
            Require(e.Literal is null && e.Variable is null && e.DimensionName is null && e.Left is null && e.Right is null
                && (e.Kind==DesignExpressionKind.Negate?e.Function is null && e.Arguments.Count==1:e.Function is { } f && Enum.IsDefined(f)
                    && e.Arguments.Count==(f is DesignFunction.Min or DesignFunction.Max or DesignFunction.Power?2:1)),"函数名称、参数数量或字段无效。");
            var args=new List<DesignValue>();foreach(var a in e.Arguments)args.Add(Evaluate(a,reference,ref nodes,depth+1,ref degrees));
            if(e.Kind==DesignExpressionKind.Negate)return Checked(-args[0].Value,args[0].Unit);
            return Function(e.Function!.Value,args,ref degrees);
        }
        Require(e.Literal is null && e.Variable is null && e.DimensionName is null && e.Function is null && e.Arguments.Count==0 && e.Left is not null && e.Right is not null,"四则表达式必须仅包含两个操作数。");
        var left=Evaluate(e.Left!,reference,ref nodes,depth+1,ref degrees);var right=Evaluate(e.Right!,reference,ref nodes,depth+1,ref degrees);
        switch(e.Kind)
        {
            case DesignExpressionKind.Add:case DesignExpressionKind.Subtract:
                Require(left.Unit==right.Unit,"加减运算单位必须一致。");return Checked(e.Kind==DesignExpressionKind.Add?left.Value+right.Value:left.Value-right.Value,left.Unit);
            case DesignExpressionKind.Multiply:
                Require(left.Unit==DrawingValueUnit.Unitless || right.Unit==DrawingValueUnit.Unitless,"乘法必须包含无单位量，不支持复合量纲。");
                return Checked(left.Value*right.Value,left.Unit==DrawingValueUnit.Unitless?right.Unit:left.Unit);
            case DesignExpressionKind.Divide:
                Require(right.Value!=0 && (right.Unit==DrawingValueUnit.Unitless || left.Unit==right.Unit),"除数不能为零且须无单位或与分子同单位。");
                return Checked(left.Value/right.Value,right.Unit==DrawingValueUnit.Unitless?left.Unit:DrawingValueUnit.Unitless);
            default:throw new ArgumentException("未知表达式。");
        }
    }
    private static DesignValue Checked(double value,DrawingValueUnit unit){var result=new DesignValue(value,unit);CheckValue(result);return result;}
    private static DesignValue Function(DesignFunction f,IReadOnlyList<DesignValue> args,ref bool degrees)
    {
        var a=args[0];double x=a.Value;
        if(f==DesignFunction.Abs)return Checked(Math.Abs(x),a.Unit);
        if(f==DesignFunction.Sign)return Checked(Math.Sign(x),DrawingValueUnit.Unitless);
        if(f is DesignFunction.Min or DesignFunction.Max)
        {Require(a.Unit==args[1].Unit,"极值函数单位必须一致。");return Checked(f==DesignFunction.Min?Math.Min(x,args[1].Value):Math.Max(x,args[1].Value),a.Unit);}
        if(f is DesignFunction.Sin or DesignFunction.Cos or DesignFunction.Tan)
        {
            Require(a.Unit==DrawingValueUnit.Degree && Math.Abs(x)<=1e8,"三角函数要求显式 Degree 角度且在数值范围内。");degrees=true;double r=x*Math.PI/180;
            Require(f!=DesignFunction.Tan || Math.Abs(Math.Cos(r))>1e-12,"正切函数位于奇点附近。");
            return Checked(f==DesignFunction.Sin?Math.Sin(r):f==DesignFunction.Cos?Math.Cos(r):Math.Tan(r),DrawingValueUnit.Unitless);
        }
        Require(a.Unit==DrawingValueUnit.Unitless,"此函数要求无单位输入，不允许隐式量纲转换。");
        if(f is DesignFunction.Asin or DesignFunction.Acos or DesignFunction.Atan)
        {
            Require(f==DesignFunction.Atan || Math.Abs(x)<=1,"反三角函数输入超出定义域。");degrees=true;
            return Checked((f==DesignFunction.Asin?Math.Asin(x):f==DesignFunction.Acos?Math.Acos(x):Math.Atan(x))*180/Math.PI,DrawingValueUnit.Degree);
        }
        Require(f!=DesignFunction.Sqrt || x>=0,"平方根不能使用负数。");Require(f!=DesignFunction.Log || x>0,"对数要求正数。");
        Require(f!=DesignFunction.Power || args[1].Unit==DrawingValueUnit.Unitless && Math.Abs(args[1].Value)<=128,"幂指数必须是有界无单位值。");
        return Checked(f switch{DesignFunction.Sqrt=>Math.Sqrt(x),DesignFunction.Exp=>Math.Exp(x),DesignFunction.Log=>Math.Log(x),
            DesignFunction.Int=>Math.Floor(x),DesignFunction.Power=>Math.Pow(x,args[1].Value),_=>throw new ArgumentException("未知函数。")},DrawingValueUnit.Unitless);
    }
    // Native equations accept degree numbers under the independently checked Degrees mode.
    // The IR retains Degree; SolidWorks rejects the textual "deg" suffix in equations.
    private static string Render(DesignExpression e,bool degreeNumbers=true)
    {
        var text=RenderNode(e,degreeNumbers);
        Require(text.Length<=16384,"展开后的原生方程超过 16384 字符限制。");
        return text;
    }
    private static string RenderNode(DesignExpression e,bool degreeNumbers)
    {
        if(e.Kind==DesignExpressionKind.Literal)return e.Literal!.Value.ToString("R",CultureInfo.InvariantCulture)+(e.Literal.Unit switch{DrawingValueUnit.Millimeter=>"mm",DrawingValueUnit.Degree=>degreeNumbers?"":"deg",_=>""});
        if(e.Kind==DesignExpressionKind.Variable)return Quote(e.Variable!);if(e.Kind==DesignExpressionKind.Dimension)return Quote(e.DimensionName!);
        if(e.Kind==DesignExpressionKind.Negate)return "(-"+Render(e.Arguments[0],degreeNumbers)+")";
        if(e.Kind==DesignExpressionKind.Function)
        {
            var f=e.Function!.Value;var a=Render(e.Arguments[0],degreeNumbers || f is DesignFunction.Sin or DesignFunction.Cos or DesignFunction.Tan);
            if(f==DesignFunction.Power)return "(("+a+") ^ ("+Render(e.Arguments[1],degreeNumbers)+"))";
            // Native INT truncates negative values toward zero; retain the IR floor contract.
            if(f==DesignFunction.Int)return "IIF("+a+" < INT("+a+"), INT("+a+") - 1, INT("+a+"))";
            if(f is DesignFunction.Min or DesignFunction.Max){var b=Render(e.Arguments[1],degreeNumbers);return "IIF("+a+(f==DesignFunction.Min?" < ":" > ")+b+", "+a+", "+b+")";}
            return (f switch{DesignFunction.Sqrt=>"SQR",DesignFunction.Asin=>"ARCSIN",DesignFunction.Acos=>"ARCCOS",DesignFunction.Atan=>"ATN",DesignFunction.Sign=>"SGN",_=>f.ToString().ToUpperInvariant()})+"("+a+")";
        }
        return "("+Render(e.Left!,degreeNumbers)+(e.Kind switch{DesignExpressionKind.Add=>" + ",DesignExpressionKind.Subtract=>" - ",DesignExpressionKind.Multiply=>" * ",_=>" / "})+Render(e.Right!,degreeNumbers)+")";
    }
}
