using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CadModeling.Core;
using CadModeling.Ir;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

return await ExecutorProgram.RunAsync(args);

[SupportedOSPlatform("windows")]
internal static class ExecutorProgram
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("SolidWorks COM执行器需要Windows。");
            return 2;
        }

        var executor = new SolidWorksComExecutor();
        var command = args.FirstOrDefault()?.ToLowerInvariant() ?? "help";
        switch (command)
        {
            case "serve":
                var pipeName = Option(args, "--pipe") ?? "cad-modeling-solidworks";
                var parentPid = int.TryParse(Option(args, "--parent-pid"), out var parsedParentPid)
                    ? parsedParentPid
                    : (int?)null;
                Console.Error.WriteLine($"SolidWorks 执行器正在监听名为 '{pipeName}' 的命名管道。COM 调用将被序列化。");
                using (var shutdown = new CancellationTokenSource())
                {
                    var parentMonitor = parentPid is null
                        ? Task.CompletedTask
                        : MonitorParentAsync(parentPid.Value, shutdown);
                    try
                    {
                        await new ExecutorPipeServer(pipeName, executor).RunAsync(shutdown.Token);
                    }
                    catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
                    await parentMonitor;
                }
                return 0;
            case "health":
                var healthResponse=await DispatchCliAsync(executor,new("health",DeadlineMilliseconds:5000));
                Console.WriteLine(JsonSerializer.Serialize(healthResponse.Health??new(false,"solidworks-com-service",healthResponse.Error??"健康请求未完成。"),ModelingIrJson.Options));
                return 0;
            case "execute":
                var irPath = Option(args, "--ir");
                if (irPath is null || !File.Exists(irPath))
                {
                    Console.Error.WriteLine("使用方法：execute --ir <absolute-plan.json> [--dry-run]");
                    return 2;
                }
                var plan = ModelingIrJson.Deserialize(await File.ReadAllTextAsync(irPath));
                var response=await DispatchCliAsync(executor,new("execute",plan,args.Contains("--dry-run",StringComparer.OrdinalIgnoreCase),DeadlineMilliseconds:plan.ExecutionDeadline.DeadlineMilliseconds));
                var result=response.Execution??new(false,response.DeadlineExceeded?"deadline_exceeded":"failed",response.Error??"CLI 执行未返回结果。",[]){RequestId=response.RequestId,OutcomeUnknown=response.OutcomeUnknown};
                Console.WriteLine(JsonSerializer.Serialize(result, ModelingIrJson.Options));
                return result.Success ? 0 : 1;
            default:
                Console.WriteLine("用法：\nCadModeling.Executor.SolidWorks\n  serve [--pipe NAME]\n  health\n  execute --ir PLAN.json [--dry-run]");
                return 0;
        }
    }

    private static async Task MonitorParentAsync(int parentPid, CancellationTokenSource shutdown)
    {
        try
        {
            using var parent = Process.GetProcessById(parentPid);
            await parent.WaitForExitAsync(shutdown.Token);
            shutdown.Cancel();
        }
        catch (ArgumentException)
        {
            shutdown.Cancel();
        }
        catch (OperationCanceledException) { }
    }

    private static async Task<ExecutorServiceResponse> DispatchCliAsync(IModelingExecutor executor,ExecutorServiceRequest request)
    {
        var budget=request.DeadlineMilliseconds??300_000;
        if(budget is <1 or >3_600_000)return new(Error:"无效的执行截止时间。");
        request=request with{RequestId=Guid.NewGuid().ToString("N"),DeadlineMilliseconds=budget};
        using var deadline=new CancellationTokenSource(budget);
        var pending=new ExecutorPipeServer("cli",executor).DispatchAsync(request,deadline.Token);
        try{return await pending.WaitAsync(deadline.Token);}
        catch(OperationCanceledException)
        {
            _=pending.ContinueWith(static task=>{_=task.Exception;},TaskContinuationOptions.OnlyOnFaulted);
            return new(Error:"CLI 执行截止时间已过；可能有一个原生调用处于活动状态。在重试前解决持久接收。",RequestId:request.RequestId,ErrorCode:"EXECUTOR_DEADLINE",OutcomeUnknown:true,DeadlineExceeded:true);
        }
    }

    private static string? Option(IReadOnlyList<string> args, string name)
    {
        for (var i = 0; i < args.Count - 1; i++)
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        return null;
    }
}

internal sealed partial class ExecutorPipeServer(string pipeName, IModelingExecutor executor)
{
    private readonly ConcurrentDictionary<string,CancellationTokenSource> _requestTokens=new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string,ExecutionResult> _completedExecutions=new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _completedOrder=new();

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var handlers=new ConcurrentDictionary<int,Task>();var handlerId=0;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var pipe=new NamedPipeServerStream(pipeName,PipeDirection.InOut,NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,PipeOptions.Asynchronous);
                try { await pipe.WaitForConnectionAsync(cancellationToken); }
                catch { await pipe.DisposeAsync();throw; }
                var id=Interlocked.Increment(ref handlerId);
                var task=HandleConnectionAsync(pipe,cancellationToken);
                handlers[id]=task;
                _=task.ContinueWith(_completed=>handlers.TryRemove(id,out var _removed),CancellationToken.None,TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);
            }
        }
        catch(OperationCanceledException) when(cancellationToken.IsCancellationRequested) { }
        finally
        {
            foreach(var cts in _requestTokens.Values)cts.Cancel();
            // Parent exit cancels future work, but cannot interrupt synchronous COM.
            // Keep the process and native lease alive until every accepted delegate
            // returns and writes its durable receipt; no fixed shutdown timeout.
            try { await Task.WhenAll(handlers.Values); } catch { }
        }
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream pipe,CancellationToken serverToken)
    {
        await using var ownedPipe=pipe;
        using var reader=new StreamReader(pipe,Encoding.UTF8,leaveOpen:true);
        using var writer=new StreamWriter(pipe,new UTF8Encoding(false),leaveOpen:true){AutoFlush=true};
        ExecutorServiceResponse response;
        ExecutorServiceRequest? request=null;
        try
        {
            var line=await reader.ReadLineAsync(serverToken);
            request=line is null?null:JsonSerializer.Deserialize<ExecutorServiceRequest>(line,ModelingIrJson.Options);
            response=await DispatchAsync(request,serverToken);
        }
        catch(OperationCanceledException) when(serverToken.IsCancellationRequested)
        { return; }
        catch(Exception ex)
        { response=new(Error:$"执行请求失败：{ex.Message}",RequestId:request?.RequestId,ErrorCode:"EXECUTOR_UNCAUGHT",OutcomeUnknown:request is not null&&request.Action is not ("pause" or "execution_status")); }
        try { await writer.WriteLineAsync(JsonSerializer.Serialize(response,ModelingIrJson.Options)); }
        catch(IOException) { /* Client may disconnect after issuing a pause. Persisted execution status remains queryable. */ }
    }

    private async Task<ExecutorServiceResponse> DispatchCoreAsync(ExecutorServiceRequest? request,CancellationToken serverToken)
    {
        if(request is null)return new(Error:"空执行请求。");
        var action=request.Action.ToLowerInvariant();
        if(action=="pause")
        {
            if(string.IsNullOrWhiteSpace(request.RequestId))return new(Error:"暂停需要 request_id。");
            if(_requestTokens.TryGetValue(request.RequestId,out var active))
            {
                active.Cancel();
                return new(RequestId:request.RequestId,Pending:true,PauseAccepted:true);
            }
            if(_completedExecutions.TryGetValue(request.RequestId,out var alreadyCompleted))
                return new(Execution:alreadyCompleted,RequestId:request.RequestId);
            return new(Error:"没有活跃或已完成的执行存在 request_id。",RequestId:request.RequestId);
        }
        if(action=="execution_status")
        {
            if(string.IsNullOrWhiteSpace(request.RequestId))return new(Error:"execution_status 需要 request_id。");
            if(_completedExecutions.TryGetValue(request.RequestId,out var completed))return new(Execution:completed,RequestId:request.RequestId);
            if(_requestTokens.ContainsKey(request.RequestId))return new(RequestId:request.RequestId,Pending:true);
            return new(Error:"未知执行 request_id。",RequestId:request.RequestId);
        }
        if(action=="execute"&&request.Plan is not null)
        {
            if(string.IsNullOrWhiteSpace(request.RequestId))return new(Error:"执行需要 request_id。");
            using var requestCts=CancellationTokenSource.CreateLinkedTokenSource(serverToken);
            if(!_requestTokens.TryAdd(request.RequestId,requestCts))return new(Error:"复制当前执行 request_id。",RequestId:request.RequestId);
            try
            {
                var execution=await executor.ExecuteAsync(request.Plan,request.DryRun,requestCts.Token);
                _completedExecutions[request.RequestId]=execution;_completedOrder.Enqueue(request.RequestId);TrimCompleted();
                return new(Execution:execution,RequestId:request.RequestId);
            }
            finally { _requestTokens.TryRemove(request.RequestId,out _); }
        }
        return action switch
        {
            "health"=>new(Health:await executor.HealthAsync(serverToken)),
            "inspect" when request.Inspection is not null=>new(Inspection:await executor.InspectAsync(request.Inspection,serverToken)),
            "observe" when request.Observation is not null=>new(Observation:await executor.CaptureObservationAsync(request.Observation,serverToken)),
            "projection" when request.Projection is not null=>new(Projection:await executor.CaptureProjectionAsync(request.Projection,serverToken)),
            "section" when request.Section is not null=>new(Section:await executor.CaptureSectionAsync(request.Section,serverToken)),
            "drawing" when request.Drawing is not null=>new(Drawing:await executor.ExportDrawingAsync(request.Drawing,serverToken)),
            "assembly" when request.Assembly is not null=>new(Assembly:await executor.BuildAssemblyAsync(request.Assembly,serverToken)),
            _=>new(Error:"未知的操作或缺少建模 IR 计划。")
        };
    }

    private void TrimCompleted()
    {
        while(_completedExecutions.Count>256&&_completedOrder.TryDequeue(out var old))_completedExecutions.TryRemove(old,out _);
    }
}

[SupportedOSPlatform("windows")]
internal sealed partial class SolidWorksComExecutor : IModelingExecutor, IDisposable
{
    private readonly SemaphoreSlim _serialGate = new(1, 1);
    private readonly StaWorker _sta = new();

    public async Task<ExecutorHealth> HealthAsync(CancellationToken cancellationToken = default)
    {
        await _serialGate.WaitAsync(cancellationToken);
        try { return await _sta.InvokeAsync(CheckHealth, cancellationToken); }
        finally { _serialGate.Release(); }
    }

    public async Task<ExecutionResult> ExecuteAsync(ModelingPlan plan, bool dryRun, CancellationToken cancellationToken = default)
    {
        await _serialGate.WaitAsync(cancellationToken);
        try
        {
            var validation = new ModelingIrValidator().Validate(plan, forExecution: true);
            if (!validation.IsValid)
                return new(false, "rejected", "IR 验证在 COM 执行之前失败。",
                    validation.Diagnostics.Select(x => new ExecutionEvidence(
                        "validation", $"{x.Code}: {x.Message}", false, Code: x.Code,
                        Category: ExecutionFailureCategory.Validation,
                        SuggestedAction: x.SuggestedAction)).ToArray(),
                    PlanFingerprint: ModelingPlanIdentity.Fingerprint(plan));
            if (dryRun)
                return new(true, "dry_run", "IR 验证；SolidWorks 未被修改。",
                    plan.Operations.Select(x => new ExecutionEvidence("operation", $"将执行{x.Id}:{x.Name}。", true)).ToArray(),
                    plan.Output.NativePath, PlanFingerprint: ModelingPlanIdentity.Fingerprint(plan));
            // Once COM work is queued, cancellation becomes a pause request. Do not cancel the STA
            // delegate itself: a synchronous SolidWorks call must be allowed to reach a known boundary.
            return await _sta.InvokeAsync(() => ExecuteOnSta(plan,cancellationToken), CancellationToken.None);
        }
        finally { _serialGate.Release(); }
    }

