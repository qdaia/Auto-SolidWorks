using System.Collections.Concurrent;
using System.Text.Json;
using CadModeling.Core;
using CadModeling.Ir;

internal sealed partial class ExecutorPipeServer
{
    private string? _activeNativeRequest;
    private readonly ConcurrentDictionary<string,ExecutorServiceResponse> _completedRequests=new(StringComparer.Ordinal);
    private readonly string _receiptRoot=Path.GetFullPath(Environment.GetEnvironmentVariable("CAD_EXECUTOR_RECEIPTS_DIR")??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"AutoSolidWorks","executor-receipts"));
    private string ReceiptPath(string id)=>Path.Combine(_receiptRoot,id+".json");
    private void WriteReceipt(string id,string action,string state,ExecutorServiceResponse? response=null)
    {
        Directory.CreateDirectory(_receiptRoot);
        var path=ReceiptPath(id);var temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        File.WriteAllText(temporary,JsonSerializer.Serialize(new{id,action,state,process_id=Environment.ProcessId,updated_utc=DateTimeOffset.UtcNow,response},ModelingIrJson.Options));
        File.Move(temporary,path,true);
    }
    internal async Task<ExecutorServiceResponse> DispatchAsync(ExecutorServiceRequest? request,CancellationToken serverToken)
    {
        if(request is null)return new(Error:"空执行请求。");
        var action=request.Action.ToLowerInvariant();
        if(action is "pause" or "execution_status")
        {
            if(!Guid.TryParseExact(request.RequestId,"N",out _))return new(Error:"控制请求需要发出的32字符的request_id。");
            if(_completedRequests.TryGetValue(request.RequestId!,out var completed))return completed;
            if(_requestTokens.ContainsKey(request.RequestId!))return await DispatchCoreAsync(request,serverToken);
            var receiptPath=ReceiptPath(request.RequestId!);
            if(File.Exists(receiptPath))
            {
                using var receipt=JsonDocument.Parse(await File.ReadAllTextAsync(receiptPath,serverToken));
                var response=receipt.RootElement.GetProperty("response");
                if(response.ValueKind==JsonValueKind.Object)return response.Deserialize<ExecutorServiceResponse>(ModelingIrJson.Options)??new(Error:"无效的完成收据。");
                return new(Error:"EXECUTOR_OUTCOME_UNKNOWN: 上一工序留下了未完成的原生请求。在重试前，请检查其文件和模型状态。",RequestId:request.RequestId,ErrorCode:"EXECUTOR_OUTCOME_UNKNOWN",OutcomeUnknown:true);
            }
            return await DispatchCoreAsync(request,serverToken);
        }
        var requestId=request.RequestId??Guid.NewGuid().ToString("N");
        if(!Guid.TryParseExact(requestId,"N",out _))return new(Error:"无效的 request_id。");
        if(_completedRequests.ContainsKey(requestId)||File.Exists(ReceiptPath(requestId)))return new(Error:"复制完成/未完成，使用 request_id 查询其收据，而非再次执行。",RequestId:requestId,ErrorCode:"EXECUTOR_DUPLICATE_REQUEST");
        var budget=request.DeadlineMilliseconds??300_000;
        if(budget is <1 or >3_600_000)return new(Error:"截止时间必须是 1..3600000 毫秒。",ErrorCode:"EXECUTOR_DEADLINE_INVALID");
        var active=Interlocked.CompareExchange(ref _activeNativeRequest,requestId,null);
        if(active is not null)
        {
            var message=$"EXECUTOR_BUSY: 原生请求{active}仍然活跃；没有新的 COM 操作被排队。";
            return new(Health:action=="health"?new(false,"solidworks-com-service",message):null,Error:message,RequestId:active,ErrorCode:"EXECUTOR_BUSY",Pending:true);
        }
        using var requestDeadline=CancellationTokenSource.CreateLinkedTokenSource(serverToken);
        var expires=DateTimeOffset.UtcNow.AddMilliseconds(budget);
        requestDeadline.CancelAfter(budget);
        var registered=action!="execute";
        if(registered)_requestTokens.TryAdd(requestId,requestDeadline);
        request=request with{RequestId=requestId};
        ExecutorServiceResponse? result=null;
        var submitted=false;
        FileStream? nativeLease=null;
        try
        {
            var acquired=AcquireNativeLease(requestId);
            nativeLease=acquired.Lease;
            if(acquired.Failure is not null)return acquired.Failure;
            WriteReceipt(requestId,action,"active");
            submitted=true;
            result=await DispatchCoreAsync(request,requestDeadline.Token);
            result=result with{RequestId=requestId,DeadlineExceeded=DateTimeOffset.UtcNow>=expires};
            _completedRequests[requestId]=result;
            WriteReceipt(requestId,action,"completed",result);
            // Keep durable receipts; only bound in-memory response retention.
            if(_completedRequests.Count>256)foreach(var id in _completedRequests.Keys.Take(_completedRequests.Count-256))_completedRequests.TryRemove(id,out _);
            return result;
        }
        catch(OperationCanceledException)
        {
            result=new(Error:$"EXECUTOR_DEADLINE: 请求在执行边界上被{requestId}撤销。",RequestId:requestId,ErrorCode:"EXECUTOR_DEADLINE",DeadlineExceeded:true);
            WriteReceipt(requestId,action,"completed",result);return result;
        }
        catch(Exception ex)
        {
            return result is not null?result with{Error="EXECUTOR_RECEIPT_WRITE：执行回执写入失败："+ex.Message,ErrorCode="EXECUTOR_RECEIPT_WRITE"}:
                new(Error:"EXECUTOR_REQUEST_FAILED：执行请求失败："+ex.Message,RequestId:requestId,ErrorCode:"EXECUTOR_REQUEST_FAILED",OutcomeUnknown:submitted);
        }
        finally
        {
            try
            {
                if(nativeLease is not null)
                {
                    // Release only after the actual delegate returned. A process crash leaves
                    // an active durable lease; the next process must not blindly replay COM work.
                    try{WriteNativeLease(nativeLease,requestId,submitted&&result is null?"active":"completed");}
                    finally{nativeLease.Dispose();}
                }
            }
            finally
            {
                if(registered)_requestTokens.TryRemove(requestId,out _);
                Interlocked.CompareExchange(ref _activeNativeRequest,null,requestId);
            }
        }
    }
}
