using CadModeling.Ir;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

internal sealed partial class SolidWorksComExecutor
{
    private static void ApplySketchEditing(IModelDoc2 model, ISketch sketch, ProfileSketchOperation operation,
        IReadOnlyList<object[]> primitives, IMathUtility math, MathTransform transform)
    {
        object Entity(SketchEntityReference r)
        {
            if(r.PrimitiveIndex<0 || r.PrimitiveIndex>=primitives.Count || r.SegmentIndex<0 || r.SegmentIndex>=primitives[r.PrimitiveIndex].Length)
                throw new ArgumentException("Sketch entity reference is outside the created primitive segments.");
            var segment=primitives[r.PrimitiveIndex][r.SegmentIndex];
            if(segment is ISketchPoint) return segment;
            return r.Part switch {
                SketchEntityPart.Segment=>segment,
                SketchEntityPart.StartPoint when segment is ISketchLine line=>line.GetStartPoint2(),
                SketchEntityPart.EndPoint when segment is ISketchLine line=>line.GetEndPoint2(),
                SketchEntityPart.StartPoint when segment is ISketchArc arc=>arc.GetStartPoint2(),
                SketchEntityPart.EndPoint when segment is ISketchArc arc=>arc.GetEndPoint2(),
                SketchEntityPart.CenterPoint when segment is ISketchArc arc=>arc.GetCenterPoint2(),
                _=>throw new ArgumentException("Requested point is unavailable for this sketch segment.")
            };
        }
        void Select(IReadOnlyList<SketchEntityReference> refs)
        {
            model.ClearSelection2(true); var append=false;
            foreach(var r in refs)
            {
                var selected=Entity(r) switch { ISketchSegment s=>s.Select4(append,null),ISketchPoint p=>p.Select4(append,null),_=>false };
                if(!selected) throw new InvalidOperationException("Cannot select sketch entity."); append=true;
            }
        }
        foreach(var relation in operation.Constraints)
        {
            var kind=relation.Kind switch {
                SketchConstraintKind.Coincident=>swConstraintType_e.swConstraintType_COINCIDENT,
                SketchConstraintKind.Horizontal=>swConstraintType_e.swConstraintType_HORIZONTAL,
                SketchConstraintKind.Vertical=>swConstraintType_e.swConstraintType_VERTICAL,
                SketchConstraintKind.Tangent=>swConstraintType_e.swConstraintType_TANGENT,
                SketchConstraintKind.Concentric=>swConstraintType_e.swConstraintType_CONCENTRIC,
                SketchConstraintKind.Symmetric=>swConstraintType_e.swConstraintType_SYMMETRIC,
                SketchConstraintKind.Equal=>swConstraintType_e.swConstraintType_SAMELENGTH,
                SketchConstraintKind.Parallel=>swConstraintType_e.swConstraintType_PARALLEL,
                SketchConstraintKind.Perpendicular=>swConstraintType_e.swConstraintType_PERPENDICULAR,
                SketchConstraintKind.Midpoint=>swConstraintType_e.swConstraintType_ATMIDDLE,
                _=>swConstraintType_e.swConstraintType_FIXED };
            if(sketch.RelationManager.AddRelation(relation.Entities.Select(r=>new System.Runtime.InteropServices.DispatchWrapper(Entity(r))).ToArray(),(int)kind) is null)
                throw new InvalidOperationException($"Could not add {relation.Kind} sketch relation.");
        }
        foreach(var d in operation.Dimensions)
        {
            Select(d.Entities);
            var p=PointInSketch(math,transform,operation.Plane,d.LabelPosition.Xmm,d.LabelPosition.Ymm);
            var display=(IDisplayDimension?)(d.Kind switch {
                SketchDimensionKind.Horizontal=>model.AddHorizontalDimension2(p.X,p.Y,p.Z),
                SketchDimensionKind.Vertical=>model.AddVerticalDimension2(p.X,p.Y,p.Z),
                SketchDimensionKind.Radius=>model.AddRadialDimension2(p.X,p.Y,p.Z),
                SketchDimensionKind.Diameter=>model.AddDiameterDimension2(p.X,p.Y,p.Z),
                _=>model.AddDimension2(p.X,p.Y,p.Z) });
            var dimension=display?.GetDimension2(0) ?? throw new InvalidOperationException($"Cannot create sketch dimension '{d.Name}'.");
            dimension.Name=d.Name;
            if(dimension.SetSystemValue3(d.Kind==SketchDimensionKind.Angle?Radians(d.Value):Mm(d.Value),
                (int)swSetValueInConfiguration_e.swSetValue_InThisConfiguration,null)!=0)
                throw new InvalidOperationException($"Cannot set driving dimension '{d.Name}'.");
        }
        foreach(var edit in operation.Edits)
        {
            Select(edit.Entities);
            if(edit.Kind==SketchEditKind.Offset)
            {
                if(!model.SketchManager.SketchOffset(Mm(edit.OffsetMm),edit.BothDirections,edit.Chain,edit.CapEnds,false,true))
                    throw new InvalidOperationException("Sketch offset failed.");
            }
            else
            {
                var p=PointInSketch(math,transform,operation.Plane,edit.PickPoint.Xmm,edit.PickPoint.Ymm);
                model.ClearSelection2(true);
                var data=model.ISelectionManager.CreateSelectData();data.X=p.X;data.Y=p.Y;data.Z=p.Z;
                if(edit.Entities.Count!=1 || Entity(edit.Entities[0]) is not ISketchSegment trimSegment || !trimSegment.Select4(false,data))
                    throw new ArgumentException("TrimClosest needs one sketch segment and a point on the portion to remove.");
                model.ClearSelection2(true);
                var world=(double[])((IMathPoint)((IMathPoint)math.CreatePoint(new[]{p.X,p.Y,p.Z})).MultiplyTransform(transform)).ArrayData;
                if(!model.Extension.SelectByID2("","SKETCHSEGMENT",world[0],world[1],world[2],false,0,null,0))
                    throw new InvalidOperationException("No sketch segment lies at the trim pick point.");
                if(!Equals(model.ISelectionManager.GetSelectedObject6(1,-1),trimSegment))
                    throw new InvalidOperationException("Trim pick point does not identify the requested sketch segment.");
                // Power-trim's selected pick point identifies the interval bounded by the nearest intersections.
                if(!model.SketchManager.SketchTrim((int)swSketchTrimChoice_e.swSketchTrimEntities,0,0,0))
                    throw new InvalidOperationException("Sketch trim failed.");
            }
        }
        model.ClearSelection2(true);
    }
}
