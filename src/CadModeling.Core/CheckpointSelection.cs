using System.Text.Json;
using CadModeling.Ir;

namespace CadModeling.Core;

public sealed record CheckpointCandidateDecision(string Path, bool Compatible, int ReusableOperationCount, string Reason);
public sealed record CheckpointSelection(string? ManifestPath, IReadOnlyList<CheckpointCandidateDecision> Candidates);

public static partial class ModelingRecovery
{
    public static CheckpointSelection SelectCompatibleCheckpoint(ModelingPlan plan)
    {
        if (!plan.Recovery.Enabled || plan.Recovery.Directory is not { } root || !Path.IsPathFullyQualified(root))
            throw new ArgumentException("自动选择检查点需要 enabled=true 和当前任务的绝对目录。");
        if (!Directory.Exists(root)) return new(null, []);
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new ArgumentException("检查点目录不能是重解析链接。");
        // Executor snapshots live exactly one directory below the task root. Do not scan unrelated trees.
        var directories = Directory.EnumerateDirectories(root).Take(513).ToArray();
        if(directories.Length > 512) throw new ArgumentException("检查点候选超过512个；请指定当前任务的较小目录或显式清单。");
        var paths = directories.Where(d => (File.GetAttributes(d) & FileAttributes.ReparsePoint) == 0)
            .Select(d => Path.Combine(d,"checkpoint.json")).Prepend(Path.Combine(root,"checkpoint.json"))
            .Where(File.Exists).Order(StringComparer.Ordinal).ToArray();
        var candidates = new List<CheckpointCandidateDecision>();
        foreach(var path in paths)
        {
            try
            {
                if(new FileInfo(path).Length > 16 * 1024 * 1024 || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("清单过大或是重解析链接。");
                var manifest = ReadAndValidate(plan, path);
                if(manifest.FormatVersion != ModelingCheckpointManifest.CurrentFormatVersion || manifest.Environment is null)
                    throw new InvalidDataException("自动选择只接受包含运行环境身份的当前格式快照。");
                if(manifest.Environment.CompilerVersion != ComponentContentIdentity.ForType(typeof(GenericPlanCompiler),"generic-plan-compiler/v1"))
                    throw new InvalidDataException("编译器内容身份已变化，不能自动复用。");
                var decision = DecideResume(plan,manifest);
                if(!decision.CanResume) throw new InvalidDataException(string.Join(";",decision.InvalidationReasons));
                candidates.Add(new(path,true,manifest.CompletedOperationCount,"来源、文件哈希和完整原生前缀一致；执行器及SolidWorks身份仍需原生入口复核。"));
            }
            catch(Exception error) when(error is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException)
            { candidates.Add(new(path,false,0,error.Message)); }
        }
        var selected = candidates.Where(c=>c.Compatible).OrderByDescending(c=>c.ReusableOperationCount).ThenBy(c=>c.Path,StringComparer.Ordinal).FirstOrDefault();
        return new(selected?.Path,candidates);
    }
}