    private static ExecutorHealth CheckHealth()
    {
        try
        {
            using var startupDialog = SolidWorksStartupDialogSuppressor.Start();
            var type = Type.GetTypeFromProgID("SldWorks.Application", throwOnError: false);
            if (type is null) return new(false, "solidworks-com", "SldWorks.Application 未注册。");
            var app = (SldWorks?)Activator.CreateInstance(type)
                      ?? throw new InvalidOperationException("COM 激活返回 null。");
            try
            {
                var suppression = startupDialog.WaitForResult(TimeSpan.FromSeconds(5));
                string revision = app.RevisionNumber() ?? "unknown";
                var active = app.IActiveDoc2;
                string? title = active?.GetTitle();
                var message = suppression.DialogHidden
                    ? suppression.NotificationAudioSilenced
                        ? "SolidWorks COM 连接成功；已抑制已知的虚假 .NET 框架启动对话框及其通知声音。"
                        : "SolidWorks COM连接成功；已隐藏已知的.NET框架启动对话框。"
                    : "SolidWorks 成功连接 COM。";
                return new(true, "solidworks-com", message, revision, title);
            }
            finally { ReleaseCom(app); }
        }
        catch (Exception ex)
        {
            return new(false, "solidworks-com", $"SolidWorks 连接失败：{ex.Message}");
        }
    }

