using CadModeling.Ir;
using System.Diagnostics.CodeAnalysis;

namespace CadModeling.Core;

public sealed record NativeCylinderAxis(Vector3 PointMm, Vector3 Direction, double RadiusMm);
public sealed record NativeAxialCoincidence(Vector3 FirstPointMm, Vector3 SecondPointMm,
    Vector3 FirstNormal, Vector3 SecondNormal);
public sealed record NativeCylindricalJoint
{
    public required string FirstComponentId { get; init; }
    public required string SecondComponentId { get; init; }
    public required string ConcentricMateIdentity { get; init; }
    public required NativeCylinderAxis FirstAxis { get; init; }
    public required NativeCylinderAxis SecondAxis { get; init; }
    public bool NativeDefinitionsVerified { get; init; }
    public bool LockRotation { get; init; }
    public string? AxialMateIdentity { get; init; }
    public NativeAxialCoincidence? AxialCoincidence { get; init; }
}
public sealed record NativeConcentricTreeReadback
{
    public bool CompleteMateInventory { get; init; }
    public bool AllResolvedTopLevelParts { get; init; }
    public required IReadOnlyList<string> ComponentIds { get; init; }
    public required IReadOnlyList<string> FixedComponentIds { get; init; }
    public int ActiveMateCount { get; init; }
    public required IReadOnlyList<NativeCylindricalJoint> Joints { get; init; }
}

