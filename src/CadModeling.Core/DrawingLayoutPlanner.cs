namespace CadModeling.Core;

public sealed record DrawingLayoutOptions
{
    public double SheetWidthMm { get; init; } = 420;
    public double SheetHeightMm { get; init; } = 297;
    public bool FirstAngle { get; init; } = true;
    public IReadOnlyList<string> Views { get; init; } = [];
    public double MarginMm { get; init; } = 20;
    public double ViewGapMm { get; init; } = 20;
    public double ScheduleRowHeightMm { get; init; } = 9;
}
public sealed record PlannedDrawingView(string Name, double X, double Y);
public sealed record DrawingLayoutPlan(double Width, double Height, bool FirstAngle,
    double Margin, double CellWidth, double CellHeight, IReadOnlyList<PlannedDrawingView> Views,
    int ScheduleRows, int ScheduleColumns, double ScheduleColumnWidth, double ScheduleRowHeight)
{
    public int ScheduleCapacity => checked(ScheduleRows * ScheduleColumns);
    public IReadOnlyList<string> SheetNames(int dimensionCount)
    {
        if(dimensionCount<0)throw new ArgumentOutOfRangeException(nameof(dimensionCount));
        var pages=Math.Max(1,(dimensionCount+ (long)ScheduleCapacity-1)/ScheduleCapacity);
        return [GeneratedChineseText.ViewsSheet,..Enumerable.Range(1,checked((int)pages)).Select(GeneratedChineseText.ParameterSheet)];
    }
    public (double X,double Y) SchedulePosition(int index)
    {
        if(index<0||index>=ScheduleCapacity)throw new ArgumentOutOfRangeException(nameof(index));
        return (Margin+(index/ScheduleRows)*ScheduleColumnWidth,Height-.052-(index%ScheduleRows)*ScheduleRowHeight);
    }
}
public static class DrawingLayoutPlanner
{
    public static DrawingLayoutPlan Create(DrawingLayoutOptions? options=null)
    {
        var o=options??new();
        if(!double.IsFinite(o.SheetWidthMm)||!double.IsFinite(o.SheetHeightMm)||o.SheetWidthMm<210||o.SheetWidthMm>2000||o.SheetHeightMm<210||o.SheetHeightMm>2000||
           !double.IsFinite(o.MarginMm)||o.MarginMm<10||o.MarginMm>40||!double.IsFinite(o.ViewGapMm)||o.ViewGapMm<10||o.ViewGapMm>40||
           !double.IsFinite(o.ScheduleRowHeightMm)||o.ScheduleRowHeightMm<7||o.ScheduleRowHeightMm>20)
            throw new ArgumentException("布局绘图需要有限的纸张尺寸为 210..2000 mm，边距为 10..40 mm，视图间隙为 10..40 mm，以及排列表格的高度为 7..20 mm。");
        var names=o.Views.Count==0?new[]{"Front","Top",o.FirstAngle?"Left":"Right","Isometric"}:o.Views.ToArray();
        var allowed=new[]{"Front","Top","Left","Right","Bottom","Back","Isometric"};
        if(names.Length==0||names.Length>7||names.Distinct(StringComparer.Ordinal).Count()!=names.Length||names.Any(n=>!allowed.Contains(n))||!names.Contains("Front"))
            throw new ArgumentException("视图必须是标准的唯一视角，包括前视图（最多七种）。");
        var compact=names.All(n=>new[]{"Front","Top",o.FirstAngle?"Left":"Right","Isometric"}.Contains(n));
        int cols=compact?2:3,rows=compact?2:3;
        var w=o.SheetWidthMm/1000;var h=o.SheetHeightMm/1000;var m=o.MarginMm/1000;var gap=o.ViewGapMm/1000;
        var cellW=(w-2*m-(cols-1)*gap)/cols;var cellH=(h-.075-(rows-1)*gap)/rows;
        if(cellW<.04||cellH<.025)throw new ArgumentException("无法将图纸视图适配到请求的视图网格。");
        var positions=new List<PlannedDrawingView>();
        foreach(var n in names)
        {
            (int col,int row)=compact?n switch {"Front"=>(0,o.FirstAngle?0:1),"Top"=>(0,o.FirstAngle?1:0),"Isometric"=>(1,o.FirstAngle?1:0),_=>(1,o.FirstAngle?0:1)}:
                n switch {"Front"=>(1,1),"Top"=>(1,o.FirstAngle?2:0),"Bottom"=>(1,o.FirstAngle?0:2),"Left"=>(o.FirstAngle?2:0,1),"Right"=>(o.FirstAngle?0:2,1),"Back"=>(0,2),_=>(2,2)};
            positions.Add(new(n,m+cellW/2+col*(cellW+gap),h-.045-cellH/2-row*(cellH+gap)));
        }
        var scheduleRows=(int)Math.Floor((h-.052-.060)/(o.ScheduleRowHeightMm/1000))+1;
        var scheduleCols=Math.Max(1,(int)Math.Floor((w-2*m)/.180));
        return new(w,h,o.FirstAngle,m,cellW,cellH,positions,scheduleRows,scheduleCols,(w-2*m)/scheduleCols,o.ScheduleRowHeightMm/1000);
    }
}
