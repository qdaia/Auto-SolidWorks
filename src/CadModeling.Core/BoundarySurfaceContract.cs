namespace CadModeling.Core;

/// <summary>InsertNetBlend2 ordered-curve roles, from the local SOLIDWORKS 2025 API contract.</summary>
public static partial class BoundarySurfaceContract
{
    public static int SelectionMark(int direction,int index,int count)
    {
        if(direction is <0 or >1||count<1||index<0||index>=count)throw new ArgumentOutOfRangeException(nameof(index));
        // First/end/interior are roles, NOT an unbounded ordinal counter.
        return (index==0?8192:index==count-1?16384:24576)|(direction+1);
    }
}
