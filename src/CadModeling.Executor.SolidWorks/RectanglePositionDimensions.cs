using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

internal sealed partial class SolidWorksComExecutor
{
    // Width/height do not consume a translated rectangle's two position degrees of freedom.
    // Dimension its actual construction-diagonal intersection; do not freeze design edges.
    private static void DimensionRectangleCenter(IModelDoc2 model,ISketch sketch,object[] entities,int index)
    {
        var diagonals=entities.OfType<ISketchLine>().Where(l=>((ISketchSegment)l).ConstructionGeometry).ToArray();
        if(diagonals.Length!=2)throw new InvalidOperationException("RECTANGLE_CENTER_UNVERIFIABLE: two native construction diagonals required.");
        var ends=diagonals.SelectMany(l=>new[]{(ISketchPoint)l.GetStartPoint2(),(ISketchPoint)l.GetEndPoint2()}).ToArray();
        var x=ends.Average(p=>p.X);var y=ends.Average(p=>p.Y);
        if(Math.Abs(x)<1e-12 && Math.Abs(y)<1e-12)return; // existing origin-centred datum path
        foreach(var line in diagonals)
        {
            var a=(ISketchPoint)line.GetStartPoint2();var b=(ISketchPoint)line.GetEndPoint2();
            if(Math.Abs((a.X+b.X)/2-x)>1e-9 || Math.Abs((a.Y+b.Y)/2-y)>1e-9)
                throw new InvalidOperationException("RECTANGLE_CENTER_UNVERIFIABLE: construction diagonals disagree.");
        }
        var origin=NativeSketchOrigin(model,sketch);
        var prior=model.SketchManager.AddToDB;ISketchPoint center;
        try{model.SketchManager.AddToDB=true;center=model.SketchManager.CreatePoint(x,y,0)??throw new InvalidOperationException("Rectangle center point creation failed.");}
        finally{model.SketchManager.AddToDB=prior;}
        void Relation(object first,object second,swConstraintType_e kind)
        {
            if(sketch.RelationManager.AddRelation(new[]{new System.Runtime.InteropServices.DispatchWrapper(first),new System.Runtime.InteropServices.DispatchWrapper(second)},(int)kind) is null)
                throw new InvalidOperationException("Rectangle center relation failed: "+kind);
        }
        foreach(var diagonal in diagonals)Relation(center,diagonal,swConstraintType_e.swConstraintType_COINCIDENT);
        void Position(bool horizontal,double value)
        {
            model.ClearSelection2(true);
            if(!center.Select4(false,null)||!origin.Select4(true,null))throw new InvalidOperationException("Rectangle center selection failed.");
            var display=(IDisplayDimension?)(horizontal?model.AddHorizontalDimension2(x,y-.01,0):model.AddVerticalDimension2(x+.01,y,0));
            var dimension=display?.GetDimension2(0)??throw new InvalidOperationException("Rectangle center driving dimension creation failed.");
            dimension.Name=(horizontal?"矩形定位X_":"矩形定位Y_")+index;
            if(dimension.DrivenState!=(int)swDimensionDrivenState_e.swDimensionDriving ||
                dimension.SetSystemValue3(Math.Abs(value),(int)swSetValueInConfiguration_e.swSetValue_InThisConfiguration,null)!=0 ||
                Math.Abs(dimension.GetSystemValue2("")-Math.Abs(value))>1e-9)
                throw new InvalidOperationException("Rectangle position did not retain the source driving value.");
        }
        if(Math.Abs(x)<1e-12)Relation(center,origin,swConstraintType_e.swConstraintType_VERTPOINTS);else Position(true,x);
        if(Math.Abs(y)<1e-12)Relation(center,origin,swConstraintType_e.swConstraintType_HORIZPOINTS);else Position(false,y);
        if(Math.Abs(center.X-x)>1e-9 || Math.Abs(center.Y-y)>1e-9)
            throw new InvalidOperationException("Rectangle position solve moved the source center.");
        model.ClearSelection2(true);
    }
}
