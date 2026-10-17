using System.Reflection;
using System.Text.Json;
using CadModeling.Core;
using CadModeling.Ir;

var output=Path.GetFullPath(args[0]);Directory.CreateDirectory(output);
var checks=new List<string>();
void Check(bool ok,string name){if(!ok)throw new Exception(name);checks.Add(name);Console.WriteLine("PASS "+name);}
var previous=Environment.GetEnvironmentVariable("CAD_EXECUTOR_RECEIPTS_DIR");
Environment.SetEnvironmentVariable("CAD_EXECUTOR_RECEIPTS_DIR",Path.Combine(output,"receipts"));
try
{
    var pipe="progressive-offline-"+Guid.NewGuid().ToString("N");var fake=new BlockingExecutor();
    var type=Assembly.Load("CadModeling.Executor.SolidWorks").GetType("ExecutorPipeServer",true)!;
    var server=Activator.CreateInstance(type,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic,null,[pipe,fake],null)!;
    using var stop=new CancellationTokenSource();
    var running=(Task)type.GetMethod("RunAsync")!.Invoke(server,[stop.Token])!;
    var client=new NamedPipeModelingExecutor(pipe);
    var plan=new RuleBasedTextCompiler().Compile("80 x 50 x 10 mm plate",Path.Combine(output,"never-created.sldprt")).Plan!;
    var id=Guid.NewGuid().ToString("N");
    var accepted=await client.StartExecutionAsync(plan,id).WaitAsync(TimeSpan.FromSeconds(10));
    await fake.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
    Check(accepted.Pending && accepted.RequestId==id && accepted.Execution is null,"async acknowledgement returns before execution completion");
    Check(File.Exists(Path.Combine(output,"receipts",id+".json")),"active receipt exists before acknowledgement");
    Check(!File.Exists(plan.Output.NativePath),"offline fake did not create a native model");
    var status=await client.GetExecutionStatusAsync(id);
    Check(status.Pending,"same request can be polled while processing");
    var duplicate=await client.StartExecutionAsync(plan,id);
    Check(duplicate.ErrorCode=="EXECUTOR_DUPLICATE_REQUEST" && fake.Calls==1,"same request never replays active mutation");
    var concurrent=await client.StartExecutionAsync(plan,Guid.NewGuid().ToString("N"));
    Check(concurrent.ErrorCode=="EXECUTOR_BUSY" && fake.Calls==1,"second native mutation is rejected while first is active");
    var pause=await client.PauseExecutionAsync(id);
    Check(pause.PauseAccepted,"pause reaches active asynchronous request");
    stop.Cancel();await Task.Delay(100);
    Check(!running.IsCompleted,"server shutdown retains accepted synchronous delegate");
    fake.Release.TrySetResult(new(true,"completed","offline controlled result",[]));
    await running.WaitAsync(TimeSpan.FromSeconds(10));
    var receipt=JsonDocument.Parse(File.ReadAllText(Path.Combine(output,"receipts",id+".json")));
    Check(receipt.RootElement.GetProperty("state").GetString()=="completed","terminal receipt survives disconnected client and server stop");
    // A fresh server must return the existing receipt and refuse replay.
    using var restartedStop=new CancellationTokenSource();
    var restarted=Activator.CreateInstance(type,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic,null,[pipe,new BlockingExecutor()],null)!;
    var restartedTask=(Task)type.GetMethod("RunAsync")!.Invoke(restarted,[restartedStop.Token])!;
    var readback=await client.GetExecutionStatusAsync(id);
    Check(readback.Execution?.Success==true,"new server reads completed durable receipt");
    var replay=await client.StartExecutionAsync(plan,id);
    Check(replay.ErrorCode=="EXECUTOR_DUPLICATE_REQUEST","completed request cannot replay after service restart");
    restartedStop.Cancel();await restartedTask.WaitAsync(TimeSpan.FromSeconds(5));
    var workflow=new ModelingWorkflow(new(),new(),fake);
    var rejected=await workflow.StartExecutionAsync(plan with{Operations=[]},Guid.NewGuid().ToString("N"));
    Check(rejected.Execution?.Status=="rejected" && fake.Calls==1,"background workflow preserves IR validation before dispatch");
    var unsupported=await workflow.StartExecutionAsync(plan,Guid.NewGuid().ToString("N"));
    Check(unsupported.ErrorCode=="BACKGROUND_UNSUPPORTED","non-background executor is not silently used");
}
finally{Environment.SetEnvironmentVariable("CAD_EXECUTOR_RECEIPTS_DIR",previous);}
if(args.Length>1)StageChecks.Run(Path.GetFullPath(args[1]),output,Check);
await ProjectionChecks.Run(output,Check);
File.WriteAllText(Path.Combine(output,"summary.json"),JsonSerializer.Serialize(new{passed=checks.Count,checks,native_execution=false},new JsonSerializerOptions{WriteIndented=true}));

sealed class BlockingExecutor:IModelingExecutor
{
    public int Calls;
    public TaskCompletionSource<bool> Entered {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource<ExecutionResult> Release {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<ExecutorHealth> HealthAsync(CancellationToken cancellationToken=default)=>Task.FromResult(new ExecutorHealth(true,"fake","offline"));
    public Task<ExecutionResult> ExecuteAsync(ModelingPlan plan,bool dryRun,CancellationToken cancellationToken=default)
    {Interlocked.Increment(ref Calls);Entered.TrySetResult(true);return Release.Task;}
}
