using System.Reflection;
using System.Text.Json;
using CadModeling.Core;
using CadModeling.Ir;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using Environment = System.Environment;

var done = new TaskCompletionSource<int>();
var thread = new Thread(() => { try { done.SetResult(Run(args)); } catch (Exception e) { Console.Error.WriteLine(e); done.SetResult(1); } });
thread.SetApartmentState(ApartmentState.STA); thread.Start(); return await done.Task;

static int Run(string[] args)
{
    if (args.Length < 2) throw new ArgumentException("mode output-directory [model-path]");
    var output = Path.GetFullPath(args[1]);
    if (Directory.Exists(output)) throw new InvalidOperationException("证据目录必须尚不存在。");
    Directory.CreateDirectory(output);
    var receiptRoot = Environment.GetEnvironmentVariable("CAD_EXECUTOR_RECEIPTS_DIR") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AutoSolidWorks", "executor-receipts");
    Directory.CreateDirectory(receiptRoot);
    using var lease = new FileStream(Path.Combine(receiptRoot, "native-execution.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
    if (lease.Length > 0)
    {
        using var previous = JsonDocument.Parse(lease);
        if (previous.RootElement.GetProperty("state").GetString() != "completed")
        {
            if(args[0]!="audit-orphan" || args.Length!=3 || previous.RootElement.GetProperty("request_id").GetString()!=args[2])throw new InvalidOperationException("已有未结束的原生租约，禁止执行探针。");
            int owner=previous.RootElement.GetProperty("process_id").GetInt32();
            try{System.Diagnostics.Process.GetProcessById(owner);throw new InvalidOperationException("原执行器仍存在，禁止孤儿审计。");}catch(ArgumentException){}
            File.WriteAllText(Path.Combine(output,"previous-lease.json"),previous.RootElement.GetRawText());
        }
    }
    var request = "probe-" + Guid.NewGuid().ToString("N");
    void Mark(string state) { lease.Position = 0; lease.SetLength(0); JsonSerializer.Serialize(lease, new { request_id = request, state, process_id = Environment.ProcessId, updated_utc = DateTimeOffset.UtcNow }); lease.Flush(true); }
    Mark("active");
    var app = (ISldWorks)Activator.CreateInstance(Type.GetTypeFromProgID("SldWorks.Application", true)!)!;
    app.Visible = true;
    var documents = new List<IModelDoc2>();
    IModelDoc2 New()
    {
        var m = (IModelDoc2)app.NewDocument(@"C:\Users\csj17\Documents\SOLIDWORKS\Templates\MMGS_Clean_2025.prtdot", 0, 0, 0);
        if (m is null) throw new InvalidOperationException("临时零件不可用。");
        documents.Add(m); return m;
    }
    var rows = new List<object>();
    void Save(string name, object value) => File.WriteAllText(Path.Combine(output, name), JsonSerializer.Serialize(value, ModelingIrJson.Options));
    object[] Equations(IModelDoc2 m)
    {
        var mgr = m.GetEquationMgr(); var result = new List<object>();
        for (int i = 0; i < mgr.GetCount(); i++) { var text = mgr.Equation[i]; var global = mgr.GlobalVariable[i]; var value = mgr.Value[i]; var status = mgr.Status; result.Add(new { i, text, global, value, status, disabled = mgr.Disabled[i], scope = mgr.GetConfigurationOption(i) }); }
        return result.ToArray();
    }
    try
    {
        if(args[0]=="audit-orphan")
        {
            // Read-only calls serialize through SolidWorks after the abandoned call.
            // This certifies that execution ended, never that the model passed.
            var inventory=(app.GetDocuments() as object[]??[]).Cast<IModelDoc2>().Select(m=>new{title=m.GetTitle(),path=m.GetPathName(),features=(m.FeatureManager.GetFeatures(false) as object[]??[]).Cast<IFeature>().Select(f=>new{name=f.Name,type=f.GetTypeName2()}).ToArray()}).ToArray();
            Save("orphan-audit.json",new{previous_request_id=args[2],owner_process_exited=true,serialized_read_only_call_returned=true,documents=inventory,previous_outcome="unknown_not_replayed",lease_execution_ended=true});
        }
        else if(args[0]=="calibration")
        {
            DesignExpression Lit(double value,DrawingValueUnit unit=DrawingValueUnit.Unitless)=>new(){Kind=DesignExpressionKind.Literal,Literal=new(value,unit)};
            DesignExpression Fn(DesignFunction f,params DesignExpression[] a)=>new(){Kind=DesignExpressionKind.Function,Function=f,Arguments=a};
            var cases=new (string Id,DesignExpression E,double Expected)[]{
                ("abs-mm",Fn(DesignFunction.Abs,Lit(-3,DrawingValueUnit.Millimeter)),3),
                ("sqrt",Fn(DesignFunction.Sqrt,Lit(81)),9),
                ("sin",Fn(DesignFunction.Sin,Lit(30,DrawingValueUnit.Degree)),.5),
                ("cos",Fn(DesignFunction.Cos,Lit(60,DrawingValueUnit.Degree)),.5),
                ("tan",Fn(DesignFunction.Tan,Lit(45,DrawingValueUnit.Degree)),1),
                ("asin",Fn(DesignFunction.Asin,Lit(.5)),30),
                ("acos",Fn(DesignFunction.Acos,Lit(.5)),60),
                ("atan",Fn(DesignFunction.Atan,Lit(1)),45),
                ("exp",Fn(DesignFunction.Exp,Lit(1)),2.718281828459045),
                ("log",Fn(DesignFunction.Log,Lit(2.718281828459045)),1),
                ("floor-negative",Fn(DesignFunction.Int,Lit(-2.3)),-3),
                ("floor-positive",Fn(DesignFunction.Int,Lit(2.3)),2),
                ("floor-integer",Fn(DesignFunction.Int,Lit(-2)),-2),
                ("sign-zero",Fn(DesignFunction.Sign,Lit(0)),0),
                ("sign-angle",Fn(DesignFunction.Sign,Lit(-7,DrawingValueUnit.Degree)),-1),
                ("sign-mm",Fn(DesignFunction.Sign,Lit(-3,DrawingValueUnit.Millimeter)),-1),
                ("min-mm",Fn(DesignFunction.Min,Lit(4,DrawingValueUnit.Millimeter),Lit(7,DrawingValueUnit.Millimeter)),4),
                ("max-degree",Fn(DesignFunction.Max,Lit(4,DrawingValueUnit.Degree),Lit(7,DrawingValueUnit.Degree)),7),
                ("power",Fn(DesignFunction.Power,Lit(2),Lit(3)),8),
                ("power-negative",Fn(DesignFunction.Power,Lit(-2),Lit(3)),-8),
                ("nested-trig",Fn(DesignFunction.Sin,Fn(DesignFunction.Asin,Lit(.5))),.5),
                ("literal-mm",Lit(25,DrawingValueUnit.Millimeter),25),
                ("literal-degree",Lit(90,DrawingValueUnit.Degree),90)};
            var type=Assembly.Load("CadModeling.Executor.SolidWorks").GetType("SolidWorksComExecutor",true)!.GetNestedType("SolidWorksDesignIntentSession",BindingFlags.NonPublic)!;
            IDesignIntentSession Session(IModelDoc2 m)=>(IDesignIntentSession)Activator.CreateInstance(type,BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance,null,[m],null)!;
            foreach(var test in cases)
            {
                var m=New();m.ConfigurationManager.AddConfiguration2("校准配置","","",0,"","",true);
                var spec=new DesignIntentSpec{ActiveConfiguration="校准配置",Configurations=[new(){Name="校准配置",ReuseExisting=true}],GlobalVariables=[new(){Name="函数结果",Expression=test.E}]};
                var compiled=DesignIntentContract.Compile(spec);var session=Session(m);
                if(compiled.RequiresDegreeEquationUnits)session.RequireDegreeEquationUnits();
                session.WriteEquation(compiled.Equations.Single());session.EvaluateAndRebuild();
                var before=session.ReadEquations().Single();
                bool Valid(DesignEquationState e)=>e.EvaluationSucceeded&&!e.Disabled&&e.GlobalVariable&&e.AllConfigurations&&double.IsFinite(e.RawValue)&&Math.Abs(e.RawValue-test.Expected)<=1e-8*Math.Max(1,Math.Abs(test.Expected))&&DesignIntentContract.NormalizeEquation(e.Equation)==DesignIntentContract.NormalizeEquation(compiled.Equations.Single().Equation);
                if(!Valid(before))throw new InvalidOperationException("函数校准失败："+test.Id);
                var path=Path.Combine(output,test.Id+".SLDPRT");int errors=0,warnings=0;
                if(!m.Extension.SaveAs(path,0,(int)swSaveAsOptions_e.swSaveAsOptions_Silent,null,ref errors,ref warnings)||errors!=0)throw new InvalidOperationException("校准模型保存失败。");
                app.CloseDoc(m.GetTitle());documents.Remove(m);
                var saved=(IModelDoc2)app.OpenDoc6(path,(int)swDocumentTypes_e.swDocPART,(int)swOpenDocOptions_e.swOpenDocOptions_Silent| (int)swOpenDocOptions_e.swOpenDocOptions_ReadOnly,"",ref errors,ref warnings);documents.Add(saved);
                var after=Session(saved).ReadEquations().Single();
                if(!Valid(after))throw new InvalidOperationException("保存后函数校准失败："+test.Id);
                rows.Add(new{id=test.Id,expected=test.Expected,unit=compiled.Equations.Single().Value.Unit,before,after,passed=true});Save("calibration.json",rows);
                app.CloseDoc(saved.GetTitle());documents.Remove(saved);Console.WriteLine("PASS native saved "+test.Id);
            }
        }
        else if (args[0] == "dimension")
        {
            var path=Path.Combine(output,"探针副本.SLDPRT");File.Copy(Path.GetFullPath(args[2]),path,false);
            int errors=0,warnings=0;
            var m=(IModelDoc2)app.OpenDoc6(path,(int)swDocumentTypes_e.swDocPART,(int)swOpenDocOptions_e.swOpenDocOptions_Silent,"",ref errors,ref warnings);
            if(m is null)throw new InvalidOperationException("探针副本打开失败。");documents.Add(m);
            var dim=(IDimension)m.Parameter("宽度_0@基础轮廓草图");
            foreach(var phase in new[]{"before","equation","rebuild"})
            {
                if(phase=="equation")m.GetEquationMgr().Add2(-1,"\"宽度_0@基础轮廓草图\" = 100mm",true);
                if(phase=="rebuild"){m.GetEquationMgr().EvaluateAll();m.ForceRebuild3(false);}
                rows.Add(new{phase,driven_state=dim.DrivenState,driving_enum=(int)swDimensionDrivenState_e.swDimensionDriving,system_value=dim.SystemValue,equations=Equations(m)});
                Save("dimension.json",rows);
            }
        }
        else if(args[0]=="thread-readback")
        {
            int err=0,warn=0;var m=(IModelDoc2)app.OpenDoc6(Path.GetFullPath(args[2]),(int)swDocumentTypes_e.swDocPART,(int)swOpenDocOptions_e.swOpenDocOptions_Silent|(int)swOpenDocOptions_e.swOpenDocOptions_ReadOnly,"",ref err,ref warn);documents.Add(m);
            var f=((object[])m.FeatureManager.GetFeatures(false)).Cast<IFeature>().Single(f=>f.GetTypeName2()=="SweepThread");var d=(IThreadFeatureData)f.GetDefinition();
            if(!d.AccessSelections((ModelDoc)m,null))throw new InvalidOperationException("Thread readback unavailable");
            try{Save("thread-definition.json",new{d.Type,d.Size,d.Pitch,d.PitchOverride,d.Diameter,d.DiameterOverride,d.BlindDepth,d.EndCondition,d.ReverseDirection,d.RightHanded,d.Offset,d.OffsetDistance,d.MultipleStart,d.NumberOfStarts,d.TrimStartFace,d.TrimEndFace});}finally{d.ReleaseSelectionAccess();}
            var body=(IBody2)((object[])((IPartDoc)m).GetBodies2((int)swBodyType_e.swSolidBody,false))[0];
            Save("thread-edges.json",((object[])body.GetEdges()).Cast<IEdge>().Select(e=>{var c=(ICurve)e.GetCurve();c.GetEndParams(out var start,out var end,out var closed,out var periodic);return new{line=c.IsLine(),circle=c.IsCircle(),start,end,points=Enumerable.Range(0,65).Select(i=>((double[])c.Evaluate2(start+(end-start)*i/64,0)).Take(3).ToArray()).ToArray()};}).ToArray());
        }
        else if(args[0]=="thread")
        {
            var profile=@"C:\ProgramData\SOLIDWORKS\SOLIDWORKS 2025\Thread Profiles\Metric Die.SLDLFP";
            Save("profile-configurations.json",app.GetConfigurationNames(profile));
            var plan=new RuleBasedTextCompiler().Compile("cylinder diameter 10 mm height 20 mm").Plan!;
            var kernel=Assembly.Load("CadModeling.Executor.SolidWorks").GetType("SolidWorksComExecutor",true)!;
            object Invoke(string name,params object[] parameters)=>kernel.GetMethod(name,BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,parameters)!;
            var math=(IMathUtility)app.GetMathUtility();var m=New();var objects=new Dictionary<string,object>();
            foreach(var op in plan.Operations)
            {
                var f=(IFeature)(op is ProfileSketchOperation sketch?Invoke("ExecuteSketch",m,sketch,math,objects):op is ExtrudeBossOperation boss?Invoke("ExecuteBossExtrude",m,boss,objects):throw new InvalidOperationException("probe operation"));
                f.Name=op.Name;objects[op.Id]=f;
            }
            var body=(IBody2)((object[])((IPartDoc)m).GetBodies2((int)swBodyType_e.swSolidBody,false))[0];
            var edge=((object[])body.GetEdges()).Cast<IEdge>().Single(e=>e.GetCurve() is ICurve c&&c.IsCircle()&&Math.Abs(((double[])c.CircleParams)[2]-.020)<1e-8);
            var data=(IThreadFeatureData)m.FeatureManager.CreateDefinition((int)swFeatureNameID_e.swFmSweepThread);
            data.InitializeThreadData();data.Type=profile;data.Size="M10x1.5";data.Edge=(Edge)edge;data.EndCondition=(int)swThreadEndCondition_e.swThreadEndCondition_Blind;data.BlindDepth=.010;data.ReverseDirection=false;data.RightHanded=true;data.PitchOverride=true;data.Pitch=.0015;
            Save("thread-request.json",new{data.Type,data.Size,data.Pitch,data.Diameter,data.BlindDepth,data.ReverseDirection,data.RightHanded});
            var feature=m.FeatureManager.CreateFeature(data);
            Save("thread-result.json",new{success=feature is not null,type=feature?.GetTypeName2(),faces=feature?.GetFaces() is object[] ff?ff.Length:0});
            if(feature is not null){int err=0,warn=0;m.Extension.SaveAs(Path.Combine(output,"M10x1.5.SLDPRT"),0,(int)swSaveAsOptions_e.swSaveAsOptions_Silent,null,ref err,ref warn);}
        }
        else if(args[0]=="boundary-new")
        {
            var draft=JsonSerializer.Deserialize<GenericModelDraft>(File.ReadAllText(args[2]),ModelingIrJson.Options)!;
            var plan=new GenericPlanCompiler().Compile(draft).Plan!;
            var kernel=Assembly.Load("CadModeling.Executor.SolidWorks").GetType("SolidWorksComExecutor",true)!;
            object Invoke(string name,params object[] parameters)=>kernel.GetMethod(name,BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,parameters)!;
            var math=(IMathUtility)app.GetMathUtility();
            var m=New();var objects=new Dictionary<string,object>();
            foreach(var op in plan.Operations.Take(plan.Operations.Count-1))
            {
                var f=(IFeature)(op is ProfileSketchOperation sketch?Invoke("ExecuteSketch",m,sketch,math,objects):Invoke("ExecuteNativeFeature",m,op,objects,math));
                f.Name=op.Name;objects[op.Id]=f;
            }
            var o=((NativeFeatureOperation)plan.Operations.Last()).Options;
            m.ClearSelection2(true);int selected=0;
            for(short direction=0;direction<2;direction++)
            {
                var ids=direction==0?o.ProfileIds:o.GuideIds;
                for(short i=0;i<ids.Count;i++)
                {
                    if(!((IFeature)objects[ids[i]]).Select2(selected++>0,direction+1))throw new InvalidOperationException("Boundary selection failed");
                    m.FeatureManager.SetNetBlendCurveData(direction,i,0,0,1,true);
                }
                m.FeatureManager.SetNetBlendDirectionData(direction,32,0,false,false);
            }
            var selections=Enumerable.Range(1,m.ISelectionManager.GetSelectedObjectCount2(-1)).Select(i=>new{mark=m.ISelectionManager.GetSelectedObjectMark(i),type=m.ISelectionManager.GetSelectedObjectType3(i,-1)}).ToArray();
            Save("inputs.json",new{selections,plan});
            var f2=m.FeatureManager.InsertNetBlend2(2,(short)o.ProfileIds.Count,(short)o.GuideIds.Count,false,1,false,o.Merge,false,true,false,0,0,false,0,false,false,0,false,0,true,false);
            Save("boundary.json",new{success=f2 is not null,type=f2?.GetTypeName2(),definition=f2 is null?null:Invoke("ReadBoundaryDefinition",m,f2)});
            if(f2 is not null){int err=0,warn=0;m.Extension.SaveAs(Path.Combine(output,"Boundary.SLDPRT"),0,(int)swSaveAsOptions_e.swSaveAsOptions_Silent,null,ref err,ref warn);}
        }
        else if(args[0]=="sign")
        {
            foreach(var rhs in new[]{"90deg","90°","90","ABS(-7deg)","IIF(4deg > 7deg, 4deg, 7deg)","IIF(4 > 7, 4, 7)"})
            {
                var m=New();var mgr=m.GetEquationMgr();int index=mgr.Add2(-1,"\"函数结果\" = "+rhs,true);
                rows.Add(new{rhs,index,angular_units=mgr.AngularEquationUnits,equations=Equations(m)});Save("sign.json",rows);
                app.CloseDoc(m.GetTitle());documents.Remove(m);
            }
        }
        else if(args[0]=="functions")
        {
            foreach(var rhs in new[]{"SIN(30deg)","SIN(30)","SIN((30deg / 1deg))","COS(60)","TAN(45)","ARCSIN(0.5)","ARCCOS(0.5)","ATN(1)","SQR(4)","EXP(1)","LOG(2.718281828459045)","INT(-1.2)","SGN(0)","ABS(-0.8)","IIF(50mm > 45mm, 50mm, 45mm)","((-2) ^ (3))"})
            {
                var m=New();var mgr=m.GetEquationMgr();int index=mgr.Add2(-1,"\"函数结果\" = "+rhs,true);
                rows.Add(new{rhs,index,angular_units=mgr.AngularEquationUnits,equations=Equations(m)});Save("functions.json",rows);
            }
        }
        else if (args[0] == "disabled")
        {
            var m=New();m.ConfigurationManager.AddConfiguration2("测试配置","","",0,"","",true);
            var mgr=m.GetEquationMgr();
            mgr.Add3(-1,"\"基础宽度\" = 40mm",true,(int)swInConfigurationOpts_e.swAllConfiguration,null);
            mgr.Add3(-1,"\"倍宽\" = (\"基础宽度\" * 2)",true,(int)swInConfigurationOpts_e.swAllConfiguration,null);
            foreach(var phase in new[]{"initial","evaluated","disable","enable"})
            {
                if(phase=="evaluated"){mgr.EvaluateAll();m.ForceRebuild3(false);}
                if(phase=="disable")mgr.Disabled[0]=true;
                if(phase=="enable")mgr.Disabled[0]=false;
                rows.Add(new{phase,count=mgr.GetCount(),disabled_count=mgr.GetDisabledEquationCount(),equations=Equations(m)});
                Save("disabled.json",rows);
            }
        }
        else if (args[0] == "adapter-equations")
        {
            var m = New(); m.ConfigurationManager.AddConfiguration2("测试配置", "", "", 0, "", "", true);
            var type = Assembly.Load("CadModeling.Executor.SolidWorks").GetType("SolidWorksComExecutor", true)!.GetNestedType("SolidWorksDesignIntentSession", BindingFlags.NonPublic)!;
            var session = (IDesignIntentSession)Activator.CreateInstance(type, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, [m], null)!;
            var plan = ModelingIrJson.Deserialize(File.ReadAllText(args[2]));
            foreach (var eq in DesignIntentContract.Compile(plan.DesignIntent!).Equations.Where(e => e.GlobalVariable))
            {
                var before = session.ReadEquations(); string? failure = null;
                try { session.WriteEquation(eq); } catch (InvalidOperationException e) { failure = e.Message; }
                rows.Add(new { requested = eq, before, after = session.ReadEquations(), failure });
                Save("adapter-equations.json", rows);
                if (failure is not null) break;
            }
        }
        else if (args[0] == "equations")
        {
            foreach (var variant in new[] { "delayed", "immediate", "evaluate-between", "ascii-delayed" })
            {
                var m = New(); m.ConfigurationManager.AddConfiguration2("测试配置", "", "", 0, "", "", true);
                var names = variant == "ascii-delayed" ? new[] { "BaseWidth", "DoubleWidth" } : new[] { "基础宽度", "倍宽" };
                var expressions = new[] { $"\"{names[0]}\" = 40mm", $"\"{names[1]}\" = (\"{names[0]}\" * 2)" };
                foreach (var expression in expressions)
                {
                    var mgr = m.GetEquationMgr(); var before = mgr.GetCount();
                    var index = mgr.Add3(-1, expression, variant == "immediate", (int)swInConfigurationOpts_e.swAllConfiguration, null);
                    rows.Add(new { variant, expression, before, index, after = mgr.GetCount(), equations = Equations(m) });
                    Save("equations.json", rows);
                    if (variant == "evaluate-between") { mgr.EvaluateAll(); m.ForceRebuild3(false); }
                }
                Console.WriteLine(variant + " " + JsonSerializer.Serialize(Equations(m)));
            }
        }
        else if (args[0] == "inventory")
        {
            int errors = 0, warnings = 0;
            var m = app.OpenDoc6(Path.GetFullPath(args[2]), (int)swDocumentTypes_e.swDocPART, (int)swOpenDocOptions_e.swOpenDocOptions_Silent | (int)swOpenDocOptions_e.swOpenDocOptions_ReadOnly, "", ref errors, ref warnings) as IModelDoc2;
            if (m is null) throw new InvalidOperationException($"打开失败：{errors}/{warnings}");
            documents.Add(m);
            foreach (IFeature f in (Array)m.FeatureManager.GetFeatures(false))
            {
                var raw = m.Extension.GetPersistReference3(f) as byte[];
                var states = f.IsSuppressed2((int)swInConfigurationOpts_e.swThisConfiguration, null);
                rows.Add(new { name = f.Name, type = f.GetTypeName2(), identity = raw is null ? null : Convert.ToBase64String(raw), states });
            }
            Save("inventory.json", rows); Console.WriteLine(JsonSerializer.Serialize(rows));
        }
        else
        {
            var m = New(); var type = Assembly.Load("CadModeling.Executor.SolidWorks").GetType("SolidWorksComExecutor", true)!.GetNestedType("SolidWorksDesignIntentSession", BindingFlags.NonPublic)!;
            var session = (IDesignIntentSession)Activator.CreateInstance(type, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, [m], null)!;
            session.SelectConfiguration(session.ActiveConfiguration); session.EnsureEditable();
            Save("config-probe.json", new { active = session.ActiveConfiguration, has_design_table = m.Extension.HasDesignTable(), pass = true });
        }
        foreach (var m in documents) app.CloseDoc(m.GetTitle());
        Mark("completed"); return 0;
    }
    catch (Exception e)
    {
        Save("failure.json", new { message = e.ToString(), request_id = request });
        // A local validation rejection has returned from synchronous COM. Close only
        // the probe's owned documents and record its known completion. RPC failures
        // and uncertain native outcomes retain the active lease for a separate audit.
        if(e is InvalidOperationException or ArgumentException)
        {
            foreach(var m in documents)app.CloseDoc(m.GetTitle());
            Mark("completed");Save("known-failure-cleanup.json",new{request_id=request,owned_documents_closed=true,outcome_known=true});
        }
        throw;
    }
}
