# Complex part modeling

Version `0.6.0+modeling.20261001.4` builds on the earlier modeling upgrade. Only new complex cases were built in this round; previous ten modeling fixtures and old regression suites were not rerun.

## Native loft controls

On `LoftBoss` / `LoftCut`, `feature.loft` additionally accepts:

- `centerline_id`: an earlier native sketch, spatial curve or helix; native selection mark 4. It must differ from profile and guide IDs, and cannot be combined with a closed loft. The generic compiler adds its dependency. The retained centerline must have provable native identity.
- `guide_influence`: NextGuide, NextSharp, NextEdge or Global. Requires guide IDs; creation and saved definition must retain the requested value and guide count.
- `start_tangent_length_mm` / `end_tangent_length_mm`: positive finite lengths, only with the corresponding NormalToProfile condition. Values are converted to meters and assigned through the native definition after creation, then read back.
- `reverse_start_tangent` / `reverse_end_tangent`: require the corresponding normal condition. These flags are retained and checked, but reversed-direction native geometry variants were not certified in this round.

New native evidence covers a three-section spatial centerline boss, a three-section asymmetric boss with two spatial guides and Global influence, and a four-section coaxial boss retaining 12/18 mm endpoint tangent lengths. The offset four-section normal-condition fixture failed; the coaxial success is a different geometry, not proof that the failed variant was fixed. LoftCut is an exposed adapter route, not separately certified cut geometry.

New complex controls on SurfaceLoft are rejected by the validator. Its centerline trial did not expose an accessible retained loft definition. Existing SurfaceLoft profile/guide/end-condition behavior remains separately scoped.

## Trimming and chained features

`SurfaceTrim` keep selections still use mark 2, Body and an explicit model-space surface point. Each point must match exactly one native temporary trimmed piece. The point is checked against the trimmed faces, not just a bounding box; nonfinite measurements and points within selection tolerance of piece boundaries fail. The original target is selected at the proved point before PostTrimSurface, avoiding commit of a temporary display body.

Body queries scoped to a feature first use generated faces, then affected faces. If neither provides bodies, only native `IBody2.GetFeatures()` membership with proven feature identity can establish scope. Missing membership never expands the query to all part bodies. This fixes the case where a trim creates new edges without generated faces and subsequent Thicken could not find its sheet.

The new three-section curved roof was trimmed at x=8 mm and thickened by 1.2 mm. Saved readback checked the standard trim, one kept piece, valid body, and 33 trimmed parameter samples per edge with x no greater than 8+1.2 mm. This is finite boundary verification, not a proof of every interior surface point.

The new centerline hollow duct uses a native boss followed by Shell: 0.8 mm retained wall thickness, exactly two removed end faces, one valid solid and at least 3 mm boundary clearance at both source end centers. It is a hollow duct, not a standard pipe/thread certification.

## Spatial networks and limits

Opt-in 2x2 Boundary corner matching now accepts explicit open SpatialCurve endpoints in model XYZ, including orientation reversal. Closed curves, unsupported sources and gaps remain rejected. This check establishes endpoint matching only. The new doubly curved 3D Boundary skin trial failed native creation. A segment-selection diagnostic also returned RPC_E_SERVERFAULT; that experimental adapter was reverted. Do not deliver this fixture as a successful Boundary surface or replace it silently with a loft/fill.

G2 Fill, arbitrary NURBS reconstruction, general connector control, whole-domain continuity and exact BREP equivalence remain uncertified. Modeling controls require typed drafts; natural-language templates do not infer these fields.

## Evidence

`artifacts/complex-modeling-20261001` contains the pre-change baseline, new regression, native creation failures/successes, five selected saved-model readbacks, source/runtime identity and local package. Reference research includes build123d/CurvesWB and Hugging Face complex CAD research; those backends were not substituted for SolidWorks native geometry.

Official contracts: [InsertProtrusionBlend2](https://help.solidworks.com/2018/english/api/sldworksapi/SOLIDWORKS.Interop.sldworks~SOLIDWORKS.Interop.sldworks.IFeatureManager~InsertProtrusionBlend2.html), [guide influence](https://help.solidworks.com/2024/english/api/swconst/SolidWorks.Interop.swconst~SolidWorks.Interop.swconst.swGuideCurveInfluence_e.html), [PreTrimSurface](https://help.solidworks.com/2021/English/api/sldworksapi/SOLIDWORKS.Interop.sldworks~SOLIDWORKS.Interop.sldworks.IFeatureManager~PreTrimSurface.html).