    private static ExecutionResult ExecuteOnSta(ModelingPlan plan,CancellationToken cancellationToken)
    {
        using var timing = CadModeling.Ir.PerformanceTrace.Begin("native.execute");
        var evidence = new List<ExecutionEvidence>();
        GeometrySnapshot? geometry = null;
        IReadOnlyList<StableFeatureReference>? featureReferences = null;
        var planFingerprint = ModelingPlanIdentity.Fingerprint(plan);
        SldWorks? app = null;
        IModelDoc2? model = null;
        bool? originalInputDimension = null;
        string? activeOperationId = null;
        string? checkpointPath = null;
        ModelingCheckpointManifest? checkpoint = null;
        ModelVerificationResult? verification = null;
        try
        {
            if(plan.Recovery.ResumeManifestPath is { } resumePath)
            {
                checkpoint=ModelingRecovery.ReadAndValidate(plan,resumePath);
                checkpointPath=resumePath;
            }
            using var startupDialog = SolidWorksStartupDialogSuppressor.Start();
            var type = Type.GetTypeFromProgID("SldWorks.Application", throwOnError: true)
                       ?? throw new InvalidOperationException("SldWorks.Application 未注册。");
            app = (SldWorks?)Activator.CreateInstance(type) ?? throw new InvalidOperationException("COM 激活返回 null。");
            var suppression = startupDialog.WaitForResult(TimeSpan.FromSeconds(5));
            if (suppression.DialogHidden)
                evidence.Add(Pass("startup_dialog",
                    suppression.NotificationAudioSilenced
                        ? "抑制了已知的虚假 .NET 框架启动对话框及其通知声音，而没有确认该对话框。"
                        : "隐藏了已知的虚假.NET框架启动对话框而无需承认它。",
                    ("notification_audio", suppression.NotificationAudioSilenced ? "silenced-and-restored" : "not-confirmed")));
            app.Visible = true;
            if(checkpoint is not null)
            {
                var currentEnvironment=new CheckpointEnvironmentIdentity
                {
                    CompilerVersion=ComponentContentIdentity.ForType(typeof(GenericPlanCompiler),"generic-plan-compiler/v1"),
                    ExecutorVersion=ComponentContentIdentity.ForType(typeof(SolidWorksComExecutor),"solidworks-executor/v1"),
                    VerifierVersion=ComponentContentIdentity.ForType(typeof(ModelVerification),"model-verification/v1"),
                    RulesFingerprint=ComponentContentIdentity.Rules(ModelingCheckpointManifest.CurrentFormatVersion.ToString(),"resume-native-prefix-v2","saved-model-reopen-v1"),
                    SolidWorksRevision=app.RevisionNumber()
                };
                var resumeDecision=ModelingRecovery.DecideResume(plan,checkpoint,currentEnvironment:currentEnvironment);
                if(resumeDecision.RequiresCleanRebuild)
                    throw new InvalidOperationException("检查点执行环境过时："+string.Join(", ",resumeDecision.InvalidationReasons));
                evidence.Add(Pass("checkpoint_resume_decision","检查点源/前缀/环境标识在原生重用前重新评估。",
                    ("reused_operations",string.Join(",",resumeDecision.ReusedOperationIds)),
                    ("reverification_evidence",string.Join(",",resumeDecision.ReverificationEvidenceIds)),
                    ("reasons",string.Join(",",resumeDecision.InvalidationReasons))));
            }
            var targetName=Path.GetFileName(plan.Output.NativePath);
            foreach(var open in (app.GetDocuments() as object[]??[]).Cast<IModelDoc2>())
                if(Path.GetFileName(open.GetPathName()).Equals(targetName,StringComparison.OrdinalIgnoreCase)||open.GetTitle().Equals(targetName,StringComparison.OrdinalIgnoreCase))
                    throw new CadExecutionException("OUTPUT_DOCUMENT_OPEN",ExecutionFailureCategory.Output,false,
                        "一个具有请求的输出文件名的文档已经在SolidWorks中打开了。","使用唯一的输出文件名，以确保现有文档保持不变。");
            originalInputDimension=app.GetUserPreferenceToggle((int)swUserPreferenceToggle_e.swInputDimValOnCreate);
            app.SetUserPreferenceToggle((int)swUserPreferenceToggle_e.swInputDimValOnCreate,false);
            evidence.Add(Pass("connect", "连接到 SolidWorks COM。", ("revision", app.RevisionNumber() ?? "unknown")));

            if ((checkpoint?.NativePath??plan.SourceModelPath) is { } sourceModel)
            {
                var target = Path.GetFullPath(plan.Output.NativePath!);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(sourceModel, target, plan.Output.OverwriteAllowed);
                int openErrors=0,openWarnings=0;
                model=(IModelDoc2?)PerformanceTrace.Measure("native.open", () => app.OpenDoc6(target,(int)swDocumentTypes_e.swDocPART,(int)swOpenDocOptions_e.swOpenDocOptions_Silent,"",ref openErrors,ref openWarnings));
                if(model is null) throw new IOException($"无法打开模型副本（错误码:{openErrors}）。");
                if(!Path.GetFullPath(model.GetPathName()).Equals(target,StringComparison.OrdinalIgnoreCase))
                { model=null; throw new IOException("SolidWorks 返回了一个未编辑的文档而不是编辑副本。使用一个唯一的输出文件名。"); }
                evidence.Add(Pass("document", "打开了一个独立的编辑副本模型。", ("source",sourceModel),("output",target)));
            }
            else
            {
                var template = FindPartTemplate(app);
                model = (IModelDoc2?)app.NewDocument(template, 0, 0d, 0d);
                if (model is null) throw new InvalidOperationException("NewDocument 返回 null。检查配置的零件模板。");
                evidence.Add(Pass("document", "创建了一个新的部件文档。", ("template", template)));
            }

            var operationObjects = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            var completed=checkpoint?.CompletedOperationCount??0;
            if(checkpoint is not null)
            {
                RestoreCheckpointFeatures(model,checkpoint,operationObjects);
                evidence.Add(Pass("checkpoint_resume","使用了保存的前缀，并恢复了其实特征引用。",("completed_operations",completed.ToString(CultureInfo.InvariantCulture)),("manifest",checkpointPath!)));
            }
            var checkpointAfter=ModelingRecovery.CheckpointAfter(plan);
            var mathUtility = (IMathUtility)app.GetMathUtility();
            var boxEdgeHistory = plan.BoxEdgeHistory is null ? null : new ControlledBoxEdgeHistory(model,plan);
            foreach (var operation in plan.Operations.Skip(completed))
            {
                if(cancellationToken.IsCancellationRequested)
                    return PauseAtBoundary("暂停请求在提交下一条 SolidWorks 操作之前。",null);
                activeOperationId=operation.Id;
                try
                {
                    using var operationTiming = PerformanceTrace.Begin("native.feature_com");
                    switch (operation)
                    {
                        case NativeFeatureOperation native:
                            var executableNative = boxEdgeHistory?.Prepare(model,native) ?? native;
                            operationObjects[native.Id] = ExecuteNativeFeature(model, executableNative, operationObjects, mathUtility);
                            boxEdgeHistory?.RecordOperation(model,native,(IFeature)operationObjects[native.Id]);
                            evidence.Add(Pass("feature", $"已创建 {native.Options.Kind}：{native.Name}。", ("id", native.Id)));
                            break;
                        case ProfileSketchOperation sketch:
                            operationObjects[sketch.Id] = ExecuteSketch(model, sketch, mathUtility, operationObjects);
                            evidence.Add(Pass("sketch", $"已创建轮廓草图{sketch.Name}。", ("id", sketch.Id)));
                            break;
                        case ExtrudeBossOperation extrude:
                            var feature = ExecuteBossExtrude(model, extrude, operationObjects);
                            operationObjects[extrude.Id] = feature;
                            evidence.Add(Pass("feature", $"创建凸台放样 '{extrude.Name}'.",
                                ("depth_mm", extrude.DepthMm.ToString(CultureInfo.InvariantCulture)),
                                ("end_condition", extrude.EndCondition.ToString())));
                            break;
                        case ExtrudeCutOperation cut:
                            var cutFeature = ExecuteCutExtrude(model, cut, operationObjects);
                            operationObjects[cut.Id] = cutFeature;
                            evidence.Add(Pass("feature", $"创建切削拉伸 '{cut.Name}'.",
                                ("depth_mm", cut.DepthMm.ToString(CultureInfo.InvariantCulture)),
                                ("end_condition", cut.EndCondition.ToString())));
                            break;
                        default:
                            throw new NotSupportedException($"不支持的操作类型{operation.GetType().Name}。");
                    }
                }
                catch (Exception ex) when (ex is not CadExecutionException)
                {
                    throw WrapOperationFailure(operation, ex);
                }
                completed++;
                activeOperationId=null;
                if(checkpointAfter.Contains(operation.Id))
                {
                    try
                    {
                        (checkpointPath,checkpoint)=SaveCheckpoint(app,model,plan,completed,operationObjects);
                        evidence.Add(Pass("checkpoint_saved","保存并重新打开了可重用前缀模型。",("operation_id",operation.Id),("manifest",checkpointPath)));
                    }
                    catch(Exception checkpointError)
                    {
                        // Optional checkpoint failure must not fabricate a recoverable state or destroy a previous checkpoint.
                        evidence.Add(new("checkpoint_unavailable",checkpointError.Message,true,Code:"CHECKPOINT_UNAVAILABLE",SuggestedAction:"使用最后有效的检查点如果存在；否则重建。"));
                    }
                }
                if(cancellationToken.IsCancellationRequested)
                {
                    // The in-flight synchronous COM call has returned, so its outcome is now known.
                    // Capture this exact boundary when possible; never submit the next operation.
                    if(checkpoint?.CompletedOperationCount!=completed)
                    {
                        try
                        {
                            (checkpointPath,checkpoint)=SaveCheckpoint(app,model,plan,completed,operationObjects);
                            evidence.Add(Pass("pause_checkpoint","暂停到达已确认的COM边界；保存并重新打开了已完成的前缀。",
                                ("completed_operations",completed.ToString(CultureInfo.InvariantCulture)),("manifest",checkpointPath)));
                        }
                        catch(Exception checkpointError)
                        {
                            evidence.Add(new("pause_checkpoint_unavailable",checkpointError.Message,true,Code:"PAUSE_CHECKPOINT_UNAVAILABLE",
                                SuggestedAction:"仅从最近的有效检查点继续；否则重建未检查点的前缀。"));
                        }
                    }
                    return PauseAtBoundary("暂停请求在/之后一个操作 SolidWorks 进行时；该请求到达已知边界，没有随后的操作被提交。",operation.Id);
                }
            }

            activeOperationId=null;
            if(cancellationToken.IsCancellationRequested)return PauseAtBoundary("停止在最终验证之前；未提交进一步的COM阶段。",null);

            DesignIntentReceipt? designIntentReceipt = null;
            if (plan.DesignIntent is { } designIntent)
            {
                var beforeIntentReferences=CaptureFeatureReferences(model,plan,operationObjects);
                designIntentReceipt = DesignIntentExecution.Apply(new SolidWorksDesignIntentSession(model), designIntent, cancellationToken);
                // Configuration changes can disconnect creation-time feature wrappers.
                // Resolve the same native objects from their pre-mutation persistent IDs.
                foreach(var prior in beforeIntentReferences)
                {
                    if(prior.PersistentReferenceBase64 is not { } encoded)
                        throw new InvalidOperationException("DESIGN_INTENT_FEATURE_IDENTITY：配置更新前特征身份不可用。");
                    var feature=model.Extension.GetObjectByPersistReference3(Convert.FromBase64String(encoded),out var state) as IFeature;
                    // Suppressed features can be unavailable to GetObjectByPersistReference3.
                    // Reacquire from the complete live tree, then still require native identity equality.
                    if(state!=0 || feature is null)feature=FindFinalDefinitionFeature(model,prior.SolidWorksName);
                    if(feature.Name!=prior.SolidWorksName
                        || !SamePersistentIdentity(model,encoded,RequireDefinitionIdentity(model,feature)))
                        throw new InvalidOperationException("DESIGN_INTENT_FEATURE_IDENTITY：配置更新后无法重取相同原生特征。");
                    operationObjects[prior.OperationId]=feature;
                }
                evidence.Add(Pass("design_intent", "方程及配置驱动值已逐项读回；待保存重开复核。",
                    ("receipts", JsonSerializer.Serialize(designIntentReceipt, ModelingIrJson.Options))));
            }

            var rebuildOk = Convert.ToBoolean(PerformanceTrace.Measure("native.rebuild", () => model.ForceRebuild3(false)));
            if (!rebuildOk) throw new InvalidOperationException("ForceRebuild3 报告失败。");
            evidence.Add(Pass("rebuild", "重建完成，未报告错误 SolidWorks。"));

            featureReferences = CaptureFeatureReferences(model, plan, operationObjects);
            evidence.Add(Pass("stable_references", "将映射的 IR 操作 ID 跟踪到 SolidWorks 特征标识和持久引用上。",
                ("count", featureReferences.Count.ToString(CultureInfo.InvariantCulture)),
                ("persistent_count", featureReferences.Count(x => x.PersistentReferenceBase64 is not null).ToString(CultureInfo.InvariantCulture))));
            VerifyExpectedFeatures(model, plan.Acceptance, evidence);
            geometry = MeasureGeometry(model);
            evidence.Add(Pass("geometry_snapshot", "测量 SolidWorks 几何和质量属性。",
                ("solid_body_count", geometry.SolidBodyCount.ToString(CultureInfo.InvariantCulture)),
                ("face_count", geometry.FaceCount.ToString(CultureInfo.InvariantCulture)),
                ("edge_count", geometry.EdgeCount.ToString(CultureInfo.InvariantCulture)),
                ("volume_mm3", geometry.VolumeMm3.ToString("0.###", CultureInfo.InvariantCulture)),
                ("surface_area_mm2", geometry.SurfaceAreaMm2.ToString("0.###", CultureInfo.InvariantCulture)),
                ("bounding_box_mm", FormatBounds(geometry.BoundingBoxMm))));
            VerifyGeometryQuality(geometry, plan.Acceptance, evidence);
            var nativeValidity=RequireValidNativeBodies(model);
            evidence.Add(Pass("native_body_validity","对全部可见及隐藏实体/曲面体执行 Check3，未发现原生故障。",
                ("body_count",nativeValidity.BodyCount.ToString(CultureInfo.InvariantCulture)),("fault_count","0")));
            if(ModelVerification.HasChecks(plan.Verification))
            {
                verification=VerifySourceRequirements(model,plan.Verification,geometry,out _);
                foreach(var check in verification.Checks)
                {
                    var measurements=check.Measurements is null?new Dictionary<string,string>():new Dictionary<string,string>(check.Measurements);
                    measurements["check_id"]=check.Id;
                    var affected=plan.DrawingContext?.Features.Where(f=>f.VerificationCheckIds.Contains(check.Id)).SelectMany(f=>f.OperationIds).Distinct().ToArray()??[];
                    measurements["affected_operation_ids"]=string.Join(",",affected);
                    evidence.Add(new("source_requirement",check.Message,check.Passed,measurements,check.Passed?null:"SOURCE_REQUIREMENT_MISMATCH",ExecutionFailureCategory.GeometryQuality,
                        !check.Passed,"检测测量特征的几何形状是否符合源要求；不要削弱期望。",affected.Length==1?affected[0]:null));
                }
                if(!verification.Passed) throw new CadExecutionException("SOURCE_REQUIREMENT_MISMATCH",ExecutionFailureCategory.GeometryQuality,true,
                    "实际模型不符合声明来源的要求。","纠正违规特征并从兼容的检查点重建。");
            }
            var output = Path.GetFullPath(plan.Output.NativePath!);
            if(cancellationToken.IsCancellationRequested)return PauseAtBoundary("停止在验证后，但在原生输出保存之前。",null);
            EnforceAllowedOutputRoot(output);
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            var saveErrors = 0;
            var saveWarnings = 0;
            PrepareModelPresentation(model);
            var saveOk = PerformanceTrace.Measure("native.save", () => model.Extension.SaveAs(output, 0, 1, null, ref saveErrors, ref saveWarnings));
            if (!saveOk || saveErrors!=0 || !File.Exists(output) || new FileInfo(output).Length == 0)
                throw new IOException($"IModelDocExtension.SaveAs 失败 (错误={saveErrors}，警告={saveWarnings}).");

            foreach (var exportPath in plan.Output.ExportPaths)
            {
                if(cancellationToken.IsCancellationRequested)return PauseAtBoundary("停止在下次导出之前；部分输出被保留。",null);
                var export = Path.GetFullPath(exportPath);
                EnforceAllowedOutputRoot(export);
                Directory.CreateDirectory(Path.GetDirectoryName(export)!);
                var exportErrors = 0;
                var exportWarnings = 0;
                var exportOk = PerformanceTrace.Measure("native.save", () => model.Extension.SaveAs(export, 0, 1, null, ref exportErrors, ref exportWarnings));
                if (!exportOk || !File.Exists(export) || new FileInfo(export).Length == 0)
                    throw new IOException($"导出 SaveAs 失败，'{export}' (错误={exportErrors}，警告={exportWarnings})。");
                evidence.Add(Pass("export", "导出了一个额外的CAD 工件。",
                    ("path", export), ("bytes", new FileInfo(export).Length.ToString(CultureInfo.InvariantCulture)),
                    ("warnings", exportWarnings.ToString(CultureInfo.InvariantCulture))));
            }

            var previewPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var view in new[]
                     {
                         (Name: "isometric", Id: (int)swStandardViews_e.swIsometricView),
                         (Name: "front", Id: (int)swStandardViews_e.swFrontView),
                         (Name: "top", Id: (int)swStandardViews_e.swTopView),
                         (Name: "right", Id: (int)swStandardViews_e.swRightView),
                         (Name: "left", Id: (int)swStandardViews_e.swLeftView)
                     })
            {
                var preview = Path.Combine(
                    Path.GetDirectoryName(output)!,
                    $"{Path.GetFileNameWithoutExtension(output)}.{view.Name}.bmp");
                if(cancellationToken.IsCancellationRequested)return PauseAtBoundary("停止在下一次预览之前；部分输出被保留。",null);
                model.ShowNamedView2("", view.Id);
                model.ViewZoomtofit2();
                model.GraphicsRedraw2();
                var previewOk = Convert.ToBoolean(model.SaveBMP(preview, 1280, 960));
                if (!previewOk || !File.Exists(preview) || new FileInfo(preview).Length == 0)
                    throw new IOException($"SolidWorks 未导出非空的{view.Name}预览。");
                previewPaths[view.Name] = preview;
                evidence.Add(Pass("preview", $"已导出{view.Name}审阅预览。",
                    ("view", view.Name), ("path", preview),
                    ("bytes", new FileInfo(preview).Length.ToString(CultureInfo.InvariantCulture))));
            }

            PrepareModelPresentation(model);
            if (!PerformanceTrace.Measure("native.save", () => model.Save3((int)swSaveAsOptions_e.swSaveAsOptions_Silent, ref saveErrors, ref saveWarnings)) || saveErrors != 0)
                throw new IOException($"无法保存最终模型展示（错误={saveErrors}，警告={saveWarnings}）。");
            evidence.Add(Pass("presentation", "保存了一个隐藏了构造几何的等轴测视图；没有抑制特征。"));

            var artifactStem = Path.Combine(Path.GetDirectoryName(output)!, Path.GetFileNameWithoutExtension(output));
            var parametersPath = artifactStem + "_parameters.json";
            File.WriteAllText(parametersPath, ModelingIrJson.Serialize(plan));
            if (new FileInfo(parametersPath).Length == 0)
                throw new IOException("参数报告为空。");
            evidence.Add(Pass("parameters", "保存了建模参数和假设。",
                ("path", parametersPath), ("bytes", new FileInfo(parametersPath).Length.ToString(CultureInfo.InvariantCulture))));

            var reviewPath = artifactStem + "_review_report.json";
            var expectedOutputs = new List<string> { output, parametersPath };
            expectedOutputs.AddRange(plan.Output.ExportPaths);
            expectedOutputs.AddRange(previewPaths.Values);
            var advancedDefinitions=CaptureAdvancedDefinitions(model,plan,featureReferences);
            if(advancedDefinitions.Count>0)evidence.Add(Pass("advanced_definition_baseline",
                "捕获最终保存状态的原生高级定义；待保存重开后逐项核对。",
                ("receipts",JsonSerializer.Serialize(advancedDefinitions,ModelingIrJson.Options))));
            var createdTitle = model.GetTitle();
            GeometryDocumentIdentity? semanticEndpoint = null;
            if (boxEdgeHistory is not null)
            {
                var persisted = boxEdgeHistory.Complete(model); semanticEndpoint = persisted.Endpoint;
                var referencePath = artifactStem + "_semantic_reference.json";
                File.WriteAllText(referencePath,JsonSerializer.Serialize(boxEdgeHistory.RootReference,ModelingIrJson.Options));
                expectedOutputs.Add(referencePath);
                evidence.Add(Pass("semantic_history","生产端捕获完整受控尺寸／单边圆角历史，保存不可变检查点及本地认证记录。",
                    ("reference_path",referencePath),("history_record_path",persisted.RecordPath),
                    ("document_revision",semanticEndpoint.DocumentRevision!),("source_revision_id",semanticEndpoint.SourceRevisionId!)));
            }
            PerformanceTrace.Measure("native.close", () => app.CloseDoc(createdTitle));
            ReleaseCom(model);
            model = null;
            if(cancellationToken.IsCancellationRequested)return PauseAtBoundary("停止在保存模型检查之前；部分输出被保留。",null);
            var reopened=InspectOnStaWithCleanValidation(new(output,Verification:plan.Verification,
                GeometryReferences:boxEdgeHistory is null?null:[boxEdgeHistory.RootReference],
                DocumentRevision:semanticEndpoint?.DocumentRevision,SourceRevisionId:semanticEndpoint?.SourceRevisionId),app,
                saved=>
                {
                    if (plan.DesignIntent is { } intent && designIntentReceipt is not null)
                        DesignIntentExecution.VerifySaved(new SolidWorksDesignIntentSession(saved), intent, designIntentReceipt);
                    VerifyAdvancedDefinitions(saved,plan,advancedDefinitions,true);
                },saved=>VerifyCleanSavedBoundaryDefinitions(saved,advancedDefinitions));
            if(!reopened.Success || !reopened.ModelReopened || !reopened.RebuildSucceeded || !reopened.CaptureComplete
                || reopened.Geometry is null || reopened.BodyValidity is not {Passed:true})
                throw new CadExecutionException("SAVED_MODEL_INVALID",ExecutionFailureCategory.GeometryQuality,true,
                    "最终原生文件重开验收失败："+reopened.Message,"保留失败证据；不要将输出作为已验收结果交付。");
            var reopenedNames=(reopened.Features??[]).Select(f=>f.Name).ToHashSet(StringComparer.Ordinal);
            if(plan.Acceptance.ExpectedFeatures.Concat((featureReferences??[]).Select(f=>f.SolidWorksName)).Any(name=>!reopenedNames.Contains(name)))
                throw new InvalidOperationException("SAVED_FEATURE_MISMATCH: 重开后的最终文件缺少预期特征。");
            VerifyGeometryQuality(reopened.Geometry,plan.Acceptance,evidence);
            SavedModelRequirements.Validate(plan,reopened);
            if (boxEdgeHistory is not null && (reopened.GeometryRefResolutions.Count != 1
                || reopened.GeometryRefResolutions[0] is not { Status:GeometryRefResolutionStatus.Resolved,Rebound:true }
                || !GeometryRefResolver.IsVerifiedResolution(reopened.GeometryRefResolutions[0])))
                throw new InvalidOperationException("生产持久历史未能在最终保存重开后通过公开引用解析及完整回执重验。");
            verification=reopened.Verification;
            if(ModelVerification.HasChecks(plan.Verification) && verification is not {Passed:true})
                throw new CadExecutionException("SAVED_REQUIREMENT_MISMATCH",ExecutionFailureCategory.GeometryQuality,true,
                    "保存的模型读回未能满足源要求。","检验保存的结果；不要将其作为验证的结果交付。");
            evidence.Add(Pass("saved_model_readback","最终文件已重新打开、重建并通过特征、原生实体有效性及基础几何验收。",
                ("sha256",reopened.ModelSha256??"")));
            if(advancedDefinitions.Count>0)evidence.Add(Pass("saved_advanced_definitions",
                "高级特征的持久身份及最终原生控制已保存重开核对；曲面控制读回不替代几何连续性测量。",
                ("count",advancedDefinitions.Count.ToString(CultureInfo.InvariantCulture))));
            var review = new
            {
                evaluation = new { status = "pass", message = "重建、特征、几何质量、导出和预览检查通过。" },
                checks = new
                {
                    rebuild_without_errors = true,
                    expected_features_exist = true,
                    expected_solid_body_count = geometry.SolidBodyCount == plan.Acceptance.Geometry.ExpectedSolidBodyCount,
                    positive_volume = !plan.Acceptance.Geometry.RequirePositiveVolume || geometry.VolumeMm3 > 0,
                    valid_topology = nativeValidity.Passed,
                    expected_outputs_exist = expectedOutputs.All(path => File.Exists(path)),
                    previews_created = previewPaths.Count == 5,
                    previews_not_blank = previewPaths.Values.All(path => new FileInfo(path).Length > 0)
                },
                model = new
                {
                    name = plan.Name,
                    features = plan.Acceptance.ExpectedFeatures,
                    expected_bounding_box_mm = plan.Acceptance.ExpectedBoundingBoxMm,
                    geometry,
                    stable_feature_references = featureReferences,
                    plan_fingerprint = planFingerprint,
                    assumptions = plan.Assumptions
                },
                saved_model_readback = reopened,
                saved_advanced_definitions = advancedDefinitions,
                saved_design_intent = designIntentReceipt,
                source_requirement_verification = verification,
                artifacts = new { native = output, exports = plan.Output.ExportPaths, previews = previewPaths, parameters = parametersPath }
            };
            File.WriteAllText(reviewPath, JsonSerializer.Serialize(review,
                new JsonSerializerOptions(ModelingIrJson.Options) { WriteIndented = true }));
            evidence.Add(Pass("review_report", "写完后飞行报告，在保存模型验证之后。",
                ("path", reviewPath), ("bytes", new FileInfo(reviewPath).Length.ToString(CultureInfo.InvariantCulture))));
            evidence.Add(Pass("save", "保存了原生的 SolidWorks 部件，并且仅关闭了本次运行创建的文档。",
                ("path", output), ("bytes", new FileInfo(output).Length.ToString(CultureInfo.InvariantCulture)),
                ("sha256", Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(output)))),
                ("warnings", saveWarnings.ToString(CultureInfo.InvariantCulture))));

            if(cancellationToken.IsCancellationRequested)return PauseAtBoundary("停止在最后一次读回后；不要将过期请求报告为按时的成功。",null);
            return new(true, "completed", "SolidWorks 创建、重建、测量并保存了该模型。",
                evidence, output, geometry, featureReferences, planFingerprint) {Verification=verification,Recovery=RecoveryState(plan,checkpointPath,checkpoint,null)};

            ExecutionResult PauseAtBoundary(string message,string? completedInFlightOperationId)
            {
                evidence.Add(new("paused",message,true,new Dictionary<string,string>
                {
                    ["completed_operations"]=completed.ToString(CultureInfo.InvariantCulture),
                    ["completed_in_flight_operation_id"]=completedInFlightOperationId??string.Empty,
                    ["next_operation_submitted"]="false"
                },Code:"EXECUTION_PAUSED",SuggestedAction:"从可用的恢复检查点中恢复；否则重建。"));
                if(app is not null&&model is not null)
                {
                    try { PerformanceTrace.Measure("native.close", () => app.CloseDoc(model.GetTitle())); } catch(COMException) { }
                    ReleaseCom(model);model=null;
                }
                return new(false,"paused",message,evidence,null,geometry,featureReferences,planFingerprint)
                { Verification=verification,Recovery=RecoveryState(plan,checkpointPath,checkpoint,null) };
            }
        }
        catch (Exception ex)
        {
            var failure = ClassifyFailure(ex);
            if(failure.OperationId is null&&activeOperationId is not null) failure=failure with {OperationId=activeOperationId};
            evidence.Add(new("failed", failure.Message, false, failure.Data, failure.Code, failure.Category,
                failure.Retryable, failure.SuggestedAction, failure.OperationId));
            if (app is not null && model is not null)
            {
                try
                {
                    var failedTitle = model.GetTitle();
                    PerformanceTrace.Measure("native.close", () => app.CloseDoc(failedTitle));
                    evidence.Add(Pass("cleanup", "关闭了未保存的文档，该文档由失败运行创建。", ("title", failedTitle)));
                }
                catch (COMException) { }
                ReleaseCom(model);
                model = null;
            }
            return new(false, "failed", "SolidWorks 执行在第一个失败的检查点处停止。", evidence,
                null, geometry, featureReferences, planFingerprint) {Verification=verification,Recovery=RecoveryState(plan,checkpointPath,checkpoint,failure.OperationId)};
        }
        finally
        {
            if(app is not null && originalInputDimension is { } inputDimension)
                try { app.SetUserPreferenceToggle((int)swUserPreferenceToggle_e.swInputDimValOnCreate,inputDimension); } catch(COMException) { }
            ReleaseCom(model);
            ReleaseCom(app);
        }
    }

    private static object ExecuteSketch(
        IModelDoc2 model,
        ProfileSketchOperation operation,
        IMathUtility mathUtility,
        IReadOnlyDictionary<string, object> objects)
    {
        model.ClearSelection2(true);
        _activeFrame = operation.Frame;
        if (operation.PlaneId is { } planeId)
        {
            SelectFeature(model, ResolveFeature(model, objects, planeId), false, 0);
        }
        else if (operation.Frame is { } frame)
        {
            var planeFeature = CreateFramePlane(model, frame, operation.Name + "_基准面");
            SelectFeature(model, planeFeature, false, 0);
        }
        else if (operation.FaceAttachment is { } attachment)
        {
            if (!objects.ContainsKey(attachment.SupportOperationId))
                throw new InvalidOperationException(
                    $"面附着的草图支持 '{attachment.SupportOperationId}' 未创建。");
            var selectedFace = Convert.ToBoolean(model.Extension.SelectByID2(
                string.Empty, "FACE", Mm(attachment.PickXmm), Mm(attachment.PickYmm), Mm(attachment.PickZmm), false, 0, null, 0));
            if (!selectedFace)
                throw new InvalidOperationException(
                    $"无法在 ({attachment.PickXmm:R},{attachment.PickYmm:R},{attachment.PickZmm:R}) mm 处选择平面支撑面。");
        }
        else
        {
            var candidates = operation.Plane switch
            {
                ReferencePlane.Front => new[] { "Front Plane", "前视基准面" },
                ReferencePlane.Top => new[] { "Top Plane", "上视基准面" },
                ReferencePlane.Right => new[] { "Right Plane", "右视基准面" },
                _ => throw new ArgumentOutOfRangeException(nameof(operation.Plane))
            };
            var selectedPlane = candidates.Any(name => Convert.ToBoolean(
                model.Extension.SelectByID2(name, "PLANE", 0d, 0d, 0d, false, 0, null, 0)));
            if (!selectedPlane) throw new InvalidOperationException($"无法在本语言的 SolidWorks 参考平面{operation.Plane}中选择。");
        }

        model.SketchManager.InsertSketch(true);
        var activeSketch = (ISketch?)model.IGetActiveSketch2()
                           ?? throw new InvalidOperationException("SolidWorks 未在 InsertSketch 后暴露活动草图。");
        var sketchToModel = activeSketch.ModelToSketchTransform.IInverse();
        var primitiveSegments = new List<object[]>();
        var sketchManager = model.SketchManager;
        var previousAddToDb = sketchManager.AddToDB;
        var previousDisplayWhenAdded = sketchManager.DisplayWhenAdded;
        // Typed coordinates must not depend on zoom, window visibility, snapping or automatic relations.
        // Restore both preferences before applying the explicitly requested constraints and dimensions.
        try
        {
        sketchManager.AddToDB = true;
        sketchManager.DisplayWhenAdded = false;
        foreach (var primitive in operation.Primitives)
        {
            var before = (activeSketch.GetSketchSegments() as object[] ?? []).Cast<ISketchSegment>().ToArray();
            object? created = primitive switch
            {
                CenteredRectangleProfile rectangle => CreateRectangle(model, mathUtility, sketchToModel, operation.Plane, rectangle),
                ThreePointRectangleProfile rectangle => CreateThreePointRectangle(model, mathUtility, sketchToModel, activeSketch, operation.Plane, rectangle),
                CircleProfile circle => CreateCircle(model, mathUtility, sketchToModel, operation.Plane, circle),
                PolygonProfile polygon => CreatePolygon(model, mathUtility, sketchToModel, activeSketch, operation.Plane, polygon),
                CompositeCurveProfile composite => CreateCompositeCurve(model, mathUtility, sketchToModel, operation.Plane, composite),
                EllipseProfile ellipse => CreateEllipse(model, mathUtility, sketchToModel, operation.Plane, ellipse),
                OpenCurveProfile open => CreateOpenCurve(model, mathUtility, sketchToModel, operation.Plane, open),
                SplineProfile spline => CreateSpline(model, mathUtility, sketchToModel, operation.Plane, spline),
                SketchPointsProfile points => points.Points.Select(p=>
                {
                    var q=PointInSketch(mathUtility,sketchToModel,operation.Plane,p.Xmm,p.Ymm);
                    return (object)(model.SketchManager.CreatePoint(q.X,q.Y,q.Z) ?? throw new InvalidOperationException("草图点创建失败。"));
                }).ToArray(),
                _ => throw new NotSupportedException($"不支持的轮廓原语{primitive.GetType().Name}。")
            };
            if (created is null) throw new InvalidOperationException($"SolidWorks 失败创建{primitive.GetType().Name}。");
            // Composite segment indices are source-ordered. Native sketch enumeration
            // can reorder arcs/lines, binding driving dimensions to the wrong entity.
            primitiveSegments.Add(primitive is SketchPointsProfile or CompositeCurveProfile ? (object[])created : (activeSketch.GetSketchSegments() as object[] ?? []).Cast<ISketchSegment>().Except(before).Cast<object>().ToArray());
        }
        }
        finally
        {
            sketchManager.DisplayWhenAdded = previousDisplayWhenAdded;
            sketchManager.AddToDB = previousAddToDb;
        }
        ApplySketchEditing(model, activeSketch, operation, primitiveSegments, mathUtility, sketchToModel);
        if(operation.AutoDimensionPrimitives)DimensionSimplePrimitives(model,activeSketch,operation,primitiveSegments);
        if(operation.RequireFullyDefined && activeSketch.GetConstrainedStatus()!=(int)swConstrainedStatus_e.swFullyConstrained)
            throw new InvalidOperationException("SKETCH_NOT_FULLY_DEFINED: 草图未通过完全定义验收（原生状态="+activeSketch.GetConstrainedStatus()+"）。");
        model.SketchManager.InsertSketch(true);
        var sketchFeature = model.IFeatureByPositionReverse(0)
                            ?? throw new InvalidOperationException("无法获取新创建的草图特征。");
        sketchFeature.Name = operation.Name;
        return sketchFeature;
    }

    private static object CreateRectangle(
        IModelDoc2 model,
        IMathUtility mathUtility,
        MathTransform sketchToModel,
        ReferencePlane plane,
        CenteredRectangleProfile rectangle)
    {
        var center = PointInSketch(mathUtility, sketchToModel, plane, rectangle.CenterXmm, rectangle.CenterYmm);
        var corner = PointInSketch(mathUtility, sketchToModel, plane,
            rectangle.CenterXmm + rectangle.WidthMm / 2d,
            rectangle.CenterYmm + rectangle.HeightMm / 2d);
        return model.SketchManager.CreateCenterRectangle(
                   center.X, center.Y, center.Z, corner.X, corner.Y, corner.Z)
               ?? throw new InvalidOperationException("CreateCenterRectangle 返回 null。");
    }

    private static object CreateCircle(
        IModelDoc2 model,
        IMathUtility mathUtility,
        MathTransform sketchToModel,
        ReferencePlane plane,
        CircleProfile circle)
    {
        var center = PointInSketch(mathUtility, sketchToModel, plane, circle.CenterXmm, circle.CenterYmm);
        return model.SketchManager.CreateCircleByRadius(center.X, center.Y, center.Z, Mm(circle.DiameterMm) / 2d)
               ?? throw new InvalidOperationException("CreateCircleByRadius 返回 null。");
    }

    private static object CreateThreePointRectangle(
        IModelDoc2 model,
        IMathUtility mathUtility,
        MathTransform sketchToModel,
        ISketch activeSketch,
        ReferencePlane plane,
        ThreePointRectangleProfile rectangle)
    {
        var first = PointInSketch(mathUtility, sketchToModel, plane, rectangle.Corner1.Xmm, rectangle.Corner1.Ymm);
        var second = PointInSketch(mathUtility, sketchToModel, plane, rectangle.Corner2.Xmm, rectangle.Corner2.Ymm);
        var third = PointInSketch(mathUtility, sketchToModel, plane, rectangle.Corner3.Xmm, rectangle.Corner3.Ymm);
        var beforeCount = SketchSegmentCount(activeSketch);
        var created = model.SketchManager.Create3PointCornerRectangle(
            first.X, first.Y, first.Z,
            second.X, second.Y, second.Z,
            third.X, third.Y, third.Z);
        var afterCount = SketchSegmentCount(activeSketch);
        if (created is null && afterCount <= beforeCount)
            throw new InvalidOperationException(
                $"创建3点角落矩形未添加几何形状；草图段数保持为{beforeCount}。");
        return created ?? activeSketch;
    }

    private static object CreatePolygon(
        IModelDoc2 model,
        IMathUtility mathUtility,
        MathTransform sketchToModel,
        ISketch activeSketch,
        ReferencePlane plane,
        PolygonProfile polygon)
    {
        var sketchManager = model.SketchManager;
        var previousAddToDatabase = sketchManager.AddToDB;
        sketchManager.AddToDB = true;
        try
        {
            var sketchPoints = polygon.Points.Select(point =>
                PointInSketch(mathUtility, sketchToModel, plane, point.Xmm, point.Ymm)).ToArray();
            for (var index = 0; index < polygon.Points.Count; index++)
            {
                var start = sketchPoints[index];
                var end = sketchPoints[(index + 1) % sketchPoints.Length];
                // ISketchManager takes sketch coordinates. The legacy IModelDoc2
                // point/line overload can fault on an arbitrary reference plane.
                _ = sketchManager.CreateLine(start.X, start.Y, start.Z, end.X, end.Y, end.Z)
                    ?? throw new InvalidOperationException($"CreateLine 未添加 polygon segment{index}。");
            }
        }
        finally
        {
            sketchManager.AddToDB = previousAddToDatabase;
        }
        return activeSketch;
    }

    private static object CreateCompositeCurve(
        IModelDoc2 model,
        IMathUtility mathUtility,
        MathTransform sketchToModel,
        ReferencePlane plane,
        CompositeCurveProfile composite)
    {
        var sketchManager = model.SketchManager;
        var orderedSegments = new List<object>();
        var previousAddToDatabase = sketchManager.AddToDB;
        sketchManager.AddToDB = true;
        try
        {
            foreach (var curve in composite.Curves)
            {
                var start = PointInSketch(mathUtility, sketchToModel, plane, curve.Start.Xmm, curve.Start.Ymm);
                var end = PointInSketch(mathUtility, sketchToModel, plane, curve.End.Xmm, curve.End.Ymm);
                object? created = curve switch
                {
                    LineProfileCurve => sketchManager.CreateLine(start.X, start.Y, start.Z, end.X, end.Y, end.Z),
                    ThreePointArcProfileCurve arc => CreateThreePointArc(sketchManager, mathUtility, sketchToModel, plane, start, end, arc),
                    _ => throw new NotSupportedException($"不支持的组合轮廓曲线{curve.GetType().Name}。")
                };
                if (created is null)
                    throw new InvalidOperationException($"SolidWorks 失败在复合曲线轮廓中创建{curve.GetType().Name}。");
                orderedSegments.Add(created);
            }
        }
        finally
        {
            sketchManager.AddToDB = previousAddToDatabase;
        }
        return orderedSegments.ToArray();
    }

    private static object CreateThreePointArc(
        ISketchManager sketchManager,
        IMathUtility mathUtility,
        MathTransform sketchToModel,
        ReferencePlane plane,
        SketchPoint start,
        SketchPoint end,
        ThreePointArcProfileCurve arc)
    {
        var onArc = PointInSketch(mathUtility, sketchToModel, plane, arc.PointOnArc.Xmm, arc.PointOnArc.Ymm);
        return sketchManager.Create3PointArc(
                   start.X, start.Y, start.Z,
                   end.X, end.Y, end.Z,
                   onArc.X, onArc.Y, onArc.Z)
               ?? throw new InvalidOperationException("Create3PointArc 返回 null。");
    }

    private static int SketchSegmentCount(ISketch sketch)
    {
        var value = sketch.GetSketchSegments();
        return value is Array segments ? segments.Length : 0;
    }

    private static object ExecuteBossExtrude(IModelDoc2 model, ExtrudeBossOperation operation, IReadOnlyDictionary<string, object> objects)
    {
        model.ClearSelection2(true);
        var sketchFeature = (IFeature)objects[operation.SketchId];
        if (!Convert.ToBoolean(sketchFeature.Select2(false, 0)))
            throw new InvalidOperationException($"无法为拉伸选择草图 '{operation.SketchId}' 。");
        SelectEndReference(model, operation.EndCondition, operation.EndReference, objects);
        var endCondition = EndCondition(operation.EndCondition);
        var startCondition = operation.StartOffsetMm > 0
            ? (int)swStartConditions_e.swStartOffset
            : (int)swStartConditions_e.swStartSketchPlane;
        var feature = (IFeature?)model.FeatureManager.FeatureExtrusion3(
            true, false, operation.ReverseDirection, endCondition, 0, Mm(operation.DepthMm), 0d,
            false, false, false, false, 0d, 0d, false, false, false, false,
            operation.Merge, false, true, startCondition, Mm(operation.StartOffsetMm), operation.ReverseStartOffset);
        if (feature is null) throw new InvalidOperationException("FeatureExtrusion3 返回 null。");
        feature.Name = operation.Name;
        return feature;
    }

    private static object ExecuteCutExtrude(IModelDoc2 model, ExtrudeCutOperation operation, IReadOnlyDictionary<string, object> objects)
    {
        model.ClearSelection2(true);
        var sketchFeature = (IFeature)objects[operation.SketchId];
        if (!Convert.ToBoolean(sketchFeature.Select2(false, 0)))
            throw new InvalidOperationException($"无法为切削拉伸选择草图 '{operation.SketchId}' 。");
        SelectEndReference(model, operation.EndCondition, operation.EndReference, objects);
        var endCondition = EndCondition(operation.EndCondition);
        var startCondition = operation.StartOffsetMm > 0
            ? (int)swStartConditions_e.swStartOffset
            : (int)swStartConditions_e.swStartSketchPlane;
        var feature = (IFeature?)model.FeatureManager.FeatureCut4(
            true, false, !operation.ReverseDirection, endCondition, 0, Mm(operation.DepthMm), 0d,
            false, false, false, false, 0d, 0d, false, false, false, false,
            false, false, true, false, false, false, startCondition, Mm(operation.StartOffsetMm), operation.ReverseStartOffset, true);
        if (feature is null) throw new InvalidOperationException("FeatureCut4 返回 null。");
        feature.Name = operation.Name;
        return feature;
    }

    private static int EndCondition(ExtrudeEndCondition condition) => condition switch
    {
        ExtrudeEndCondition.Blind => (int)swEndConditions_e.swEndCondBlind,
        ExtrudeEndCondition.MidPlane => (int)swEndConditions_e.swEndCondMidPlane,
        ExtrudeEndCondition.UpToSurface => (int)swEndConditions_e.swEndCondUpToSurface,
        ExtrudeEndCondition.ThroughAll => (int)swEndConditions_e.swEndCondThroughAll,
        ExtrudeEndCondition.UpToNext => (int)swEndConditions_e.swEndCondUpToNext,
        _ => throw new NotSupportedException($"不支持的拉伸端条件{condition}。")
    };

    private static void SelectEndReference(
        IModelDoc2 model,
        ExtrudeEndCondition endCondition,
        PlanarFaceReference? reference,
        IReadOnlyDictionary<string, object> objects)
    {
        if (endCondition != ExtrudeEndCondition.UpToSurface) return;
        if (reference is null)
            throw new InvalidOperationException("UpToSurface 拉伸操作需要一个端参考。");
        if (!objects.TryGetValue(reference.SupportOperationId, out var supportObject) || supportObject is not IFeature)
            throw new InvalidOperationException(
                $"端参考支撑 '{reference.SupportOperationId}' 未创建。");
        var targetX = Mm(reference.PickXmm);
        var targetY = Mm(reference.PickYmm);
        var targetZ = Mm(reference.PickZmm);
        var part = (IPartDoc)model;
        // Resolve against the current solid because downstream cuts can split or replace faces first created
        // by the anchored support feature. The support id identifies the owning feature.
        var bodies = part.GetBodies2((int)swBodyType_e.swSolidBody, true) as Array;
        IFace2? nearestFace = null;
        var nearestDistanceSquared = double.PositiveInfinity;
        if (bodies is not null)
        {
            foreach (var face in bodies.Cast<object>().OfType<IBody2>()
                         .SelectMany(body => (body.GetFaces() as Array)?.Cast<object>().OfType<IFace2>() ?? []))
            {
                if (face.GetClosestPointOn(targetX, targetY, targetZ) is not double[] closest || closest.Length < 3)
                    continue;
                var dx = closest[0] - targetX;
                var dy = closest[1] - targetY;
                var dz = closest[2] - targetZ;
                var distanceSquared = (dx * dx) + (dy * dy) + (dz * dz);
                if (distanceSquared >= nearestDistanceSquared) continue;
                nearestFace = face;
                nearestDistanceSquared = distanceSquared;
            }
        }
        const double facePointToleranceMeters = 1e-6;
        if (nearestFace is null || nearestDistanceSquared > facePointToleranceMeters * facePointToleranceMeters)
        {
            var rawBox = part.GetPartBox(true) as double[];
            var box = rawBox is { Length: >= 6 }
                ? $"[{rawBox[0] * 1000d:R}, {rawBox[1] * 1000d:R}, {rawBox[2] * 1000d:R}]..[{rawBox[3] * 1000d:R}, {rawBox[4] * 1000d:R}, {rawBox[5] * 1000d:R}] mm"
                : "unavailable";
            throw new InvalidOperationException(
                $"无法确定由基准面'{reference.SupportOperationId}'锚定的拉伸端面在 ({reference.PickXmm:R},{reference.PickYmm:R},{reference.PickZmm:R}) mm处；最近的面距离为{Math.Sqrt(nearestDistanceSquared) * 1000d:R}mm，当前的固体盒为{box}。");
        }
        var selectData = ((ISelectionMgr)model.SelectionManager).CreateSelectData();
        selectData.Mark = 1;
        var selected = ((IEntity)nearestFace).Select4(true, selectData);
        if (!selected)
            throw new InvalidOperationException(
                $"无法在 ({reference.PickXmm:R},{reference.PickYmm:R},{reference.PickZmm:R}) mm 处选择拉伸的端面。");
    }

    private static SketchPoint PointInSketch(
        IMathUtility mathUtility,
        MathTransform sketchToModel,
        ReferencePlane plane,
        double uMm,
        double vMm)
    {
        if (_activeFrame is { } frame)
        {
            var (u, v, _) = FrameBasis(frame);
            var origin = frame.OriginMm;
            var point = (IMathPoint)mathUtility.CreatePoint(new[] {
                Mm(origin.X + u.X*uMm + v.X*vMm), Mm(origin.Y + u.Y*uMm + v.Y*vMm), Mm(origin.Z + u.Z*uMm + v.Z*vMm) });
            var local = (IMathPoint)point.MultiplyTransform(sketchToModel.IInverse());
            var data = (double[])local.ArrayData;
            // Creation APIs consume 2D sketch coordinates. Remove only floating-point plane residuals;
            // a genuinely inconsistent frame must fail rather than being projected onto another plane.
            if (Math.Abs(data[2]) > 1e-8)
                throw new InvalidOperationException($"草图框架点离当前草图平面{data[2]:R}m。");
            return new(data[0], data[1], 0d);
        }
        var (sketchU, sketchV) = plane switch
        {
            ReferencePlane.Front => (uMm, vMm),
            ReferencePlane.Top => (uMm, -vMm),
            ReferencePlane.Right => (-uMm, vMm),
            _ => throw new ArgumentOutOfRangeException(nameof(plane))
        };
        // SketchManager creation APIs consume coordinates in the active sketch coordinate system.
        // Top-plane IR coordinates are (width, depth); Right-plane coordinates are (depth, height).
        return new(Mm(sketchU), Mm(sketchV), 0d);
    }

    private static GeometrySnapshot MeasureGeometry(IModelDoc2 model)
    {
        model.ClearSelection2(true);
        var part = (IPartDoc)model;
        var bodies = new List<IBody2>();
        var rawBodies = part.GetBodies2((int)swBodyType_e.swSolidBody, false);
        if (rawBodies is Array bodyArray)
            bodies.AddRange(bodyArray.Cast<object>().OfType<IBody2>());
        var solidBodyCount=bodies.Count;
        var surfaces=part.GetBodies2((int)swBodyType_e.swSheetBody,false) as object[] ?? [];
        bodies.AddRange(surfaces.Cast<IBody2>());
        if(bodies.Count==0) throw new InvalidOperationException("重建模型中不存在实体或曲面体。");

        try
        {
            // PartBox can include visible construction sketches/axes. Measure bodies only.
            var box = new[] { double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity,
                double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity };
            foreach (var body in bodies)
            for (var axis = 0; axis < 3; axis++)
            foreach (var sign in new[] { -1, 1 })
            {
                if (!body.GetExtremePoint(axis == 0 ? sign : 0, axis == 1 ? sign : 0, axis == 2 ? sign : 0,
                    out var x, out var y, out var z)) throw new InvalidOperationException("无法测量体的端点。");
                var value = axis == 0 ? x : axis == 1 ? y : z;
                box[axis] = Math.Min(box[axis], value); box[axis + 3] = Math.Max(box[axis + 3], value);
            }
            if (box.Length < 6)
                throw new CadExecutionException("GEOMETRY_ENVELOPE_UNAVAILABLE", ExecutionFailureCategory.GeometryQuality,
                    false, "SolidWorks 返回了一个不完整的部分包围盒。",
                    "重建零件，并检查是否存在有效的实体体。");

            IMassProperty2? massProperty = null;
            try
            {
                massProperty = solidBodyCount>0 ? model.Extension.CreateMassProperty2() as IMassProperty2 : null;
                if (massProperty is null && solidBodyCount>0)
                    throw new CadExecutionException("MASS_PROPERTIES_UNAVAILABLE", ExecutionFailureCategory.GeometryQuality,
                        true, "无法在SolidWorks创建质量属性评估器。",
                        "重建模型并重试；验证文档包含一个实体体。");
                if(massProperty is not null)
                {
                    massProperty.UseSystemUnits=true;
                    massProperty.IncludeHiddenBodiesOrComponents=true;
                    massProperty.AccuracyLevel=(int)swMassPropertyAccuracyLevel_e.swMassPropertyAccuracyLevel_Higher;
                    if(!massProperty.Recalculate()) throw new InvalidOperationException("无法重新计算质量属性。");
                }
                var center = massProperty is not null?ToDoubles(massProperty.CenterOfMass, 3, "质心"):new double[3];
                var area=bodies.SelectMany(b=>(b.GetFaces() as object[] ?? []).Cast<IFace2>()).Sum(f=>f.GetArea());
                if(solidBodyCount==0)
                {
                    var sheetMass=bodies.Select(b=>(double[])b.GetMassProperties(1)).ToArray();
                    var totalArea=sheetMass.Sum(m=>m[3]);
                    if(totalArea<=0) throw new InvalidOperationException("曲面体没有可测量的面积。");
                    center=Enumerable.Range(0,3).Select(axis=>sheetMass.Sum(m=>m[axis]*m[3])/totalArea).ToArray();
                }
                return new(
                    solidBodyCount,
                    bodies.Sum(body => body.GetFaceCount()),
                    bodies.Sum(body => body.GetEdgeCount()),
                    (massProperty?.Volume??0) * 1_000_000_000d,
                    area * 1_000_000d,
                    new(center[0] * 1000d, center[1] * 1000d, center[2] * 1000d),
                    new(
                        Math.Abs(box[3] - box[0]) * 1000d,
                        Math.Abs(box[4] - box[1]) * 1000d,
                        Math.Abs(box[5] - box[2]) * 1000d)) { SurfaceBodyCount=surfaces.Length };
            }
            finally
            {
                ReleaseCom(massProperty);
            }
        }
        finally
        {
            foreach (var body in bodies) ReleaseCom(body);
        }
    }

    private static void VerifyGeometryQuality(GeometrySnapshot geometry, AcceptanceSpec acceptance, List<ExecutionEvidence> evidence)
    {
        var quality = acceptance.Geometry;
        if(quality.ExpectedCenterOfMassMm is { } expectedCenter)
        {
            var measured=geometry.CenterOfMassMm;
            var error=Math.Max(Math.Abs(measured.X-expectedCenter.X),Math.Max(Math.Abs(measured.Y-expectedCenter.Y),Math.Abs(measured.Z-expectedCenter.Z)));
            if(!double.IsFinite(error) || error>quality.CenterOfMassToleranceMm)
                throw new CadExecutionException("CENTER_OF_MASS_MISMATCH",ExecutionFailureCategory.GeometryQuality,true,
                    "质心测量值与请求的位置不同。","检查体的平移、旋转和材料分布。");
            evidence.Add(Pass("center_of_mass","测量的质心匹配到指定的位置。"));
        }
        if(quality.ExpectedSurfaceBodyCount is { } expectedSurfaces && geometry.SurfaceBodyCount!=expectedSurfaces)
            throw new InvalidOperationException($"预期为曲面体{expectedSurfaces}，测量值为{geometry.SurfaceBodyCount}。");
        if (geometry.SolidBodyCount != quality.ExpectedSolidBodyCount)
            throw new CadExecutionException("SOLID_BODY_COUNT_MISMATCH", ExecutionFailureCategory.GeometryQuality,
                true,
                $"预期为{quality.ExpectedSolidBodyCount}个实体/实体，测量为{geometry.SolidBodyCount}。",
                "检查断开的圆柱特征、失败的合并或移除完整体的切割。",
                data: new Dictionary<string, string>
                {
                    ["expected"] = quality.ExpectedSolidBodyCount.ToString(CultureInfo.InvariantCulture),
                    ["measured"] = geometry.SolidBodyCount.ToString(CultureInfo.InvariantCulture)
                });
        if (quality.RequirePositiveVolume && (!double.IsFinite(geometry.VolumeMm3) || geometry.VolumeMm3 <= 0))
            throw new CadExecutionException("NON_POSITIVE_VOLUME", ExecutionFailureCategory.GeometryQuality,
                true, "模型零件没有有限正体积。",
                "检查轮廓闭合、凸台方向、切削深度和实体合并设置。");
        if (quality.RequireValidTopology && (geometry.FaceCount <= 0 || geometry.EdgeCount < 0))
            throw new CadExecutionException("INVALID_TOPOLOGY", ExecutionFailureCategory.GeometryQuality,
                true, "所建模的实体没有可用的面/边拓扑。",
                "重建特征链，并检查首先创建无效几何的操作。");

        if (acceptance.ExpectedBoundingBoxMm is { } expectedBounds)
        {
            var measured = new[] { geometry.BoundingBoxMm.X, geometry.BoundingBoxMm.Y, geometry.BoundingBoxMm.Z };
            var expected = new[] { expectedBounds.X, expectedBounds.Y, expectedBounds.Z };
            var passed = measured.Zip(expected).All(pair =>
                Math.Abs(pair.First - pair.Second) <= acceptance.BoundingBoxToleranceMm);
            evidence.Add(new ExecutionEvidence(
                Stage: "bounding_box",
                Message: passed ? "测量的包围盒符合IR接受合同。" : "测量的包围盒与IR接受合同不同。",
                Passed: passed,
                Data: new Dictionary<string, string>
                {
                    ["measured_mm"] = FormatBounds(geometry.BoundingBoxMm),
                    ["expected_mm"] = FormatBounds(expectedBounds),
                    ["tolerance_mm"] = acceptance.BoundingBoxToleranceMm.ToString(CultureInfo.InvariantCulture)
                },
                Code: passed ? null : "BOUNDING_BOX_MISMATCH",
                Category: passed ? null : ExecutionFailureCategory.GeometryQuality,
                Retryable: !passed,
                SuggestedAction: passed ? null : "检查草图方向、拉伸方向、尺寸和特征偏移。"));
            if (!passed)
                throw new CadExecutionException("BOUNDING_BOX_MISMATCH", ExecutionFailureCategory.GeometryQuality,
                    true, "包围盒接受性检查失败。",
                    "检查草图方向、拉伸方向、尺寸和特征偏移。");
        }

        if (quality.ExpectedVolumeMm3 is { } expectedVolume)
        {
            var tolerance = expectedVolume * quality.VolumeTolerancePercent / 100d;
            var passed = Math.Abs(geometry.VolumeMm3 - expectedVolume) <= tolerance;
            evidence.Add(new ExecutionEvidence(
                Stage: "volume",
                Message: passed ? "测量体积符合IR接受合同。" : "测量体积与IR接受合同不同。",
                Passed: passed,
                Data: new Dictionary<string, string>
                {
                    ["measured_mm3"] = geometry.VolumeMm3.ToString("0.###", CultureInfo.InvariantCulture),
                    ["expected_mm3"] = expectedVolume.ToString("0.###", CultureInfo.InvariantCulture),
                    ["tolerance_percent"] = quality.VolumeTolerancePercent.ToString(CultureInfo.InvariantCulture)
                },
                Code: passed ? null : "VOLUME_MISMATCH",
                Category: passed ? null : ExecutionFailureCategory.GeometryQuality,
                Retryable: !passed,
                SuggestedAction: passed ? null : "检查缺少或多余的切削、孔和凸台特征；然后修订建模 IR。"));
            if (!passed)
                throw new CadExecutionException("VOLUME_MISMATCH", ExecutionFailureCategory.GeometryQuality,
                    true, "体积验收检查失败。",
                    "检查缺少或多余的切削、孔和凸台特征；然后修订建模 IR。");
        }

        evidence.Add(Pass("geometry_quality", "体的数量、体积和拓扑检查通过。"));
    }

    private static IReadOnlyList<StableFeatureReference> CaptureFeatureReferences(
        IModelDoc2 model,
        ModelingPlan plan,
        IReadOnlyDictionary<string, object> operationObjects)
    {
        var references = new List<StableFeatureReference>();
        foreach (var operation in plan.Operations)
        {
            if (!operationObjects.TryGetValue(operation.Id, out var value) || value is not IFeature feature) continue;
            string? persistentReference = null;
            try
            {
                var raw = model.Extension.GetPersistReference3(feature);
                var bytes = raw switch
                {
                    byte[] direct => direct,
                    Array array => array.Cast<object>().Select(Convert.ToByte).ToArray(),
                    _ => []
                };
                if (bytes.Length > 0) persistentReference = Convert.ToBase64String(bytes);
            }
            catch (COMException)
            {
                // The operation/name mapping remains useful if this SolidWorks build cannot persist the object.
            }

            references.Add(new(
                operation.Id,
                operation.Name,
                operation.GetType().Name,
                feature.Name,
                feature.GetID(),
                persistentReference,
                operation.DependsOn));
        }
        return references;
    }

    private static double[] ToDoubles(object raw, int minimumLength, string name)
    {
        if (raw is not Array array)
            throw new CadExecutionException("GEOMETRY_DATA_INVALID", ExecutionFailureCategory.GeometryQuality,
                false, $"SolidWorks 返回无效的{name}数据。", "重建模型后再试。");
        var values = array.Cast<object>().Select(Convert.ToDouble).ToArray();
        if (values.Length < minimumLength)
            throw new CadExecutionException("GEOMETRY_DATA_INCOMPLETE", ExecutionFailureCategory.GeometryQuality,
                false, $"SolidWorks 返回不完整的{name}数据。", "重建模型后再试。");
        return values;
    }

    private static string FormatBounds(BoundingBoxSpec bounds) => string.Join(" x ", new[]
    {
        bounds.X.ToString("0.###", CultureInfo.InvariantCulture),
        bounds.Y.ToString("0.###", CultureInfo.InvariantCulture),
        bounds.Z.ToString("0.###", CultureInfo.InvariantCulture)
    });

    private static void VerifyExpectedFeatures(IModelDoc2 model, AcceptanceSpec acceptance, List<ExecutionEvidence> evidence)
    {
        if (acceptance.ExpectedFeatures.Count == 0) return;
        var actual = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rawFeatures = model.FeatureManager.GetFeatures(false);
        if (rawFeatures is Array features)
        {
            foreach (var raw in features)
            {
                if (raw is not IFeature feature) continue;
                actual.Add(feature.Name);
                ReleaseCom(feature);
            }
        }
        var missing = acceptance.ExpectedFeatures.Where(expected => !actual.Contains(expected)).ToList();
        var passed = missing.Count == 0;
        evidence.Add(new("feature_tree",
            passed ? "所有由IR接受合同命名的特征存在于SolidWorks特征树中。"
                   : "一个或多个预期的 SolidWorks 特征缺失。",
            passed,
            new Dictionary<string, string>
            {
                ["expected_count"] = acceptance.ExpectedFeatures.Count.ToString(CultureInfo.InvariantCulture),
                ["missing"] = missing.Count == 0 ? "<none>" : string.Join(", ", missing)
            }));
        if (!passed) throw new InvalidOperationException("特征树接受检查失败。");
    }

    private static string FindPartTemplate(ISldWorks app)
    {
        var configured = System.Environment.GetEnvironmentVariable("SOLIDWORKS_PART_TEMPLATE");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return Path.GetFullPath(configured);
        var defaultPart = app.GetUserPreferenceStringValue((int)swUserPreferenceStringValue_e.swDefaultTemplatePart);
        if (!string.IsNullOrWhiteSpace(defaultPart) && File.Exists(defaultPart)) return Path.GetFullPath(defaultPart);
        var defaultSetting = app.GetUserPreferenceStringValue((int)swUserPreferenceStringValue_e.swFileLocationsDocumentTemplates);
        foreach (var candidate in (defaultSetting ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var expanded = System.Environment.ExpandEnvironmentVariables(candidate.Trim('"'));
            if (File.Exists(expanded)) return expanded;
            if (Directory.Exists(expanded))
            {
                var template = Directory.EnumerateFiles(expanded, "*.prtdot").FirstOrDefault();
                if (template is not null) return template;
            }
        }
        var programDataRoot = Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.CommonApplicationData), "SOLIDWORKS");
        if (Directory.Exists(programDataRoot))
        {
            var standardTemplate = Directory.EnumerateFiles(programDataRoot, "*.prtdot", SearchOption.AllDirectories)
                .OrderBy(path => path.Contains($"特征{Path.DirectorySeparatorChar}MBD{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault();
            if (standardTemplate is not null) return standardTemplate;
        }
        throw new FileNotFoundException("未找到 SolidWorks 部件模板。将 SOLIDWORKS_PART_TEMPLATE 设置为绝对的 .prtdot 路径。");
    }

    private static void EnforceAllowedOutputRoot(string output)
    {
        var configuredRoot = System.Environment.GetEnvironmentVariable("CAD_ALLOWED_OUTPUT_ROOT");
        if (string.IsNullOrWhiteSpace(configuredRoot)) return;
        var root = Path.GetFullPath(configuredRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!output.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException($"输出超出 CAD_ALLOWED_OUTPUT_ROOT '{root}.'.");
    }

    private static double Mm(double value) => value / 1000d;

    private readonly record struct SketchPoint(double X, double Y, double Z);

    private static CadExecutionException WrapOperationFailure(ModelingOperation operation, Exception exception)
    {
        var referenceFailure = exception is EntitySelectionException || exception.Message.Contains("select", StringComparison.OrdinalIgnoreCase) ||
                               exception.Message.Contains("reference", StringComparison.OrdinalIgnoreCase) ||
                               exception.Message.Contains("选择", StringComparison.Ordinal) ||
                               exception.Message.Contains("引用", StringComparison.Ordinal) ||
                               exception.Message.Contains("基准", StringComparison.Ordinal);
        return new(
            referenceFailure ? "REFERENCE_RESOLUTION_FAILED" : "FEATURE_CREATION_FAILED",
            referenceFailure ? ExecutionFailureCategory.ReferenceResolution : ExecutionFailureCategory.FeatureCreation,
            true,
            $"操作 {operation.Id}（{operation.Name}）失败：{exception.Message}",
            referenceFailure
                ? "解决引用的草图/平面/特征与当前模型状态，然后重试修订的 IR。"
                : "检查该操作的尺寸和依赖关系，修订建模 IR，从干净的文档重新开始。",
            operation.Id,
            new Dictionary<string, string>
            {
                ["operation_type"] = operation.GetType().Name,
                ["exception"] = exception.GetType().Name,
                ["hresult"] = $"0x{exception.HResult:X8}",
                ["depends_on"] = string.Join(",",operation.DependsOn)
            }.Concat(exception is EntitySelectionException selection ? selection.Diagnostics : new Dictionary<string,string>()).ToDictionary(x=>x.Key,x=>x.Value),
            exception);
    }

    private static ExecutionFailure ClassifyFailure(Exception exception)
    {
        if (exception is CadExecutionException cad)
        {
            var data = cad.FailureData is null
                ? new Dictionary<string, string>()
                : new Dictionary<string, string>(cad.FailureData);
            data["exception"] = cad.InnerException?.GetType().Name ?? cad.GetType().Name;
            return new(cad.Code, cad.Category, cad.Retryable, cad.Message, cad.SuggestedAction,
                cad.OperationId, data);
        }

        return exception switch
        {
            FileNotFoundException => new("ENVIRONMENT_FILE_MISSING", ExecutionFailureCategory.Environment, false,
                exception.Message, "验证包含 SolidWorks 的部件模板，并且配置的文件系统路径。", null,
                FailureData(exception)),
            UnauthorizedAccessException => new("OUTPUT_NOT_AUTHORIZED", ExecutionFailureCategory.Output, false,
                exception.Message, "选择 CAD_ALLOWED_OUTPUT_ROOT 内的输出路径，并确认是否覆盖现有文件。", null,
                FailureData(exception)),
            IOException => new("OUTPUT_IO_FAILED", ExecutionFailureCategory.Output, true,
                exception.Message, "检查目的地是否可写且未被锁定，然后以版本化路径重试。", null,
                FailureData(exception)),
            COMException => new("SOLIDWORKS_COM_FAILED", ExecutionFailureCategory.SolidWorksInterop, true,
                exception.Message, "检查 SolidWorks 状态并重建错误，然后从一个干净的文档重新开始。", null,
                FailureData(exception)),
            InvalidComObjectException => new("SOLIDWORKS_OBJECT_LIFETIME_FAILED", ExecutionFailureCategory.SolidWorksInterop, true,
                exception.Message, "重试从一个干净的文档开始，并在执行器中检查 COM 对象释放的顺序。", null,
                FailureData(exception)),
            InvalidOperationException when exception.Message.Contains("ForceRebuild3", StringComparison.OrdinalIgnoreCase) =>
                new("REBUILD_FAILED", ExecutionFailureCategory.Rebuild, true, exception.Message,
                    "检查最后成功的特征，并修订第一个失败的操作，在建模IR中。", null,
                    FailureData(exception)),
            _ => new("EXECUTION_FAILED", ExecutionFailureCategory.Unknown, false, exception.Message,
                "检查结构证据和执行日志后再更改建模 IR。", null,
                FailureData(exception))
        };

        static IReadOnlyDictionary<string, string> FailureData(Exception ex) =>
            new Dictionary<string, string> { ["exception"] = ex.GetType().Name };
    }

    private static ExecutionEvidence Pass(string stage, string message, params (string Key, string Value)[] data) =>
        new(stage, message, true, data.Length == 0 ? null : data.ToDictionary(x => x.Key, x => x.Value));

    private sealed class CadExecutionException : Exception
    {
        public CadExecutionException(
            string code,
            ExecutionFailureCategory category,
            bool retryable,
            string message,
            string suggestedAction,
            string? operationId = null,
            IReadOnlyDictionary<string, string>? data = null,
            Exception? innerException = null) : base(message, innerException)
        {
            Code = code;
            Category = category;
            Retryable = retryable;
            SuggestedAction = suggestedAction;
            OperationId = operationId;
            FailureData = data;
        }

        public string Code { get; }
        public ExecutionFailureCategory Category { get; }
        public bool Retryable { get; }
        public string SuggestedAction { get; }
        public string? OperationId { get; }
        public IReadOnlyDictionary<string, string>? FailureData { get; }
    }

    private sealed record ExecutionFailure(
        string Code,
        ExecutionFailureCategory Category,
        bool Retryable,
        string Message,
        string SuggestedAction,
        string? OperationId,
        IReadOnlyDictionary<string, string> Data);

    private static void ReleaseCom(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
            Marshal.FinalReleaseComObject(value);
    }

    public void Dispose()
    {
        _serialGate.Dispose();
        _sta.Dispose();
    }
}

[SupportedOSPlatform("windows")]
internal sealed class SolidWorksStartupDialogSuppressor : IDisposable
{
    private const string ExactJournalMessage = "没能装入 Microsoft .NET Framework。";
    private const int SwHide = 0;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly ManualResetEventSlim _audioReady = new(false);
    private readonly Task<StartupDialogSuppressionResult> _watchTask;
    private volatile bool _audioSuppressionArmed;

    private SolidWorksStartupDialogSuppressor(bool watchNewProcess)
    {
        if (!watchNewProcess)
        {
            _audioReady.Set();
            _watchTask = Task.FromResult(new StartupDialogSuppressionResult(false, false));
            return;
        }
        var startedUtc = DateTime.UtcNow;
        _watchTask = Task.Run(() => Watch(startedUtc, _cancellation.Token));
        // Do not launch SolidWorks until the system-sounds session has been muted or audio setup has failed safely.
        _audioReady.Wait(TimeSpan.FromSeconds(2));
    }

    public static SolidWorksStartupDialogSuppressor Start()
    {
        var processes = Process.GetProcessesByName("SLDWORKS");
        try
        {
            return new(processes.Length == 0);
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }
    }

    public StartupDialogSuppressionResult WaitForResult(TimeSpan timeout)
    {
        try
        {
            if (_watchTask.Wait(timeout)) return _watchTask.Result;
            _cancellation.Cancel();
            return _watchTask.Wait(TimeSpan.FromSeconds(1))
                ? _watchTask.Result
                : new(false, _audioSuppressionArmed);
        }
        catch (AggregateException ex) when (ex.InnerExceptions.All(x => x is OperationCanceledException))
        {
            return new(false, _audioSuppressionArmed);
        }
    }

    private StartupDialogSuppressionResult Watch(DateTime startedUtc, CancellationToken cancellationToken)
    {
        var comInitialized = CoInitializeEx(IntPtr.Zero, 0) >= 0;
        SolidWorksStartupAudioSilencer? audioSilencer = null;
        try
        {
            try
            {
                audioSilencer = SolidWorksStartupAudioSilencer.Start(startedUtc);
                _audioSuppressionArmed = audioSilencer.IsArmed;
            }
            catch (COMException)
            {
                // Window suppression remains useful if Core Audio is unavailable.
            }
            finally
            {
                _audioReady.Set();
            }

            var deadline = startedUtc + TimeSpan.FromSeconds(12);
            while (DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
            {
                audioSilencer?.MuteMatchingSessions();
                if (AnyJournalContainsExactMessage() && TryDismissForNewSolidWorksProcess(startedUtc))
                {
                    // Keep the notification session muted until the short system-sound waveform has fully elapsed.
                    cancellationToken.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(400));
                    var audioWasArmed = _audioSuppressionArmed;
                    audioSilencer?.Dispose();
                    var audioWasRestored = audioSilencer?.RestoreSucceeded ?? false;
                    audioSilencer = null;
                    return new(true, audioWasArmed && audioWasRestored);
                }
                cancellationToken.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(25));
            }
            return new(false, _audioSuppressionArmed);
        }
        finally
        {
            _audioReady.Set();
            audioSilencer?.Dispose();
            if (comInitialized) CoUninitialize();
        }
    }

    private static bool AnyJournalContainsExactMessage()
    {
        try
        {
            var root = Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
                "SOLIDWORKS");
            if (!Directory.Exists(root)) return false;
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Directory.EnumerateFiles(root, "swxJRNL.swj", SearchOption.AllDirectories).Any(journal =>
                Encoding.GetEncoding(936).GetString(File.ReadAllBytes(journal))
                    .Contains(ExactJournalMessage, StringComparison.Ordinal));
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static bool TryDismissForNewSolidWorksProcess(DateTime startedUtc)
    {
        foreach (var process in Process.GetProcessesByName("SLDWORKS"))
        {
            using (process)
            {
                try
                {
                    if (process.StartTime.ToUniversalTime() < startedUtc - TimeSpan.FromSeconds(2)) continue;
                    if (TryDismissDialog((uint)process.Id)) return true;
                }
                catch (InvalidOperationException) { }
            }
        }
        return false;
    }

    private static bool TryDismissDialog(uint processId)
    {
        var dismissed = false;
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out var ownerProcessId);
            if (ownerProcessId != processId || !IsWindowVisible(window)) return true;
            if (!WindowText(window).Equals("SOLIDWORKS", StringComparison.OrdinalIgnoreCase)) return true;
            if (!WindowClass(window).Equals("#32770", StringComparison.Ordinal)) return true;

            if (!GetWindowRect(window, out var bounds)) return true;
            var width = bounds.Right - bounds.Left;
            var height = bounds.Bottom - bounds.Top;
            if (width is < 400 or > 520 || height is < 120 or > 220) return true;

            var buttons = new List<string>();
            var hasDirectUi = false;
            EnumChildWindows(window, (child, _) =>
            {
                var childClass = WindowClass(child);
                if (childClass.Equals("DirectUIHWND", StringComparison.Ordinal)) hasDirectUi = true;
                if (IsWindowVisible(child) && childClass.Equals("Button", StringComparison.Ordinal))
                    buttons.Add(WindowText(child));
                return true;
            }, IntPtr.Zero);
            if (!hasDirectUi || buttons.Count != 1 ||
                !(buttons[0].Equals("确定", StringComparison.Ordinal) ||
                  buttons[0].Equals("OK", StringComparison.OrdinalIgnoreCase))) return true;

            ShowWindow(window, SwHide);
            dismissed = true;
            return false;
        }, IntPtr.Zero);
        return dismissed;
    }

    private static string WindowText(IntPtr window)
    {
        var text = new StringBuilder(512);
        GetWindowText(window, text, text.Capacity);
        return text.ToString();
    }

    private static string WindowClass(IntPtr window)
    {
        var text = new StringBuilder(128);
        GetClassName(window, text, text.Capacity);
        return text.ToString();
    }

    public void Dispose()
    {
        _cancellation.Cancel();
        try { _watchTask.Wait(TimeSpan.FromSeconds(1)); }
        catch (AggregateException) { }
        _cancellation.Dispose();
        _audioReady.Dispose();
    }

    private delegate bool EnumWindowsCallback(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsCallback callback, IntPtr parameter);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, StringBuilder text, int maximumCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder className, int maximumCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out WindowBounds bounds);

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr reserved, uint coInit);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowBounds
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}

internal readonly record struct StartupDialogSuppressionResult(
    bool DialogHidden,
    bool NotificationAudioSilenced);

[SupportedOSPlatform("windows")]
internal sealed class StaWorker : IDisposable
{
    private readonly BlockingCollection<Action> _queue = [];
    private readonly Thread _thread;

    public StaWorker()
    {
        _thread = new Thread(() =>
        {
            foreach (var action in _queue.GetConsumingEnumerable()) action();
        })
        { IsBackground = true, Name = "SolidWorks-COM-STA" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public Task<T> InvokeAsync<T>(Func<T> function, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Add(() =>
        {
            if (cancellationToken.IsCancellationRequested)
            {
                completion.TrySetCanceled(cancellationToken);
                return;
            }
            try { completion.TrySetResult(function()); }
            catch (Exception ex) { completion.TrySetException(ex); }
        }, cancellationToken);
        return completion.Task;
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        if(_thread.Join(TimeSpan.FromSeconds(5)))_queue.Dispose();
    }
}
