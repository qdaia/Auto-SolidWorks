using System.IO.Pipes;
using System.Reflection;
using System.Text;
using System.Text.Json;
using CadModeling.Core;
using CadModeling.Ir;

var output=Path.GetFullPath(args.Single());Directory.CreateDirectory(output);
var previous=Environment.GetEnvironmentVariable("CAD_EXECUTOR_RECEIPTS_DIR");Environment.SetEnvironmentVariable("CAD_EXECUTOR_RECEIPTS_DIR",Path.Combine(output,"receipts"));
try
{
    var kernel=Assembly.Load("CadModeling.Executor.SolidWorks").GetType("SolidWorksComExecutor",true)!;
    var sessionType=kernel.GetNestedType("NativeUnchangedTopologySession",BindingFlags.NonPublic)!;
    var sphere=new GeometryCandidate{CandidateId="sphere",NativePersistentReference="entity:sphere",FeatureId="球面来源",
        OwnerFeaturePersistentReference="owner:sphere",Signature=new(){EntityKind=EntityKind.Face,GeometryKind=GeometryKind.Sphere,
            AnchorMm=new(0,0,0),RadiusMm=20,AreaMm2=4000}};
    var reference=new GeometryRef{RefId="sphere",DocumentId="document:sphere",ModelSha256=new string('a',64),
        DocumentRevision="saved:one",SourceRevisionId="source:one",NativePersistentReference="entity:sphere",EntityKind=EntityKind.Face,
        GeometryKind=GeometryKind.Sphere,FeatureId="球面来源",OperationId="sphere-source",Signature=sphere.Signature,SourceFactIds=["radius:20"],
        Semantic=new(){SemanticKey="sphere.surface",RootToken="root",RootOwnerPersistentReference="owner:sphere",RootConfiguration="默认"}};
    var document=new GeometryDocumentIdentity{DocumentId="document:sphere",ModelSha256=new string('A',64),DocumentRevision="saved:one",SourceRevisionId="source:one"};
    var session=(ISemanticTopologyHistorySession)Activator.CreateInstance(sessionType,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic,
        null,[new[]{sphere},"默认"],null)!;
    var history=session.Capture(reference,document)??throw new Exception("Native history rejects equivalent SHA casing");
    if(!GeometryRefResolver.IsVerifiedResolution(GeometryRefResolver.Resolve(reference,document,[sphere],history)))
        throw new Exception("Equivalent SHA casing lost the complete semantic receipt");
    if(session.Capture(reference,document with{ModelSha256=new string('B',64)}) is not null)
        throw new Exception("Different SHA acquired native history");
    if(session.Capture(reference,document with{SourceRevisionId="source:changed"}) is not null)
        throw new Exception("Different source revision acquired native history");
    if(session.Capture(reference,document with{DocumentRevision="saved:changed"}) is not null)
        throw new Exception("Different saved revision acquired native history");
    if(session.Capture(reference with{Semantic=reference.Semantic with{RootOwnerPersistentReference="wrong-owner"}},document) is not null)
        throw new Exception("Wrong native owner acquired history");
    var pipe="offline-lifetime-"+Guid.NewGuid().ToString("N");var fake=new BlockingPort();
    var type=Assembly.Load("CadModeling.Executor.SolidWorks").GetType("ExecutorPipeServer",true)!;
    var server=Activator.CreateInstance(type,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic,null,[pipe,fake],null)!;
    using var stop=new CancellationTokenSource();
    var running=(Task)type.GetMethod("RunAsync")!.Invoke(server,[stop.Token])!;
    using var client=new NamedPipeClientStream(".",pipe,PipeDirection.InOut,PipeOptions.Asynchronous);await client.ConnectAsync(5000);
    using var writer=new StreamWriter(client,new UTF8Encoding(false),leaveOpen:true){AutoFlush=true};
    using var reader=new StreamReader(client,Encoding.UTF8,leaveOpen:true);
    var id=Guid.NewGuid().ToString("N");await writer.WriteLineAsync(JsonSerializer.Serialize(new ExecutorServiceRequest("health",RequestId:id,DeadlineMilliseconds:60000),ModelingIrJson.Options));
    var response=reader.ReadLineAsync();
    await fake.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));stop.Cancel();await Task.Delay(5500);
    if(running.IsCompleted)throw new Exception("Shutdown abandoned an accepted synchronous native delegate");
    using var activeReader=new StreamReader(new FileStream(Path.Combine(output,"receipts","native-execution.lock"),FileMode.Open,FileAccess.Read,FileShare.ReadWrite));
    using var active=JsonDocument.Parse(activeReader.ReadToEnd());activeReader.Dispose();
    if(active.RootElement.GetProperty("state").GetString()!="active")throw new Exception("Lease released while delegate remained active");
    fake.Release.TrySetResult(new(true,"offline-port","known delegate returned"));await response.WaitAsync(TimeSpan.FromSeconds(5));await running.WaitAsync(TimeSpan.FromSeconds(5));
    using var completed=JsonDocument.Parse(File.ReadAllText(Path.Combine(output,"receipts",id+".json")));
    using var lease=JsonDocument.Parse(File.ReadAllText(Path.Combine(output,"receipts","native-execution.lock")));
    if(completed.RootElement.GetProperty("state").GetString()!="completed"||lease.RootElement.GetProperty("state").GetString()!="completed")throw new Exception("Known returned result lost its durable receipt");
    File.WriteAllText(Path.Combine(output,"summary.json"),JsonSerializer.Serialize(new{status="pass",passed=8,solidworks_started=false,
        checks=new[]{"equivalent SHA case keeps verified semantic receipt","different SHA rejects native history","different source revision rejects native history",
            "different saved revision rejects native history","wrong owner rejects native history","shutdown waits beyond prior five-second limit",
            "lease stays active until synchronous delegate returns","completion receipt and lease persist after parent cancellation"}},ModelingIrJson.Options));
    Console.WriteLine("Offline native lifetime checks passed: 8");
}
finally{Environment.SetEnvironmentVariable("CAD_EXECUTOR_RECEIPTS_DIR",previous);}
sealed class BlockingPort:IModelingExecutor
{
    public TaskCompletionSource<bool> Entered {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource<ExecutorHealth> Release {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<ExecutorHealth> HealthAsync(CancellationToken token=default){Entered.TrySetResult(true);return Release.Task;}
    public Task<ExecutionResult> ExecuteAsync(ModelingPlan plan,bool dryRun,CancellationToken token=default)=>throw new InvalidOperationException("Offline test must never execute a model");
}
