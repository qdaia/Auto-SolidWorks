using CadModeling.Ir;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

internal sealed partial class SolidWorksComExecutor
{
    [ThreadStatic] private static ISketchPoint? _activeSketchOrigin;
    [ThreadStatic] private static ISketchArc? _activeOriginArc;
    private static ISketchPoint NativeSketchOrigin(IModelDoc2 model,ISketch sketch)
    {
        if(_activeSketchOrigin is not null)return _activeSketchOrigin;
        // An arbitrary offset sketch has a local datum, explicitly classified as reference.
        // Only this zero-coordinate datum is fixed; design circle centers are dimensioned to it.
        var previous=model.SketchManager.AddToDB;
        ISketchPoint? datum;
        try { model.SketchManager.AddToDB=true;datum=model.SketchManager.CreatePoint(0,0,0); }
        finally { model.SketchManager.AddToDB=previous; }
        if(datum is null)throw new InvalidOperationException("草图原点副本创建失败。");
        var entities=new[]{new System.Runtime.InteropServices.DispatchWrapper(datum)};
        if(sketch.RelationManager.AddRelation(entities,(int)swConstraintType_e.swConstraintType_FIXED) is null)
            throw new InvalidOperationException("草图局部基准点无法建立。");
        _activeSketchOrigin=datum;
        return datum;
    }

    private static void DimensionCircleCenter(IModelDoc2 model,ISketch sketch,ISketchArc arc,int index,ProfileSketchOperation operation)
    {
        var diagnosticStage="read_center";
        try {
        var center=(ISketchPoint)arc.GetCenterPoint2();
        if(Math.Abs(center.X)<1e-12&&Math.Abs(center.Y)<1e-12)
        {
            // Origin-centred geometry declares a stable reference, not a variable hole location.
            // Avoid merging coincident COM point objects: SW2025 can invalidate their RCWs.
            if(_activeOriginArc is null)
            {
                if(sketch.RelationManager.AddRelation(new[]{new System.Runtime.InteropServices.DispatchWrapper(center)},
                    (int)swConstraintType_e.swConstraintType_FIXED) is null)throw new InvalidOperationException("原点参考无法锚定。");
                _activeOriginArc=arc;_activeSketchOrigin=center;
            }
            else if(sketch.RelationManager.AddRelation(new[]{new System.Runtime.InteropServices.DispatchWrapper(arc),
                new System.Runtime.InteropServices.DispatchWrapper(_activeOriginArc)},(int)swConstraintType_e.swConstraintType_CONCENTRIC) is null)
                throw new InvalidOperationException("同心原点参考未建立。");
            return;
        }
        diagnosticStage="create_origin";
        var origin=NativeSketchOrigin(model,sketch);
        var x=center.X;var y=center.Y;
        void Relation(swConstraintType_e type)
        {
            diagnosticStage="relation_"+type;
            if(sketch.RelationManager.AddRelation(new[]{new System.Runtime.InteropServices.DispatchWrapper(center),
                new System.Runtime.InteropServices.DispatchWrapper(origin)},(int)type) is null)
                throw new InvalidOperationException("圆心基准关系未建立："+type);
        }
        void Position(bool horizontal,double value)
        {
            diagnosticStage=horizontal?"select_x_dimension":"select_y_dimension";
            model.ClearSelection2(true);
            if(!center.Select4(false,null)||!origin.Select4(true,null))throw new InvalidOperationException("无法选择圆心定位点。");
            var display=(IDisplayDimension?)(horizontal?model.AddHorizontalDimension2(x,y-.01,0):model.AddVerticalDimension2(x+.01,y,0));
            diagnosticStage="assign_position_dimension";
            var dimension=display?.GetDimension2(0)??throw new InvalidOperationException("圆心定位尺寸创建失败。");
            dimension.Name=index==0&&operation.CenterPositionDimensionName is {} alias&&
                operation.CenterPositionDimensionAxis==(horizontal?"X":"Y")?alias:(horizontal?"定位X_":"定位Y_")+index;
            if(dimension.DrivenState!=(int)swDimensionDrivenState_e.swDimensionDriving||
                dimension.SetSystemValue3(Math.Abs(value),(int)swSetValueInConfiguration_e.swSetValue_InThisConfiguration,null)!=0)
                throw new InvalidOperationException("圆心定位尺寸没有保持驱动状态。");
        }
        if(Math.Abs(x)<1e-12&&Math.Abs(y)<1e-12)Relation(swConstraintType_e.swConstraintType_COINCIDENT);
        else
        {
            if(Math.Abs(x)<1e-12)Relation(swConstraintType_e.swConstraintType_VERTPOINTS);else Position(true,x);
            if(Math.Abs(y)<1e-12)Relation(swConstraintType_e.swConstraintType_HORIZPOINTS);else Position(false,y);
        }
        diagnosticStage="readback_center";
        center=(ISketchPoint)arc.GetCenterPoint2();
        if(Math.Abs(center.X-x)>1e-9||Math.Abs(center.Y-y)>1e-9)
            throw new InvalidOperationException("定位尺寸求解移动了源圆心。");
        model.ClearSelection2(true);
        } catch(System.Runtime.InteropServices.COMException error) {
            throw new InvalidOperationException("SKETCH_POSITION_"+diagnosticStage+": "+error.Message,error);
        }
    }
}
