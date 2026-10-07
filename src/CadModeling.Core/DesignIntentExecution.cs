using CadModeling.Ir;

namespace CadModeling.Core;

public sealed record DesignDimensionState(string Name, NativeDimensionParameterKind Kind, double SystemValue, bool Driving)
{
    public bool EquationControlled { get; init; }
}
public sealed record DesignEquationState(string Equation, bool GlobalVariable, bool Disabled, bool AllConfigurations,
    double RawValue, bool EvaluationSucceeded);
public sealed record DesignFeatureState(string Name, string PersistentReference, bool Suppressed);
public sealed record DesignConfigurationReceipt(string Name, IReadOnlyList<DesignEquationState> Equations,
    IReadOnlyList<DesignDimensionState> Dimensions)
{
    public IReadOnlyList<DesignFeatureState> Features { get; init; } = [];
}
public sealed record DesignIntentReceipt(string ActiveConfiguration, IReadOnlyList<DesignConfigurationReceipt> Configurations);

/// <summary>Native port; the core never activates CAD. Implementations must throw on failed mutations.</summary>
public interface IDesignIntentSession
{
    string ActiveConfiguration { get; }
    IReadOnlyList<string> ConfigurationNames();
    void EnsureEditable();
    void SelectConfiguration(string name);
    void CreateConfiguration(string name, string source);
    IReadOnlyList<DesignEquationState> ReadEquations();
    void WriteEquation(CompiledDesignEquation equation);
    void WriteConfigurationEquation(CompiledDesignEquation equation,string configuration) =>
        throw new InvalidOperationException("DESIGN_INTENT_CONFIGURATION_PORT：配置方程写入端口不可用。");
    void RequireDegreeEquationUnits() =>
        throw new InvalidOperationException("DESIGN_INTENT_ANGULAR_PORT：三角函数角度模式无法确认。");
    void RequireConfigurationEquationAuthoring() =>
        throw new InvalidOperationException("DESIGN_INTENT_CONFIGURATION_PORT：配置方程写入能力不可用。");
    void RequireAutomaticSolveOrder() =>
        throw new InvalidOperationException("DESIGN_INTENT_SOLVE_ORDER_PORT：自动求解顺序无法确认。");
    IReadOnlyList<DesignFeatureState> ReadFeatureInventory() =>
        throw new InvalidOperationException("DESIGN_INTENT_FEATURE_PORT：完整特征抑制清单不可用。");
    void RequireFeatureSuppressionAuthoring() =>
        throw new InvalidOperationException("DESIGN_INTENT_FEATURE_PORT：抑制创作能力不可用。");
    bool SameFeatureIdentity(string first,string second) => first==second;
    void WriteFeatureSuppression(string name,bool suppressed) =>
        throw new InvalidOperationException("DESIGN_INTENT_FEATURE_PORT：特征抑制写入端口不可用。");
    void EvaluateAndRebuild();
    DesignDimensionState ReadDimension(string name);
    void WriteDimension(string name, double systemValue);
    void RequireFullyDefinedSketch(string name);
    void RequireGeometry(GeometryQualitySpec geometry);
}

