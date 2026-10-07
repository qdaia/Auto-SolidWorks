using System.Text.Json;
using CadModeling.Core;
using CadModeling.Ir;

internal sealed partial class ExecutorPipeServer
{
    private (FileStream? Lease,ExecutorServiceResponse? Failure) AcquireNativeLease(string requestId)
    {
        Directory.CreateDirectory(_receiptRoot);
        var path=Path.Combine(_receiptRoot,"native-execution.lock");
        var existed=File.Exists(path);
        FileStream lease;
        try{lease=new FileStream(path,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.Read);}
        catch(IOException){return(null,new(Health:new(false,"solidworks-com-service","EXECUTOR_BUSY: 另一个执行者拥有原生执行的租约。"),Error:"EXECUTOR_BUSY: 另一个执行者拥有原生执行的租约。",ErrorCode:"EXECUTOR_BUSY",Pending:true));}
        try
        {
            if(existed&&lease.Length==0)
            {
                lease.Dispose();
                const string message="EXECUTOR_OUTCOME_UNKNOWN: 空的原生租约可能是一个中断的写操作；在重试之前，先解决之前的原生/模型状态。";
                return(null,new(Health:new(false,"solidworks-com-service",message),Error:message,ErrorCode:"EXECUTOR_OUTCOME_UNKNOWN",OutcomeUnknown:true));
            }
            if(lease.Length>0)
            {
                using var previous=JsonDocument.Parse(lease);
                if(previous.RootElement.GetProperty("state").GetString()=="active")
                {
                    var id=previous.RootElement.GetProperty("request_id").GetString();
                    lease.Dispose();
                    var message=$"EXECUTOR_BUSY: 未完成的原生租赁为{id}。前执行者未收到完成证明而停止；在重试前解决原生/模型状态。";
                    return(null,new(Health:new(false,"solidworks-com-service",message),Error:message,RequestId:id,ErrorCode:"EXECUTOR_OUTCOME_UNKNOWN",OutcomeUnknown:true));
                }
            }
            WriteNativeLease(lease,requestId,"active");
            return(lease,null);
        }
        catch{lease.Dispose();throw;}
    }
    private static void WriteNativeLease(FileStream lease,string requestId,string state)
    {
        lease.Position=0;lease.SetLength(0);
        JsonSerializer.Serialize(lease,new{request_id=requestId,state,process_id=Environment.ProcessId,updated_utc=DateTimeOffset.UtcNow},ModelingIrJson.Options);
        lease.Flush(true);
    }
}
