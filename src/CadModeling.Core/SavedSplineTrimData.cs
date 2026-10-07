namespace CadModeling.Core;

/// <summary>读取原生 GetTrimCurves2(false,false) 的有界单环数据，不转换底层曲面。</summary>
public static class SavedSplineTrimDataReader
{
    public static IReadOnlyList<SavedSplineCurve> Read(IReadOnlyList<double> data)
    {
        if (data is null || data.Count is < 8 or > 262144)
            throw new ArgumentException("样条修剪数据缺失或超界。");
        int position = 0;
        (int First, int Second) Pair()
        {
            if (position >= data.Count) throw new ArgumentException("样条修剪元数据不完整。");
            var bits = BitConverter.DoubleToInt64Bits(data[position++]);
            return (unchecked((int)bits), unchecked((int)(bits >> 32)));
        }
        double[] Numbers(int count)
        {
            if (count < 0 || count > data.Count - position) throw new ArgumentException("样条修剪数组不完整。");
            var result = data.Skip(position).Take(count).ToArray(); position += count;
            if (result.Any(x => !double.IsFinite(x))) throw new ArgumentException("样条修剪数组含非有限数据。");
            return result;
        }
        var header = Pair(); var loop = Pair();
        if (header.First != 1 || header.Second is < 3 or > 4096 || loop.First != header.Second || loop.Second != 0)
            throw new ArgumentException("目前仅认证单个外环、无周期接缝的样条面。");
        var metadata = new List<(int Order, int Dimension, int Count)>();
        for (int i = 0; i < header.Second; i++)
        {
            var a = Pair(); var b = Pair();
            if (a.First is < 2 or > 16 || a.Second != 0 || b.First is not (2 or 3)
                || b.Second < a.First || b.Second > 8192)
                throw new ArgumentException("修剪曲线阶数、维度、周期性或控制点数量不受支持。");
            metadata.Add((a.First, b.First, b.Second));
        }
        var knots = metadata.Select(m => Numbers(m.Order + m.Count)).ToArray();
        var curves = metadata.Select((m, i) => new SavedSplineCurve(m.Order, m.Dimension, m.Count,
            false, knots[i], Numbers(checked(m.Dimension * m.Count)))).ToArray();
        var tail = Pair();
        if (tail != (1, 1) || position != data.Count)
            throw new ArgumentException("多份修剪曲面或未消费的原生数据不能认证。");
        return curves;
    }
}