public static class DesignIntentExecution
{
    public static DesignIntentReceipt Apply(IDesignIntentSession session, DesignIntentSpec spec, CancellationToken token = default)
    {
        var compiled = DesignIntentContract.Compile(spec);
        var original = session.ActiveConfiguration; var finished = false;
        try
        {
            token.ThrowIfCancellationRequested(); session.EnsureEditable();
            if(spec.Configurations.Any(c=>c.GlobalVariables.Count>0))session.RequireConfigurationEquationAuthoring();
            if(spec.Configurations.Any(c=>c.FeatureSuppression.Count>0))session.RequireFeatureSuppressionAuthoring();
            var existing = session.ConfigurationNames(); UniqueConfigurations(existing);
            foreach (var c in spec.Configurations)
            {
                Require(c.ReuseExisting == existing.Contains(c.Name, StringComparer.Ordinal),
                    "配置存在性不符合显式复用/新建策略：" + c.Name);
                Require(!existing.Any(n => n != c.Name && n.Equals(c.Name, StringComparison.OrdinalIgnoreCase)), "配置名称大小写歧义。");
                if (!c.ReuseExisting) Require(existing.Contains(c.CreateFromConfiguration!, StringComparer.Ordinal), "来源配置必须已存在。");
            }
            // Snapshot before mutation; never mutate while traversing IEquationMgr.
            var baseline = Capture(session, compiled, existing, rebuild: false, validateIntent: false, token);
            foreach(var c in spec.Configurations.Where(c=>!c.ReuseExisting))
            foreach(var input in spec.DimensionInputs)
            {
                var source=baseline.Single(r=>r.Name==c.CreateFromConfiguration).Dimensions.Single(d=>d.Name==input.DimensionName);
                var expected=DesignIntentContract.DimensionInputValue(spec,c.Name,input.DimensionName,final:false);
                Require(expected.Unit==DesignIntentContract.Unit(source.Kind)
                    && NativeDimensionValues.Matches(source.SystemValue,NativeDimensionValues.ToSystemValue(expected.Value,source.Kind,expected.Unit),source.Kind),
                    "新配置输入与明确来源配置不符："+c.Name+"/"+input.DimensionName);
            }
            foreach (var receipt in baseline)
            {
                foreach (var dimension in spec.Configurations.SelectMany(c => c.Dimensions))
                    Require(!receipt.Equations.Any(e => DesignIntentContract.EquationTarget(e.Equation)
                        .Equals(dimension.DimensionName, StringComparison.OrdinalIgnoreCase)),
                        "数值覆盖目标由现有方程控制；必须显式声明方程修改：" + dimension.DimensionName);
                foreach (var eq in compiled.Equations)
                {
                    var matches = receipt.Equations.Where(e => DesignIntentContract.EquationTarget(e.Equation)
                        .Equals(eq.Target, StringComparison.OrdinalIgnoreCase)).ToArray();
                    Require(matches.Length <= 1 && (matches.Length == 0 || eq.ReplaceExisting
                        && DesignIntentContract.EquationTarget(matches[0].Equation) == eq.Target
                        && matches[0].GlobalVariable == eq.GlobalVariable), "现有方程目标冲突或未授权替换：" + eq.Target);
                }
            }
            foreach (var c in spec.Configurations.Where(c => !c.ReuseExisting))
            { token.ThrowIfCancellationRequested(); session.CreateConfiguration(c.Name, c.CreateFromConfiguration!); }
            var expectedNames = existing.Concat(spec.Configurations.Where(c => !c.ReuseExisting).Select(c => c.Name)).ToArray();
            Require(SameNames(expectedNames, session.ConfigurationNames()), "创建配置后名称清单不符。");
            session.SelectConfiguration(spec.ActiveConfiguration);
            foreach (var eq in compiled.Equations)
            { token.ThrowIfCancellationRequested(); session.WriteEquation(eq); }
            foreach(var config in spec.Configurations.Where(c=>c.GlobalVariables.Count>0))
            {
                session.SelectConfiguration(config.Name);
                foreach(var eq in compiled.ForConfiguration(config.Name).Where(e=>e.GlobalVariable && config.GlobalVariables.Any(v=>v.Name==e.Target)))
                {token.ThrowIfCancellationRequested();session.WriteConfigurationEquation(eq,config.Name);}
            }
            foreach (var c in spec.Configurations)
            {
                token.ThrowIfCancellationRequested(); session.SelectConfiguration(c.Name);
                foreach (var dim in c.Dimensions)
                {
                    var state = session.ReadDimension(dim.DimensionName);
                    var systemValue = ExpectedSystemValue(state, dim.Value);
                    token.ThrowIfCancellationRequested(); session.WriteDimension(dim.DimensionName, systemValue);
                }
                foreach(var feature in c.FeatureSuppression)
                { token.ThrowIfCancellationRequested(); session.WriteFeatureSuppression(feature.FeatureName,feature.Suppressed); }
            }
            var final = Capture(session, compiled, expectedNames, rebuild: true, validateIntent: true, token);
            if(spec.Configurations.Any(c=>c.FeatureSuppression.Count>0))
            foreach(var after in final)
            {
                var config=spec.Configurations.SingleOrDefault(c=>c.Name==after.Name);
                var before=baseline.Single(c=>c.Name==(config is {ReuseExisting:false}?config.CreateFromConfiguration:after.Name));
                VerifyFeatureInventory(session,before.Features,after.Features,config?.FeatureSuppression??[]);
            }
            foreach (var before in baseline)
            {
                var after = final.Single(c => c.Name == before.Name);
                var protectedEquations = before.Equations.Where(e => !compiled.Equations.Any(eq => eq.Target == DesignIntentContract.EquationTarget(e.Equation)));
                foreach (var eq in protectedEquations)
                    Require(after.Equations.Any(e => SameEquation(e, eq)), "设计意图改变了无关的原有方程。");
                foreach (var dim in before.Dimensions.Where(d => !compiled.Equations.Any(e => !e.GlobalVariable && e.Target == d.Name)
                    && !spec.Configurations.Any(c => c.Name == before.Name && c.Dimensions.Any(v => v.DimensionName == d.Name))))
                    Require(after.Dimensions.Any(d => SameDimension(d, dim)), "配置尺寸更新泄漏到未指定的配置：" + before.Name + "/" + dim.Name);
            }
            token.ThrowIfCancellationRequested(); session.SelectConfiguration(spec.ActiveConfiguration);
            finished = true; return new(spec.ActiveConfiguration, final);
        }
        finally
        {
            if (!finished) session.SelectConfiguration(original);
        }
    }

