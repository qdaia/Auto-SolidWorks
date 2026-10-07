using System.Security.Cryptography;
using System.Text.Json;
using CadModeling.Ir;

namespace CadModeling.Core;

public sealed record TopologyCheckpointProof(string RelativePath, string ModelSha256);
public sealed record DurableTopologyHistory(GeometryRef RootReference, TopologyHistoryCapture Capture,
    IReadOnlyList<TopologyCheckpointProof> Checkpoints);

/// <summary>生产端自建的本地库。MAC 阻止外部 JSON 冒充生产记录；不防同一用户对密钥／进程的控制。</summary>
public sealed class DurableTopologyHistoryStore(string directory)
{
    private string Root => Path.GetFullPath(directory);
    private sealed record SignedHistory(string PayloadBase64, string MacHex);

    public TopologyCheckpointProof SaveCheckpoint(string modelPath, string expectedSha)
    {
        var relative = Path.Combine("checkpoints", Guid.NewGuid().ToString("N") + ".SLDPRT");
        var path = CheckedPath(relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var source = new FileStream(modelPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        { source.CopyTo(target); target.Flush(true); }
        if (!Hash(path).Equals(expectedSha, StringComparison.OrdinalIgnoreCase))
            throw new IOException("历史检查点与实际保存文档 SHA 不一致；不写入成功历史。");
        return new(relative, expectedSha);
    }

    public string Save(DurableTopologyHistory history)
    {
        ValidateProofs(history);
        var last = history.Capture.Snapshots[^1];
        var resolution = GeometryRefResolver.Resolve(history.RootReference, last.Document,
            last.Elements.Select(e => e.Candidate).ToArray(), history.Capture);
        if (resolution.Status != GeometryRefResolutionStatus.Resolved || !GeometryRefResolver.IsVerifiedResolution(resolution))
            throw new InvalidOperationException("生产持久历史未形成可重验的唯一来源后继；不写入成功记录。");
        Directory.CreateDirectory(Root);
        var keyPath = Path.Combine(Root, "producer-key.bin");
        if (!File.Exists(keyPath))
        {
            using var keyFile = new FileStream(keyPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            keyFile.Write(RandomNumberGenerator.GetBytes(32)); keyFile.Flush(true);
        }
        var key = File.ReadAllBytes(keyPath);
        if (key.Length != 32) throw new InvalidOperationException("生产历史认证密钥无效。");
        var payload = JsonSerializer.SerializeToUtf8Bytes(history, ModelingIrJson.Options);
        var signed = new SignedHistory(Convert.ToBase64String(payload), Convert.ToHexString(HMACSHA256.HashData(key, payload)));
        var path = HistoryPath(history.RootReference);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        JsonSerializer.Serialize(target, signed, ModelingIrJson.Options); target.Flush(true);
        return path;
    }

    public DurableTopologyHistory? Load(GeometryRef reference, GeometryDocumentIdentity current)
    {
        var path = HistoryPath(reference); var keyPath = Path.Combine(Root, "producer-key.bin");
        if (!File.Exists(path) || !File.Exists(keyPath)) return null;
        if (new FileInfo(path).Length > 32_000_000) throw new InvalidOperationException("生产历史文件超过限定大小。");
        var signed = JsonSerializer.Deserialize<SignedHistory>(File.ReadAllText(path), ModelingIrJson.Options)
            ?? throw new InvalidOperationException("生产历史记录缺失。");
        var key = File.ReadAllBytes(keyPath); var payload = Convert.FromBase64String(signed.PayloadBase64);
        if (key.Length != 32 || !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(signed.MacHex), HMACSHA256.HashData(key, payload)))
            throw new InvalidOperationException("生产历史认证失败；拒绝外部或被修改的历史。");
        var history = JsonSerializer.Deserialize<DurableTopologyHistory>(payload, ModelingIrJson.Options)
            ?? throw new InvalidOperationException("生产历史载荷缺失。");
        if (GeometryRefResolver.Fingerprint(reference) != GeometryRefResolver.Fingerprint(history.RootReference))
            throw new InvalidOperationException("持久历史根引用身份不符。");
        ValidateProofs(history);
        var last = history.Capture.Snapshots[^1].Document;
        if (current.DocumentId != last.DocumentId || !SamePath(current.DocumentPath, last.DocumentPath)
            || !current.ModelSha256.Equals(last.ModelSha256, StringComparison.OrdinalIgnoreCase)
            || current.SourceRevisionId is not null && current.SourceRevisionId != last.SourceRevisionId
            || current.DocumentRevision is not null && current.DocumentRevision != last.DocumentRevision)
            throw new InvalidOperationException("生产历史终点已陈旧或文档／源修订不符。");
        return history;
    }

    private void ValidateProofs(DurableTopologyHistory h)
    {
        if (h.Capture.Provider != "native-controlled-box-edge-history/v1" || !h.Capture.SavedModelIdentityVerified
            || !h.Capture.CompleteInventory || h.Checkpoints.Count != h.Capture.Snapshots.Count || h.Checkpoints.Count is < 1 or > 65
            || h.RootReference.DocumentPath is null || h.Capture.Digest != SemanticTopologyResolver.Digest(h.Capture))
            throw new InvalidOperationException("持久历史缺完整生产记录、受控提供者或检查点。");
        for (int i = 0; i < h.Checkpoints.Count; i++)
        {
            var p = h.Checkpoints[i]; var doc = h.Capture.Snapshots[i].Document;
            if (!SamePath(doc.DocumentPath, h.RootReference.DocumentPath) || doc.DocumentId != h.RootReference.DocumentId
                || !p.ModelSha256.Equals(doc.ModelSha256, StringComparison.OrdinalIgnoreCase)
                || !Hash(CheckedPath(p.RelativePath)).Equals(p.ModelSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("不可变历史检查点身份或完整字节核查失败。");
        }
    }

    private string HistoryPath(GeometryRef reference) => CheckedPath(Path.Combine("records", GeometryRefResolver.Fingerprint(reference) + ".json"));
    private string CheckedPath(string relative)
    {
        if (Path.IsPathRooted(relative)) throw new InvalidOperationException("历史路径需为库内相对路径。");
        var path = Path.GetFullPath(Path.Combine(Root, relative));
        if (!path.StartsWith(Root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidOperationException("历史路径越出生产库。");
        return path;
    }
    private static bool SamePath(string? a, string? b) => a is not null && b is not null
        && Path.GetFullPath(a).Equals(Path.GetFullPath(b), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    private static string Hash(string path)
    { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
}
