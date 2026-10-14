using CadModeling.Core;
using CadModeling.Ir;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
internal sealed partial class SolidWorksComExecutor
{
    private static SketchQualityObservation ReadSketchQuality(IFeature feature)
    {
        try
        {
            if(feature.GetSpecificFeature2() is not ISketch sketch)return new(){Error="原生草图不可读"};
            var fixedEntities=new List<FixedSketchEntity>();
            foreach(var relation in (sketch.RelationManager.GetRelations((int)swSketchRelationFilterType_e.swAll) as object[]??[]).OfType<ISketchRelation>())
                if(relation.GetRelationType()==(int)swConstraintType_e.swConstraintType_FIXED)
                    foreach(var entity in relation.GetEntities() as object[]??[])
                        if(entity is ISketchPoint p)fixedEntities.Add(new(Math.Abs(p.X)<1e-12&&Math.Abs(p.Y)<1e-12?"reference_origin":"fixed_design_point",new(p.X*1000,p.Y*1000,p.Z*1000)));
                        else fixedEntities.Add(new("fixed_design_entity",null));
            return new(){Complete=true,ConstraintStatus=sketch.GetConstrainedStatus(),FixedEntities=fixedEntities};
        }
        catch(Exception error) { return new(){Error=error.Message}; }
    }
}
