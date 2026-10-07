using CadModeling.Ir;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

internal sealed partial class SolidWorksComExecutor
{
    private static void DimensionSimplePrimitives(IModelDoc2 model, ISketch sketch,
        ProfileSketchOperation operation, IReadOnlyList<object[]> primitives)
    {
        var expected=new List<(IDimension Dimension,double Value)>();
        void Add(ISketchSegment segment,string name,double value,bool horizontal=false,bool vertical=false)
        {
            model.ClearSelection2(true);
            if(!segment.Select4(false,null))throw new InvalidOperationException("无法选择自动尺寸实体。");
            IDisplayDimension? display=(IDisplayDimension?)(horizontal?model.AddHorizontalDimension2(0,-0.02,0):
                vertical?model.AddVerticalDimension2(0.02,0,0):model.AddDiameterDimension2(0.02,0.02,0));
            var dimension=display?.GetDimension2(0)??throw new InvalidOperationException("无法创建关键驱动尺寸："+name);
            if(dimension.DrivenState!=(int)swDimensionDrivenState_e.swDimensionDriving)
                dimension.DrivenState=(int)swDimensionDrivenState_e.swDimensionDriving;
            dimension.Name=name;
            if(dimension.SetSystemValue3(Mm(value),(int)swSetValueInConfiguration_e.swSetValue_InThisConfiguration,null)!=0)
                throw new InvalidOperationException("关键驱动尺寸赋值失败："+name);
            expected.Add((dimension,Mm(value)));
        }
        for(int i=0;i<operation.Primitives.Count;i++)
        {
            switch(operation.Primitives[i])
            {
                case CircleProfile circle:
                    var arc=primitives[i].OfType<ISketchArc>().Single();
                    // Source center coordinates are positional design intent; leave only the diameter editable.
                    if(sketch.RelationManager.AddRelation(new[]{new System.Runtime.InteropServices.DispatchWrapper(arc.GetCenterPoint2())},(int)swConstraintType_e.swConstraintType_FIXED) is null)
                        throw new InvalidOperationException("无法锚定圆心源位置。");
                    Add((ISketchSegment)arc,i==0?"直径":"孔径_"+i,circle.DiameterMm);
                    break;
                case CenteredRectangleProfile rectangle:
                    var lines=primitives[i].OfType<ISketchLine>().Where(l=>!((ISketchSegment)l).ConstructionGeometry).ToArray();
                    // CreateCenterRectangle can infer equal side lengths for a square. Independent width/height
                    // dimensions must replace that relation; keep equal construction diagonals intact.
                    foreach(var relation in (sketch.RelationManager.GetRelations((int)swSketchRelationFilterType_e.swAll) as object[]??[]).Cast<ISketchRelation>())
                    {
                        var entities=relation.GetEntities() as object[]??[];
                        if(relation.GetRelationType()==(int)swConstraintType_e.swConstraintType_SAMELENGTH
                            && entities.Length>=2 && entities.All(e=>e is ISketchSegment s&&!s.ConstructionGeometry))
                            if(!sketch.RelationManager.DeleteRelation((SketchRelation)relation))throw new InvalidOperationException("无法替换自动等长关系。");
                    }
                    ISketchLine Find(bool horizontal)=>lines.First(l=>
                    {
                        var a=(ISketchPoint)l.GetStartPoint2();var b=(ISketchPoint)l.GetEndPoint2();
                        return horizontal?Math.Abs(b.Y-a.Y)<1e-9 && Math.Abs(Math.Abs(b.X-a.X)-Mm(rectangle.WidthMm))<1e-8:
                            Math.Abs(b.X-a.X)<1e-9 && Math.Abs(Math.Abs(b.Y-a.Y)-Mm(rectangle.HeightMm))<1e-8;
                    });
                    Add((ISketchSegment)Find(true),"宽度_"+i,rectangle.WidthMm,horizontal:true);
                    Add((ISketchSegment)Find(false),"高度_"+i,rectangle.HeightMm,vertical:true);
                    break;
                default:throw new InvalidOperationException("自动尺寸不支持该原语。");
            }
        }
        void ValidateDrivingDimensions()
        {
            foreach(var item in expected)
                if(item.Dimension.DrivenState!=(int)swDimensionDrivenState_e.swDimensionDriving
                    || Math.Abs(item.Dimension.GetSystemValue2("")-item.Value)>1e-9)
                    throw new InvalidOperationException("自动定义后关键尺寸未保留为源要求的驱动值。");
        }
        ValidateDrivingDimensions();
        // Native rectangle relations may already anchor the profile. Extra datum relations would overconstrain it.
        if(sketch.GetConstrainedStatus()==(int)swConstrainedStatus_e.swFullyConstrained)return;
        // Keep datum creation independent of zoom-dependent auto-snapping, as with the source primitives.
        var previousAddToDb=model.SketchManager.AddToDB;
        ISketchPoint datum;
        try{model.SketchManager.AddToDB=true;datum=model.SketchManager.CreatePoint(0,0,0)??throw new InvalidOperationException("无法创建尺寸基准点。");}
        finally{model.SketchManager.AddToDB=previousAddToDb;}
        if(sketch.RelationManager.AddRelation(new[]{new System.Runtime.InteropServices.DispatchWrapper(datum)},(int)swConstraintType_e.swConstraintType_FIXED) is null)
            throw new InvalidOperationException("无法固定源坐标基准点。");
        model.ClearSelection2(true);
        // Equal/parallel/tangent inference can overconstrain a square that already has two driving lengths.
        var relations=(int)(swSketchFullyDefineRelationType_e.swSketchFullyDefineRelationType_Horizontal
            |swSketchFullyDefineRelationType_e.swSketchFullyDefineRelationType_Vertical
            |swSketchFullyDefineRelationType_e.swSketchFullyDefineRelationType_Coincident);
        model.SketchManager.FullyDefineSketch(true,false,relations,true,1,datum,1,datum,0,0);
        ValidateDrivingDimensions();
    }
}