    public static void VerifySaved(IDesignIntentSession session, DesignIntentSpec spec, DesignIntentReceipt receipt)
    {
        var compiled = DesignIntentContract.Compile(spec); var active = session.ActiveConfiguration;
        Require(active == receipt.ActiveConfiguration && active == spec.ActiveConfiguration, "保存后活动配置发生变化。");
        try
        {
            var names = session.ConfigurationNames(); UniqueConfigurations(names);
            Require(SameNames(names, receipt.Configurations.Select(c => c.Name).ToArray()), "保存后配置清单发生变化。");
            var saved = Capture(session, compiled, names, rebuild: true, validateIntent: true, CancellationToken.None);
            foreach (var before in receipt.Configurations)
            {
                var after = saved.Single(c => c.Name == before.Name);
                Require(before.Equations.Count == after.Equations.Count
                    && before.Equations.All(e => after.Equations.Count(a => SameEquation(a, e)) == 1), "保存后方程定义或求解状态发生变化。");
                Require(before.Dimensions.Count == after.Dimensions.Count
                    && before.Dimensions.All(d => after.Dimensions.Any(a => SameDimension(a, d))), "保存后驱动尺寸发生变化。");
                VerifyFeatureInventory(session,before.Features,after.Features,[]);
            }
        }
        finally { session.SelectConfiguration(active); }
    }

