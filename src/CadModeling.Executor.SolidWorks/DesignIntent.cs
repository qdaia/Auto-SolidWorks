using CadModeling.Core;
using CadModeling.Ir;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

internal sealed partial class SolidWorksComExecutor
{
    // Native acceptance is bound to the exact build identity and retained fixture receipts.
    private sealed class SolidWorksDesignIntentSession(IModelDoc2 model) : IDesignIntentSession
    {
        public string ActiveConfiguration => model.ConfigurationManager.ActiveConfiguration.Name;
        public IReadOnlyList<string> ConfigurationNames() => (model.GetConfigurationNames() as Array
            ?? throw new InvalidOperationException("未返回配置清单。")).Cast<string>().ToArray();
        public void EnsureEditable()
        {
            if (model.Extension.HasDesignTable()) throw new InvalidOperationException("DESIGN_INTENT_EXTERNAL_CONTROL: 暂不编辑由设计表控制的模型。");
            var manager = model.GetEquationMgr() ?? throw new InvalidOperationException("方程管理器不可用。");
            if (manager.LinkToFile || !string.IsNullOrWhiteSpace(manager.FilePath))
                throw new InvalidOperationException("DESIGN_INTENT_EXTERNAL_CONTROL: 暂不编辑外部链接方程。");
            RequireNoDisabledEquations(manager);
        }
        public void SelectConfiguration(string name)
        {
            // SOLIDWORKS returns false when asked to show the already-active configuration.
            if (ActiveConfiguration == name) return;
            if (!model.ShowConfiguration2(name) || ActiveConfiguration != name)
                throw new InvalidOperationException("切换配置失败：" + name);
        }
        public void CreateConfiguration(string name, string source)
        {
            SelectConfiguration(source);
            // Empty parent creates an independent configuration from the explicit active source.
            var config = model.ConfigurationManager.AddConfiguration2(name, "参数设计意图", "", 0, "", "", true);
            if (config is null || config.Name != name) throw new InvalidOperationException("创建配置失败：" + name);
        }
        public IReadOnlyList<DesignEquationState> ReadEquations()
        {
            // Reacquire after every configuration change, as required by IEquationMgr docs.
            var manager = model.GetEquationMgr() ?? throw new InvalidOperationException("方程管理器不可用。");
            RequireNoDisabledEquations(manager);
            int count = manager.GetCount();
            if (count is < 0 or > 5000) throw new InvalidOperationException("方程库存数量无效。");
            var result = new List<DesignEquationState>();
            for (int i = 0; i < count; i++)
            {
                // GetCount enumerates active relations only. In the tested 2025 API,
                // Disabled[i] returns true for active row 1 despite disabled count zero.
                // Reject models with any disabled rows so the active inventory is complete.
                var text = manager.Equation[i]; var global = manager.GlobalVariable[i];
                var scope = manager.GetConfigurationOption(i);
                var rawValue = manager.Value[i]; var status = manager.Status;
                result.Add(new(text, global, false, scope == (int)swInConfigurationOpts_e.swAllConfiguration,
                    rawValue, status == i));
            }
            return result;
        }
        private static void RequireNoDisabledEquations(IEquationMgr manager)
        {
            if(manager.GetDisabledEquationCount()!=0)
                throw new InvalidOperationException("DESIGN_INTENT_DISABLED_EQUATIONS：存在禁用方程，无法取得完整可认证库存；不编辑该模型。");
        }
        public void WriteEquation(CompiledDesignEquation equation)
        {
            // Traverse into a detached snapshot before calling Add3/SetEquation.
            var snapshot = ReadEquations();
            var matches = snapshot.Select((e, i) => (Equation: e, Index: i)).Where(e =>
                DesignIntentContract.EquationTarget(e.Equation.Equation) == equation.Target).ToArray();
            if (matches.Length > 1 || matches.Length == 1 && !equation.ReplaceExisting)
                throw new InvalidOperationException("方程目标冲突：" + equation.Target);
            var manager = model.GetEquationMgr() ?? throw new InvalidOperationException("方程管理器不可用。");
            int expectedIndex = matches.Length == 0 ? snapshot.Count : matches[0].Index;
            if (matches.Length == 0)
            {
                int index = ConfigurationNames().Count == 1 ? manager.Add2(-1, equation.Equation, false)
                    : manager.Add3(-1, equation.Equation, false, (int)swInConfigurationOpts_e.swAllConfiguration, null);
                // SOLIDWORKS may insert globals at index zero and reorder the inventory.
                // Validate the returned identity and all previous definitions, not append order.
                NativeDesignIntentReadback.VerifyInsertedEquation(snapshot, ReadEquations(), index, equation);
                expectedIndex = index;
            }
            else if (ConfigurationNames().Count == 1) manager.Equation[expectedIndex] = equation.Equation;
            else if (manager.SetEquationAndConfigurationOption(expectedIndex, equation.Equation,
                (int)swInConfigurationOpts_e.swAllConfiguration, null) != expectedIndex)
                throw new InvalidOperationException("替换方程失败；现有方程可能不由 Add3 创建：" + equation.Target);
            if (DesignIntentContract.NormalizeEquation(manager.Equation[expectedIndex])
                != DesignIntentContract.NormalizeEquation(equation.Equation)) throw new InvalidOperationException("方程写入后定义不符。");
        }
        public void RequireDegreeEquationUnits()
        {
            var manager=model.GetEquationMgr()??throw new InvalidOperationException("方程管理器不可用。");
            if(manager.AngularEquationUnits!=(int)swAngularEquationUnits_e.swAngularEquationUnitsDegrees)
                throw new InvalidOperationException("DESIGN_INTENT_ANGULAR_UNITS：三角函数要求方程角度模式为 Degrees；不修改现有方程的角度设置。");
        }
        public void WriteConfigurationEquation(CompiledDesignEquation equation,string configuration)
        {
            if(ActiveConfiguration!=configuration || !equation.GlobalVariable)
                throw new InvalidOperationException("配置变量写入的活动配置或目标类型错误。");
            var snapshot=ReadEquations();var indexes=snapshot.Select((e,i)=>(e,i))
                .Where(p=>DesignIntentContract.EquationTarget(p.e.Equation)==equation.Target && p.e.GlobalVariable).Select(p=>p.i).ToArray();
            if(indexes.Length!=1)throw new InvalidOperationException("配置变量必须先通过全配置 Add3 创建。");
            var manager=model.GetEquationMgr()??throw new InvalidOperationException("方程管理器不可用。");
            int index=indexes[0];
            if(ConfigurationNames().Count==1)manager.Equation[index]=equation.Equation;
            else if(manager.SetEquationAndConfigurationOption(index,equation.Equation,(int)swInConfigurationOpts_e.swSpecifyConfiguration,new[]{configuration})!=index)
                throw new InvalidOperationException("原生配置变量写入失败。");
            if(DesignIntentContract.NormalizeEquation(manager.Equation[index])!=DesignIntentContract.NormalizeEquation(equation.Equation))
                throw new InvalidOperationException("原生配置变量写入后定义不符。");
        }
        public void RequireConfigurationEquationAuthoring()=>EnsureEditable();
        public void RequireAutomaticSolveOrder()
        {
            var manager=model.GetEquationMgr()??throw new InvalidOperationException("方程管理器不可用。");
            if(!manager.AutomaticSolveOrder)
                throw new InvalidOperationException("DESIGN_INTENT_SOLVE_ORDER：尺寸依赖及配置变量要求启用自动求解顺序；未改写现有设置。");
        }
        public IReadOnlyList<DesignFeatureState> ReadFeatureInventory()
        {
            if(model.FeatureManager.GetFeatures(false) is not Array inventory || inventory.Length is 0 or >10000)
                throw new InvalidOperationException("无法取得完整特征清单。");
            var result=new List<DesignFeatureState>();
            foreach(var item in inventory)
            {
                if(item is not IFeature feature)throw new InvalidOperationException("特征清单包含未知对象。");
                if(NativeDesignIntentReadback.IsPresentationTreeNode(feature.GetTypeName2()))continue;
                if(feature.IsSuppressed2((int)swInConfigurationOpts_e.swThisConfiguration,null) is not Array states
                    || states.Length!=1 || states.GetValue(0) is not bool suppressed)
                    throw new InvalidOperationException("特征当前配置抑制状态不可读："+feature.Name);
                var identity=Persistent(model,feature)??throw new InvalidOperationException("特征持久身份不可读："+feature.Name);
                result.Add(new(feature.Name,identity,suppressed));
            }
            return result;
        }
        public bool SameFeatureIdentity(string first,string second)=>first==second
            || model.Extension.IsSamePersistentID(Convert.FromBase64String(first),Convert.FromBase64String(second))==(int)swObjectEquality.swObjectSame;
        public void RequireFeatureSuppressionAuthoring()=>EnsureEditable();
        public void WriteFeatureSuppression(string name,bool suppressed)
        {
            var feature=(model as IPartDoc)?.FeatureByName(name) as IFeature;
            if(feature is null || feature.Name!=name)throw new InvalidOperationException("未找到准确抑制目标："+name);
            if(!feature.SetSuppression2((int)(suppressed?swFeatureSuppressionAction_e.swSuppressFeature:swFeatureSuppressionAction_e.swUnSuppressFeature),
                (int)swInConfigurationOpts_e.swThisConfiguration,null))
                throw new InvalidOperationException("指定配置特征抑制写入失败："+name);
        }
        public void EvaluateAndRebuild()
        {
            var manager = model.GetEquationMgr() ?? throw new InvalidOperationException("方程管理器不可用。");
            manager.EvaluateAll(); // API documents -1 for BOTH success and failure; do not gate on this return value.
            if (!model.ForceRebuild3(false)) throw new InvalidOperationException("配置方程更新后重建失败。");
            // Capture subsequently checks Value[index]/Status for each controlled equation.
        }
        private IDimension Dimension(string name)
        {
            var dim = model.Parameter(name) as IDimension ?? throw new InvalidOperationException("未找到参数：" + name);
            var actualName = string.Join('@', dim.FullName.Split('@').Take(2));
            if (actualName != name) throw new InvalidOperationException("参数路径解析到了错误身份：" + name);
            return dim;
        }
        public DesignDimensionState ReadDimension(string name)
        {
            var dim = Dimension(name);
            var kind = (swDimensionParamType_e)dim.GetType() switch
            {
                swDimensionParamType_e.swDimensionParamTypeDoubleLinear => NativeDimensionParameterKind.Length,
                swDimensionParamType_e.swDimensionParamTypeDoubleAngular => NativeDimensionParameterKind.Angle,
                swDimensionParamType_e.swDimensionParamTypeInteger => NativeDimensionParameterKind.Integer,
                _ => throw new InvalidOperationException("不支持该参数类型：" + name)
            };
            var values = dim.GetSystemValue3((int)swInConfigurationOpts_e.swThisConfiguration, null) as Array;
            if (values is null || values.Length != 1) throw new InvalidOperationException("参数系统值读回无效：" + name);
            return new(name, kind, Convert.ToDouble(values.GetValue(0)), dim.DrivenState == (int)swDimensionDrivenState_e.swDimensionDriving)
            { EquationControlled = HasActiveDimensionEquation(model,name) };
        }
        public void WriteDimension(string name, double systemValue)
        {
            if (Dimension(name).SetSystemValue3(systemValue, (int)swSetValueInConfiguration_e.swSetValue_InThisConfiguration, null) != 0)
                throw new InvalidOperationException("配置驱动尺寸设置失败：" + name);
        }
        public void RequireFullyDefinedSketch(string name)
        {
            var feature = (model as IPartDoc)?.FeatureByName(name) as IFeature;
            if (feature is null || feature.Name != name || feature.IsSuppressed()
                || feature.GetSpecificFeature2() is not ISketch sketch || sketch.GetConstrainedStatus() != 3)
                throw new InvalidOperationException("配置草图缺失、抑制或未完全定义：" + name);
        }
        public void RequireGeometry(GeometryQualitySpec geometry)
        {
            RequireValidNativeBodies(model);
            VerifyGeometryQuality(MeasureGeometry(model), new AcceptanceSpec { Geometry = geometry }, []);
        }
    }
    private static bool HasActiveDimensionEquation(IModelDoc2 model,string name)
    {
        var manager=model.GetEquationMgr();
        if(manager is null || manager.GetDisabledEquationCount()!=0)return false;
        var matches=new SolidWorksDesignIntentSession(model).ReadEquations()
            .Where(e=>DesignIntentContract.EquationTarget(e.Equation).Equals(name,StringComparison.OrdinalIgnoreCase)).ToArray();
        return matches.Length==1 && DesignIntentContract.EquationTarget(matches[0].Equation)==name
            && !matches[0].GlobalVariable && !matches[0].Disabled && matches[0].EvaluationSucceeded && double.IsFinite(matches[0].RawValue);
    }
}
