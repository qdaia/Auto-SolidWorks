# Local geometry verification

Use source-derived local checks for intentionally partial/intersecting holes, stepped bores, conical sinks/tips and planar shoulders. These inspect actual trimmed faces in model coordinates before save and after reopening the native file; the same checks work on STEP imports. They supplement the complete-cylinder checks. Do not remove or replace a failed full-cylinder requirement with samples unless the source itself requires a partial wall.

## Contract

`verification.surface_samples` contains checks with:

- `id`, `source_literal`, `source_dimension_ids`: same source requirement and binding rules as other checks.
- `surface_kind`: `Plane`, `Cylinder`, or `Cone`.
- `points_mm`: nonempty array of `{x,y,z}` in global model mm. Derive these from the source geometry, never from measured output.
- `outward_normals`: exactly one nonzero vector per point, pointing **out of the material**. On an internal hole the normal points toward the hole axis. Vectors are normalized internally; opposite directions fail.
- `diameter_mm`: required only for Cylinder, read from actual cylinder parameters.
- `cone_half_angle_degrees`: required only for Cone, in (0,90). A 118-degree drill point has a 59-degree half angle. A 90-degree countersink has a 45-degree half angle. Cone diameter at a particular axial station is constrained by the point coordinates, not a nominal cylinder diameter.
- `expected_area_mm2`: optional independently derived total area of all unique faces touched by matching samples. `area_tolerance_mm2` defaults 0.1 and must be smaller than the expected area. Repeated samples on the same face do not count its area twice. Sample every intended face fragment; missed fragments fail the area expectation. Area is derived from source geometry, not copied from the inspected model, and is not a linear-dimension binding target. The executor integrates every oriented trimming loop of the matched analytic face, including inner loops and seam coedges. It refines 8-point Gauss quadrature at least three levels, retains an estimated numerical uncertainty margin and fails closed on non-convergence, missing boundaries, evaluation/time limits, or a result too close to the tolerance boundary. This is a numerical estimate, not an exact integral. Ordinary IFace2.GetArea is approximate and is not used to decide this area check.
- `tolerance_mm`: default 0.05; `angle_tolerance_degrees`: default 0.1.

`verification.boundary_clearances` contains `id`, `source_literal`, `source_dimension_ids`, `points_mm`, `minimum_distance_mm`, and `tolerance_mm` (default 0.01, strictly less than minimum distance). Every point must be at least the specified distance from **all solid faces**. An unread face or missing body inventory fails closed. This check measures distance only: it does **not** distinguish material from void or prove that two cavities connect. Pair it with independently specified wall geometry and material-side normals.

At most 512 local points are accepted per request. All declared points must pass. Results include each point's nearest-boundary distance, candidate face parameters, optional unique-face total area and an explicit scope statement. COM failures produce `unverifiable`; mismatching measured geometry produces `mismatch`. Unsupported analytic/spline surfaces cannot satisfy one of the three declared surface types.

Source bindings support `points_mm.N.x/y/z`, `diameter_mm`, `cone_half_angle_degrees` and `minimum_distance_mm`. Use length units for coordinates/diameters/distances, Degree for cone angles. The computed read-only binding target `cone_included_angle_degrees` is twice the half angle: bind a source 90-degree countersink directly to this field while declaring `cone_half_angle_degrees:45`. Derived coordinates need corresponding derived source facts when bound. Tolerances and normals cannot masquerade as source dimension bindings. Feature inventory can reference these check IDs just like complete-cylinder checks.

## Example: diameter 10 edge half-hole

An 80 x 50 x 10 plate spans x=-40..40, y=-25..25, z=0..10. A through-cut cylinder centered at x=40, y=0 leaves only the inner half of its wall. Selected source-derived checks:

```json
{
  "surface_samples": [{
    "id": "edge_half_wall",
    "source_literal": "R5 semicylindrical edge notch at x=40, z=0..10",
    "surface_kind": "Cylinder",
    "points_mm": [{"x":35,"y":0,"z":2},{"x":36,"y":3,"z":5},{"x":35,"y":0,"z":8}],
    "outward_normals": [{"x":1,"y":0,"z":0},{"x":0.8,"y":-0.6,"z":0},{"x":1,"y":0,"z":0}],
    "diameter_mm": 10,
    "expected_area_mm2": 157.07963267948966
  }],
  "boundary_clearances": [{
    "id": "outside_half_absent",
    "source_literal": "No external half-wall at x=45",
    "points_mm": [{"x":45,"y":0,"z":5}],
    "minimum_distance_mm": 4
  }]
}
```

This text-only example omits source bindings; for drawings add source dimension IDs and their binding records. The numeric expectations are intentionally written before building the fixture.

## Coverage selection

For partial/intersecting walls, use multiple angular and axial stations on each retained segment, with boundary-clearance probes where another cut should remove a wall. For counterbores, check both cylindrical diameters and the planar shoulder location/normal. For countersinks and drill tips, check cone angle and multiple radii/axial stations including near both ends, plus the adjoining cylindrical wall. Preserve native dimensional checks where suitable.

When source geometry determines patch area, include it: half-cylinder area is πrL; annular shoulder area is π(R²-r²); conical/frustum side area is π(R+r)√((R-r)²+h²). This catches missing or excess area away from sampled points. Area covers unique touched faces only; it does not locate a compensated defect or prove adjacency. Finite sampling can still miss defects between points. It does not establish exact whole-face coverage, topology, through/blind connectivity, complete tip geometry, thread pitch/effective depth, GD&T or whole-drawing equivalence. Physical helical threads and arbitrary blended/spline faces remain outside this checker. Cosmetic thread annotations are not verified by cylindrical/conical samples. Keep those requirements unresolved or independently verified; never report them as passed from sample results.

## API basis

The executor queries `IFace2.GetClosestPointOn` on the trimmed face, then `ISurface.Evaluate` at the returned UV, adjusts normal direction with `IFace2.FaceInSurfaceSense`, and reads cylinder or `ConeParams2` parameters. It does not accept points lying only on the unbounded extension of a removed wall.

- [Trimmed face closest point](https://help.solidworks.com/2023/english/api/sldworksapi/SOLIDWORKS.Interop.sldworks~SOLIDWORKS.Interop.sldworks.IFace2~GetClosestPointOn.html)
- [Surface evaluation and normal](https://help.solidworks.com/2021/english/api/sldworksapi/SolidWorks.Interop.sldworks~SolidWorks.Interop.sldworks.ISurface~Evaluate.html)
- [Cone parameters and half-angle units](https://help.solidworks.com/2024/English/api/sldworksapi/SolidWorks.Interop.sldworks~SolidWorks.Interop.sldworks.ISurface~ConeParams2.html)

- [Approximate face area and tessellation guidance](https://help.solidworks.com/2024/english/api/sldworksapi/solidworks.interop.sldworks~solidworks.interop.sldworks.iface2~getarea.html?format=P&value=)

Area integration uses Stokes boundary forms: plane ½(p×dp)·n; cylinder -rz dθ; cone r² dθ/(2 sin α). Loop orientations come from the actual coedges. [Coedge evaluation and oriented derivatives](https://help.solidworks.com/2023/english/api/sldworksapi/SolidWorks.Interop.sldworks~SolidWorks.Interop.sldworks.ICoEdge~Evaluate2.html). Boundary integration is limited to 2048 coedges, 64 subdivisions per edge, 20000 evaluations and 20 seconds per face.