    private static IReadOnlyList<DesignConfigurationReceipt> Capture(IDesignIntentSession session, CompiledDesignIntent compiled,
        IReadOnlyList<string> names, bool rebuild, bool validateIntent, CancellationToken token)
    {
        var targets = compiled.Equations.Where(e => !e.GlobalVariable).Select(e => e.Target)
            .Concat(compiled.Spec.Configurations.SelectMany(c => c.Dimensions).Select(d => d.DimensionName))
            .Concat(compiled.Spec.DimensionInputs.Select(d=>d.DimensionName)).Distinct(StringComparer.Ordinal).ToArray();
        var receipts = new List<DesignConfigurationReceipt>();
        foreach (var name in names)
        {
            token.ThrowIfCancellationRequested(); session.SelectConfiguration(name);
            if (!validateIntent) session.EnsureEditable();
            if(compiled.RequiresDegreeEquationUnits)session.RequireDegreeEquationUnits();
            if(compiled.RequiresAutomaticSolveOrder)session.RequireAutomaticSolveOrder();
            if (rebuild) session.EvaluateAndRebuild();
            var eqs = session.ReadEquations();
            Require(eqs.GroupBy(e => DesignIntentContract.EquationTarget(e.Equation), StringComparer.OrdinalIgnoreCase).All(g => g.Count() == 1),
                "原生方程目标重复或存在大小写歧义。");
            var dims = targets.Select(session.ReadDimension).ToArray();
            IReadOnlyList<DesignFeatureState> features=[];
            if(compiled.Spec.Configurations.Any(c=>c.FeatureSuppression.Count>0))
            {
                features=session.ReadFeatureInventory();
                Require(features.Count is >0 and <=10000 && features.All(f=>!string.IsNullOrWhiteSpace(f.Name) && !string.IsNullOrWhiteSpace(f.PersistentReference))
                    && features.Select(f=>f.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count()==features.Count
                    && features.Select(f=>f.PersistentReference).Distinct(StringComparer.Ordinal).Count()==features.Count,"特征清单为空、不完整或名称／身份有歧义。");
                foreach(var target in compiled.Spec.Configurations.SelectMany(c=>c.FeatureSuppression))
                    Require(features.Count(f=>f.Name==target.FeatureName)==1,"抑制目标未精确匹配原生特征："+target.FeatureName);
                if(validateIntent)
                foreach(var target in compiled.Spec.Configurations.SingleOrDefault(c=>c.Name==name)?.FeatureSuppression??[])
                    Require(features.Single(f=>f.Name==target.FeatureName).Suppressed==target.Suppressed,"特征抑制状态未达到请求值："+target.FeatureName);
            }
            // All target types and driving states are preflighted before the first mutation.
            var expectedEquations=compiled.ForConfiguration(name);
            foreach (var eq in expectedEquations.Where(e => !e.GlobalVariable))
                ExpectedSystemValue(dims.Single(d => d.Name == eq.Target), eq.Value, allowEquationControl:true);
            foreach (var dim in compiled.Spec.Configurations.SelectMany(c => c.Dimensions))
                ExpectedSystemValue(dims.Single(d => d.Name == dim.DimensionName), dim.Value);
            foreach(var input in compiled.Spec.DimensionInputs)
            {
                var state=dims.Single(d=>d.Name==input.DimensionName);
                var expected=DesignIntentContract.DimensionInputValue(compiled.Spec,name,input.DimensionName,validateIntent);
                Require(double.IsFinite(state.SystemValue) && expected.Unit==DesignIntentContract.Unit(state.Kind)
                    && NativeDimensionValues.Matches(state.SystemValue,NativeDimensionValues.ToSystemValue(expected.Value,state.Kind,expected.Unit),state.Kind),
                    "独立尺寸输入与当前配置不符："+name+"/"+input.DimensionName);
            }
            if (validateIntent)
            {
                foreach (var eq in expectedEquations)
                {
                    bool scopedGlobal=eq.GlobalVariable && compiled.Spec.Configurations.Any(c=>c.GlobalVariables.Any(v=>v.Name==eq.Target));
                    var actual = eqs.SingleOrDefault(e => DesignIntentContract.EquationTarget(e.Equation) == eq.Target);
                    Require(actual is not null && actual.GlobalVariable == eq.GlobalVariable && !actual.Disabled
                        && (scopedGlobal || actual.AllConfigurations) && actual.EvaluationSucceeded && double.IsFinite(actual.RawValue)
                        && DesignIntentContract.NormalizeEquation(actual.Equation) == DesignIntentContract.NormalizeEquation(eq.Equation),
                        "方程定义、作用域或求解状态未保留：" + eq.Target);
                    if (!eq.GlobalVariable) CheckDimension(dims.Single(d => d.Name == eq.Target), eq.Value, allowEquationControl:true);
                }
                var config = compiled.Spec.Configurations.SingleOrDefault(c => c.Name == name);
                if (config is not null)
                {
                    foreach (var dim in config.Dimensions) CheckDimension(dims.Single(d => d.Name == dim.DimensionName), dim.Value);
                    session.RequireGeometry(config.Geometry);
                }
                foreach (var sketch in compiled.Spec.RequireFullyDefinedSketches) session.RequireFullyDefinedSketch(sketch);
            }
            receipts.Add(new(name, eqs.ToArray(), dims){Features=features.ToArray()});
        }
        return receipts;
    }

    private static void CheckDimension(DesignDimensionState state, DesignValue value, bool allowEquationControl=false) => Require(
        NativeDimensionValues.Matches(state.SystemValue, ExpectedSystemValue(state, value,allowEquationControl), state.Kind), "驱动尺寸未达到目标值：" + state.Name);
    private static void VerifyFeatureInventory(IDesignIntentSession session,IReadOnlyList<DesignFeatureState> before,
        IReadOnlyList<DesignFeatureState> after,IReadOnlyList<DesignFeatureSuppression> changes)
    {
        Require(before.Count==after.Count,"配置抑制改变了特征清单数量。");
        foreach(var prior in before)
        {
            var current=after.SingleOrDefault(f=>f.Name==prior.Name);
            var expected=changes.SingleOrDefault(f=>f.FeatureName==prior.Name)?.Suppressed??prior.Suppressed;
            Require(current is not null && session.SameFeatureIdentity(prior.PersistentReference,current.PersistentReference)
                && current.Suppressed==expected,"特征身份变化、未声明的连带抑制或跨配置泄漏："+prior.Name);
        }
    }
    private static double ExpectedSystemValue(DesignDimensionState state, DesignValue value, bool allowEquationControl=false)
    {
        Require((state.Driving || allowEquationControl && state.EquationControlled) && double.IsFinite(state.SystemValue) && value.Unit == DesignIntentContract.Unit(state.Kind),
            "尺寸不是驱动参数、值无效或单位与原生类型不符：" + state.Name);
        return NativeDimensionValues.ToSystemValue(value.Value, state.Kind, value.Unit);
    }
    private static bool SameDimension(DesignDimensionState a, DesignDimensionState b) => a.Name == b.Name && a.Kind == b.Kind
        && a.Driving == b.Driving && a.EquationControlled==b.EquationControlled && NativeDimensionValues.Matches(a.SystemValue, b.SystemValue, b.Kind);
    private static bool SameEquation(DesignEquationState a, DesignEquationState b) => a.GlobalVariable == b.GlobalVariable && a.Disabled == b.Disabled
        && a.AllConfigurations == b.AllConfigurations && a.EvaluationSucceeded == b.EvaluationSucceeded
        && DesignIntentContract.Close(a.RawValue, b.RawValue)
        && DesignIntentContract.NormalizeEquation(a.Equation) == DesignIntentContract.NormalizeEquation(b.Equation);
    private static bool SameNames(IReadOnlyList<string> a, IReadOnlyList<string> b) => a.Count == b.Count
        && a.ToHashSet(StringComparer.Ordinal).SetEquals(b);
    private static void UniqueConfigurations(IReadOnlyList<string> names) => Require(names.Count > 0
        && names.Distinct(StringComparer.OrdinalIgnoreCase).Count() == names.Count, "原生配置清单为空或名称有歧义。");
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException("DESIGN_INTENT_READBACK: " + message); }
}
