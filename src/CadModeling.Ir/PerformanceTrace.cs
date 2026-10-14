using System.Diagnostics;
using System.Text.Json;

namespace CadModeling.Ir;

/// <summary>Opt-in diagnostic spans; never participate in model identity or acceptance.</summary>
public static class PerformanceTrace
{
    public const string DirectoryVariable = "AUTO_SOLIDWORKS_TRACE_DIRECTORY";
    private static readonly AsyncLocal<Span?> Current = new();
    private static readonly object WriteGate = new();
    private static readonly string ProcessInstance = Guid.NewGuid().ToString("N");
    private static long _sequence;
    private static long _writeFailures;
    public static long WriteFailures => Interlocked.Read(ref _writeFailures);
    public static string? CurrentRequestId => Current.Value?.RequestId;
    public static int? CurrentAttempt => Current.Value?.Attempt;
    public static string? CurrentFilePath => Environment.GetEnvironmentVariable(DirectoryVariable) is {Length:>0} directory&&Path.IsPathFullyQualified(directory)
        ?Path.Combine(directory,$"timing-{Environment.ProcessId}-{ProcessInstance}.jsonl"):null;

    public static IDisposable Begin(string stage,string? requestId=null,string? operationId=null,int? attempt=null)
    {
        var directory = Environment.GetEnvironmentVariable(DirectoryVariable);
        if (string.IsNullOrWhiteSpace(directory)) return Disabled.Instance;
        // An invalid diagnostics destination must not change CAD execution behavior.
        try
        {
            if (!Path.IsPathFullyQualified(directory)) throw new IOException("跟踪目录必须是绝对路径。");
            var span = new Span(directory, stage, Current.Value,requestId,operationId,attempt);
            Current.Value = span;
            return span;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Failed();
            return Disabled.Instance;
        }
    }

    public static T Measure<T>(string stage, Func<T> action)
    {
        using var span = Begin(stage);
        return action();
    }

    public static void Measure(string stage, Action action)
    {
        using var span = Begin(stage);
        action();
    }

    private static void Failed()
    {
        if (Interlocked.Increment(ref _writeFailures) != 1) return;
        try { Console.Error.WriteLine("AutoSolidWorks 诊断计时写入失败；计时证据不完整。"); }
        catch (IOException) { }
    }

    private sealed class Disabled : IDisposable
    {
        public static readonly Disabled Instance = new();
        public void Dispose() { }
    }

    private sealed class Span(string directory, string stage, Span? parent,string? requestId,string? operationId,int? attempt) : IDisposable
    {
        public string? RequestId { get; }=requestId??parent?.RequestId;
        public string? OperationId { get; }=operationId??parent?.OperationId;
        public int? Attempt { get; }=attempt??parent?.Attempt;
        private readonly long _id = Interlocked.Increment(ref _sequence);
        private readonly long _started = Stopwatch.GetTimestamp();
        private readonly DateTimeOffset _startedUtc = DateTimeOffset.UtcNow;
        private readonly int _thread = Environment.CurrentManagedThreadId;
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            var ended = Stopwatch.GetTimestamp();
            if (ReferenceEquals(Current.Value, this)) Current.Value = parent;
            try
            {
                // Stop before serialization and disk I/O. Parent spans remain inclusive.
                var record = JsonSerializer.Serialize(new
                {
                    schema_version = 1, process_id = Environment.ProcessId, process_instance = ProcessInstance,
                    executable_path = Environment.ProcessPath, span_id = _id, parent_span_id = parent?._id,
                    stage, thread_id = _thread, started_utc = _startedUtc,
                    request_id=RequestId,operation_id=OperationId,attempt=Attempt,
                    started_ticks = _started, ended_ticks = ended, frequency = Stopwatch.Frequency,
                    elapsed_ms = (ended - _started) * 1000d / Stopwatch.Frequency,
                    write_failures = WriteFailures,
                    semantics = "inclusive_wall_time; 上一次完成不是接受"
                });
                lock (WriteGate)
                {
                    Directory.CreateDirectory(directory);
                    File.AppendAllText(Path.Combine(directory, $"timing-{Environment.ProcessId}-{ProcessInstance}.jsonl"), record + "\n");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                Failed();
            }
        }
    }
}
