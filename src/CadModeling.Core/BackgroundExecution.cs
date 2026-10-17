using CadModeling.Ir;
namespace CadModeling.Core;

public interface IBackgroundModelingExecutor
{
    Task<ExecutorServiceResponse> StartExecutionAsync(ModelingPlan plan,string requestId,CancellationToken cancellationToken=default);
}