/// <summary>
/// Exact instantaneous relative span in a grounded tree of cylindrical joints.
/// Each joint consists of a native concentric mate and, optionally, coincident
/// end planes normal to its axis. There are no loop closure constraints, limits,
/// gear/screw couplings, redundant mates, subassemblies or inferred endpoints.
/// All spatial joint twists are read in the current assembly frame. A joint
/// shared by both paths to the ground cancels from their relative motion.
/// </summary>
public static class ConcentricTreeMobilityContract
{
    public static IReadOnlyList<AssemblyMotion> Basis(NativeConcentricTreeReadback r,
        string componentId, string relativeToComponentId)
    {
        Require(r is not null && r.CompleteMateInventory && r.AllResolvedTopLevelParts,
            "缺少完整原生配合库存或已解析的顶层零件。");
        var ids = r.ComponentIds;
        Require(ids is { Count: >= 2 and <= 32 } && ids.All(x => !string.IsNullOrWhiteSpace(x))
            && ids.Distinct(StringComparer.Ordinal).Count() == ids.Count, "组件库存无效。");
        Require(r.FixedComponentIds is { Count: 1 } && ids.Contains(r.FixedComponentIds[0], StringComparer.Ordinal),
            "开链需要唯一实际固定的基准组件。");
        Require(componentId != relativeToComponentId && ids.Contains(componentId, StringComparer.Ordinal)
            && ids.Contains(relativeToComponentId, StringComparer.Ordinal), "相对运动端点无效。");
        Require(r.Joints is not null && r.Joints.Count == ids.Count - 1, "关节数与完整开链组件库存不符。");
        var adjacency = ids.ToDictionary(id => id, _ => new List<(string Other, NativeCylindricalJoint Joint)>(), StringComparer.Ordinal);
        var pairs = new HashSet<(string First,string Second)>();
        var mateIdentities = new HashSet<string>(StringComparer.Ordinal);
        var jointBases = new Dictionary<NativeCylindricalJoint, IReadOnlyList<AssemblyMotion>>();
        foreach (var j in r.Joints)
        {
            Require(j is not null && j.NativeDefinitionsVerified && j.FirstComponentId != j.SecondComponentId
                && adjacency.ContainsKey(j.FirstComponentId) && adjacency.ContainsKey(j.SecondComponentId), "关节原生定义或组件归属无效。");
            var pair = new[] { j.FirstComponentId, j.SecondComponentId }.Order(StringComparer.Ordinal).ToArray();
            Require(pairs.Add((pair[0],pair[1])), "重复组件对不能隐藏多配合或闭环。");
            Require(!string.IsNullOrWhiteSpace(j.ConcentricMateIdentity) && mateIdentities.Add(j.ConcentricMateIdentity), "同心配合原生身份重复或缺失。");
            Require((j.AxialCoincidence is null) == (j.AxialMateIdentity is null), "轴向配合身份与实际平面库存不符。");
            if (j.AxialMateIdentity is { } axial)
                Require(!string.IsNullOrWhiteSpace(axial) && mateIdentities.Add(axial), "轴向配合原生身份重复或缺失。");
            jointBases.Add(j, JointBasis(j));
            adjacency[j.FirstComponentId].Add((j.SecondComponentId, j));
            adjacency[j.SecondComponentId].Add((j.FirstComponentId, j));
        }
        Require(mateIdentities.Count == r.ActiveMateCount, "存在未消费的原生配合，不能认证完整运动基。");
        var visited = new HashSet<string>(StringComparer.Ordinal);
        void Visit(string id, string? parent)
        {
            Require(visited.Add(id), "闭环机构需要专用奇异性与耦合证明。");
            foreach (var edge in adjacency[id]) if (edge.Other != parent) Visit(edge.Other, id);
        }
        Visit(r.FixedComponentIds[0], null);
        Require(visited.Count == ids.Count, "完整组件库存中存在断开的组件或闭环。");
        var parentEdges = new Dictionary<string, (string Parent, NativeCylindricalJoint Joint)>(StringComparer.Ordinal);
        var queue = new Queue<string>(); queue.Enqueue(componentId);
        visited.Clear(); visited.Add(componentId);
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            foreach (var edge in adjacency[id])
                if (visited.Add(edge.Other)) { parentEdges.Add(edge.Other, (id, edge.Joint)); queue.Enqueue(edge.Other); }
        }
        var candidates = new List<AssemblyMotion>();
        for (var id = relativeToComponentId; id != componentId;)
        {
            var edge = parentEdges[id]; candidates.AddRange(jointBases[edge.Joint]); id = edge.Parent;
        }
        return IndependentSpan(candidates);
    }

    private static IReadOnlyList<AssemblyMotion> JointBasis(NativeCylindricalJoint j)
    {
        Require(j.FirstAxis is not null && j.SecondAxis is not null, "圆柱轴线库存缺失。");
        var p = j.FirstAxis.PointMm; var q = j.SecondAxis.PointMm;
        Require(Point(p) && Point(q) && double.IsFinite(j.FirstAxis.RadiusMm) && j.FirstAxis.RadiusMm > 0
            && double.IsFinite(j.SecondAxis.RadiusMm) && j.SecondAxis.RadiusMm > 0, "圆柱位置或半径无效。");
        var a = Unit(j.FirstAxis.Direction); var b = Unit(j.SecondAxis.Direction);
        Require(Math.Abs(Dot(a, b)) >= 1 - 1e-8, "实际圆柱轴线不平行。");
        var d = Minus(q, p); var projection = Dot(d, a);
        Require(Norm(Minus(d, Times(a, projection))) <= .01, "实际圆柱轴线不共线。");
        if (j.AxialCoincidence is { } stop)
        {
            Require(Point(stop.FirstPointMm) && Point(stop.SecondPointMm), "轴向端面位置无效。");
            var n = Unit(stop.FirstNormal); var m = Unit(stop.SecondNormal);
            Require(Math.Abs(Dot(n, a)) >= 1 - 1e-8 && Math.Abs(Dot(m, a)) >= 1 - 1e-8,
                "端面法向不平行于实际同心轴，不能认作轴向止挡。");
            Require(Math.Abs(Dot(Minus(stop.SecondPointMm, stop.FirstPointMm), n)) <= .01,
                "原生轴向端面不重合。");
        }
        var result = new List<AssemblyMotion>();
        if (j.AxialCoincidence is null) result.Add(new() { Kind = AssemblyMotionKind.Translation, Direction = a });
        if (!j.LockRotation) result.Add(new() { Kind = AssemblyMotionKind.Rotation, Direction = a, PointMm = p });
        return result;
    }

    private static IReadOnlyList<AssemblyMotion> IndependentSpan(IReadOnlyList<AssemblyMotion> motions)
    {
        // A tree makes the joint coordinates regular, but the end-component
        // projection can still be singular (e.g. three collinear planar hinges).
        // Remove only structural duplicates that stay identical under every
        // motion on this path. Other rank losses are refused unless the relative
        // map has full row rank six; counting its present nullspace is insufficient.
        var rawRotations=motions.Where(m=>m.Kind==AssemblyMotionKind.Rotation).ToArray();
        bool Parallel(Vector3 a,Vector3 b)=>Norm(Cross(a,b))<=1e-12;
        var coaxial=rawRotations.Length>0 && rawRotations.All(m=>Parallel(m.Direction,rawRotations[0].Direction)
            &&Norm(Cross(Minus(m.PointMm!,rawRotations[0].PointMm!),rawRotations[0].Direction))<=1e-9)
            &&motions.Where(m=>m.Kind==AssemblyMotionKind.Translation).All(m=>Parallel(m.Direction,rawRotations[0].Direction));
        var candidates=new List<AssemblyMotion>();
        foreach(var mode in motions)
        {
            var duplicate=mode.Kind==AssemblyMotionKind.Rotation
                ?coaxial &&candidates.Any(m=>m.Kind==AssemblyMotionKind.Rotation)
                :rawRotations.All(r=>Parallel(r.Direction,mode.Direction))
                    &&candidates.Any(m=>m.Kind==AssemblyMotionKind.Translation&&Parallel(m.Direction,mode.Direction));
            if(!duplicate)candidates.Add(mode);
        }
        // Select independent original twists; projected combinations need not be
        // represented as synthetic screw axes. Sign does not change a subspace.
        var rotations = candidates.Where(m => m.PointMm is not null).Select(m => m.PointMm!).ToArray();
        var origin = rotations.Length == 0 ? new Vector3(0, 0, 0)
            : new Vector3(rotations.Average(p => p.X), rotations.Average(p => p.Y), rotations.Average(p => p.Z));
        var vectors = new List<double[]>(); var result = new List<AssemblyMotion>();
        foreach (var mode in candidates)
        {
            var a = mode.Direction;
            var velocity = mode.Kind == AssemblyMotionKind.Translation ? a : Times(Cross(a, Minus(origin, mode.PointMm!)), .01);
            double[] v = mode.Kind == AssemblyMotionKind.Translation ? [0, 0, 0, velocity.X, velocity.Y, velocity.Z]
                : [a.X, a.Y, a.Z, velocity.X, velocity.Y, velocity.Z];
            var scale = Math.Sqrt(v.Sum(x => x * x)); v = v.Select(x => x / scale).ToArray();
            for (var pass = 0; pass < 2; pass++)
                foreach (var basis in vectors)
                {
                    var dot = v.Zip(basis, (x, y) => x * y).Sum();
                    for (var i = 0; i < 6; i++) v[i] -= dot * basis[i];
                }
            var norm = Math.Sqrt(v.Sum(x => x * x));
            if (norm <= 1e-10) continue;
            Require(double.IsFinite(norm) && norm >= 1e-7, "相对运动秩病态，不能认证完整运动基。");
            vectors.Add(v.Select(x => x / norm).ToArray()); result.Add(mode);
        }
        Require(result.Count <= 6, "相对运动秩超限。");
        Require(result.Count==candidates.Count ||result.Count==6,
            "端点相对运动可能位于奇异投影；缺少额外结构性秩证明。");
        return result;
    }

    private static bool Point(Vector3 p) => p is not null && NativeFeatureValidation.Finite(p)
        && Math.Max(Math.Abs(p.X), Math.Max(Math.Abs(p.Y), Math.Abs(p.Z))) <= 1e6;
    private static Vector3 Unit(Vector3 a)
    {
        Require(a is not null && NativeFeatureValidation.Finite(a), "非有限运动轴线。");
        var n = Norm(a); Require(double.IsFinite(n) && n > 1e-10, "零轴线或溢出轴线。"); return Times(a, 1 / n);
    }
    private static double Norm(Vector3 a) => NativeFeatureValidation.Norm(a);
    private static double Dot(Vector3 a, Vector3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    private static Vector3 Minus(Vector3 a, Vector3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    private static Vector3 Times(Vector3 a, double k) => new(a.X * k, a.Y * k, a.Z * k);
    private static Vector3 Cross(Vector3 a, Vector3 b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
    private static void Require([DoesNotReturnIf(false)] bool condition, string message)
    {
        if (!condition) throw new ArgumentException("ASSEMBLY_MOBILITY_UNVERIFIABLE：" + message);
    }
}
