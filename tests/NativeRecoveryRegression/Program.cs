using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CadModeling.Core;
using CadModeling.Ir;

if(args.Length!=3||Path.GetFileName(args[2])!=args[2])throw new ArgumentException("Supply fixture root, exact executor executable, unused output-directory name.");
var root=Path.GetFullPath(args[0]);var output=Path.Combine(root,args[2]);
if(Directory.Exists(output))throw new IOException("Preserve existing evidence; use a fresh output directory.");
Directory.CreateDirectory(output);
var options=new JsonSerializerOptions(ModelingIrJson.Options){WriteIndented=true};
void Save(string name,object value)=>File.WriteAllText(Path.Combine(output,name+".json"),JsonSerializer.Serialize(value,options));
var checks=new List<object>();
void Check(string name,bool passed){checks.Add(new{name,passed});Save("checks",checks);Console.WriteLine((passed?"PASS ":"FAIL ")+name);if(!passed)Environment.ExitCode=1;}
var node=JsonNode.Parse(File.ReadAllText(Path.Combine(root,"t08-native-fixtures","blind-plan.json")))!["plan"]!.DeepClone();
node["plan_id"]="native-pause-"+Guid.NewGuid().ToString("N");node["name"]="native_pause_resume";
node["source_text"]="Deterministic native recovery fixture:30x30x10 plate with seven diameter2 depth5 holes";
node["output"]!["native_path"]=Path.Combine(output,"resumed.SLDPRT");
node["recovery"]!["directory"]=Path.Combine(output,"checkpoints");
node["recovery"]!["after_operation_ids"]=new JsonArray("block");
node["recovery"]!["source_revision_id"]="native-recovery-v1";
var operations=node["operations"]!.AsArray();var template=operations[2]!.DeepClone();operations.RemoveAt(2);
for(var i=0;i<7;i++)
{
    var hole=template.DeepClone();hole["id"]="hole-"+i;hole["name"]="hole-"+i;
    hole["options"]!["diameter_mm"]=2;hole["options"]!["hole_centers"]![0]!["xmm"]=-12+i*4;
    operations.Add(hole);
}
node["acceptance"]!["expected_features"]=new JsonArray(operations.Select(o=>JsonValue.Create(o!["name"]!.GetValue<string>()) as JsonNode).ToArray());
node["acceptance"]!["geometry"]!["expected_volume_mm3"]=9000-7*Math.PI*5;
var plan=ModelingIrJson.Deserialize(node.ToJsonString());Save("plan",plan);
var executable=Path.GetFullPath(args[1]);
Save("manifest",new{executor=executable,executor_sha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.ChangeExtension(executable,".dll")))),scope="real native recovery, development fixture, not installed desktop or independent drawing acceptance"});
var pipeName="native-recovery-"+Guid.NewGuid().ToString("N");
using var executor=new AutoStartingNamedPipeModelingExecutor(new(){PipeName=pipeName,ExecutablePath=executable,LogDirectory=output,StartupTimeoutMilliseconds=60000});
var health=await executor.HealthAsync();Save("health",health);if(!health.Available)throw new IOException(health.Message);
var client=new NamedPipeModelingExecutor(pipeName,10000);var requestId=Guid.NewGuid().ToString("N");
// Submit on an owned transport and intentionally disconnect. Server request ownership must survive.
await using(var pipe=new NamedPipeClientStream(".",pipeName,PipeDirection.InOut,PipeOptions.Asynchronous))
{
    await pipe.ConnectAsync(10000);using var writer=new StreamWriter(pipe,new UTF8Encoding(false),leaveOpen:true){AutoFlush=true};
    await writer.WriteLineAsync(JsonSerializer.Serialize(new ExecutorServiceRequest("execute",plan,RequestId:requestId),ModelingIrJson.Options));
}
var deadline=DateTime.UtcNow.AddSeconds(100);string? checkpointPath=null;
while(DateTime.UtcNow<deadline)
{
    var checkpointRoot=plan.Recovery.Directory!;
    if(Directory.Exists(checkpointRoot))checkpointPath=Directory.EnumerateFiles(checkpointRoot,"checkpoint.json",SearchOption.AllDirectories).FirstOrDefault();
    if(checkpointPath is not null)break;
    var state=await client.GetExecutionStatusAsync(requestId);
    if(state.Execution is not null){Save("completed-before-pause",state);break;}
    await Task.Delay(100);
}
var pause=await client.PauseExecutionAsync(requestId);Save("pause",pause);
Check("request-level pause is accepted after execute-client disconnect",pause.PauseAccepted);
ExecutorServiceResponse final=new();deadline=DateTime.UtcNow.AddSeconds(100);
while(DateTime.UtcNow<deadline)
{
    final=await client.GetExecutionStatusAsync(requestId);
    if(final.Execution is not null)break;
    await Task.Delay(200);
}
Save("reconnected-status",final);
var paused=final.Execution;
Check("reconnected status contains confirmed paused boundary",paused?.Status=="paused"&&paused.Recovery?.ManifestPath is not null);
if(paused?.Status!="paused"||paused.Recovery?.ManifestPath is not { } manifestPath)return;
var manifest=ModelingRecovery.ReadAndValidate(plan,manifestPath);Save("confirmed-checkpoint",manifest);
Check("pause checkpoint is reopened complete and has remaining suffix",manifest.ModelReopened&&manifest.WriteState==CheckpointWriteState.Complete&&manifest.CompletedOperationCount>=2&&manifest.CompletedOperationCount<plan.Operations.Count);
var checkpointHash=DrawingPlanValidation.FileHash(manifest.NativePath);
var corruptPath=Path.Combine(output,"corrupt-checkpoint.json");
File.WriteAllText(corruptPath,JsonSerializer.Serialize(manifest with{NativeSha256=new string('F',64)},options));
var corruptPlan=plan with{Recovery=plan.Recovery with{ResumeManifestPath=corruptPath}};
var corruptResult=await executor.ExecuteAsync(corruptPlan,false);Save("corrupt-rejected",corruptResult);
Check("corrupted checkpoint rejected before output mutation",!corruptResult.Success&&!File.Exists(plan.Output.NativePath));
var stalePlan=plan with{Recovery=plan.Recovery with{ResumeManifestPath=manifestPath,SourceRevisionId="stale-source"}};
var staleResult=await executor.ExecuteAsync(stalePlan,false);Save("stale-source-rejected",staleResult);
Check("stale source revision rejected before output mutation",!staleResult.Success&&!File.Exists(plan.Output.NativePath));
var resumed=await executor.ExecuteAsync(plan with{Recovery=plan.Recovery with{ResumeManifestPath=manifestPath}},false);Save("resumed",resumed);
Check("valid checkpoint resumes to actual saved native model",resumed.Success&&File.Exists(plan.Output.NativePath));
Check("resume explicitly reuses native completed prefix",resumed.Evidence.Any(e=>e.Stage=="checkpoint_resume"&&e.Passed));
Check("checkpoint bytes preserved after resume",DrawingPlanValidation.FileHash(manifest.NativePath)==checkpointHash);
if(resumed.Success)
{
    var inspection=await executor.InspectAsync(new(plan.Output.NativePath!));Save("reopened",inspection);
    Check("resumed model reopens with expected volume",inspection.Success&&inspection.ModelReopened&&inspection.Geometry is { } geometry&&Math.Abs(geometry.VolumeMm3-(9000-7*Math.PI*5))<.01);
    Check("each required feature exists once after resume",plan.Operations.All(op=>inspection.Features?.Count(f=>f.Name==op.Name)==1));
}
Console.WriteLine("DONE "+output);
