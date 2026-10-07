"""Local FreeCAD worker. Upstream CurvesWB is unmodified and pinned separately."""
import hashlib
import json
import math
import os
from pathlib import Path
import sys
import traceback

ROOT = Path(__file__).resolve().parent
sys.path.insert(0, str(ROOT / 'CurvesWB'))


def require(condition, code, message):
    if not condition:
        raise ValueError(code + ': ' + message)


def validate(draft):
    require(isinstance(draft, dict), 'GORDON_INPUT', '需要曲线网络对象。')
    tol = draft.get('tolerance_mm', 0.01)
    samples = draft.get('samples_per_curve', 41)
    require(isinstance(tol, (int, float)) and math.isfinite(tol) and 0.0001 <= tol <= 0.1,
            'GORDON_TOLERANCE', '公差必须在 0.0001..0.1 毫米范围内。')
    require(isinstance(samples, int) and 11 <= samples <= 201,
            'GORDON_SAMPLES', '每条曲线的采样数必须在 11..201 范围内。')
    ids = set()
    for name in ('profiles', 'guides'):
        family = draft.get(name, [])
        require(isinstance(family, list) and 2 <= len(family) <= 16,
                'GORDON_FAMILY', '每组曲线必须包含 2..16 条曲线。')
        for curve in family:
            identity = curve.get('id', '')
            require(isinstance(identity, str) and identity.strip() and len(identity) <= 128 and identity not in ids,
                    'GORDON_ID', '曲线 ID 必须非空且唯一。')
            ids.add(identity)
            pts = curve.get('points_mm', [])
            require(isinstance(pts, list) and 2 <= len(pts) <= 128,
                    'GORDON_POINTS', '每条曲线需要 2..128 个插值点。')
            for p in pts:
                require(isinstance(p, list) and len(p) == 3 and all(isinstance(v, (int, float))
                        and math.isfinite(v) and abs(v) <= 1000000 for v in p),
                        'GORDON_COORDINATE', '需要有限的模型空间 XYZ 坐标，单位为毫米。')
            require(math.dist(pts[0], pts[-1]) > tol, 'GORDON_CLOSED', '仅支持开放曲线网络。')
            require(all(math.dist(a, b) > 1e-7 for a, b in zip(pts, pts[1:])),
                    'GORDON_DUPLICATE_POINT', '相邻插值点不得重合。')
    return tol, samples


def curve_deviations(curves, face, samples, Part):
    results = []
    for identity, curve in curves:
        distances = []
        for k in range(samples):
            t = curve.FirstParameter + (curve.LastParameter - curve.FirstParameter) * k / (samples - 1)
            distances.append(Part.Vertex(curve.value(t)).distToShape(face)[0])
        results.append(dict(id=identity, sample_count=samples, maximum_mm=max(distances),
                            rms_mm=math.sqrt(sum(d*d for d in distances)/samples)))
    return results


def preview(surface, destination):
    # A vector preview of the actual surface, with both isoparametric directions.
    lines = []
    projected = []
    u0, u1, v0, v1 = surface.bounds()
    for family in range(2):
        for i in range(13):
            points = []
            for j in range(51):
                u = u0 + (u1-u0) * (j/50 if family == 0 else i/12)
                v = v0 + (v1-v0) * (i/12 if family == 0 else j/50)
                p = surface.value(u, v)
                xy = (0.70710678*(p.x-p.y), 0.40824829*(p.x+p.y)-0.81649658*p.z)
                points.append(xy)
                projected.append(xy)
            lines.append((family, points))
    xmin, xmax = min(p[0] for p in projected), max(p[0] for p in projected)
    ymin, ymax = min(p[1] for p in projected), max(p[1] for p in projected)
    scale = min(700/max(xmax-xmin, 1e-6), 460/max(ymax-ymin, 1e-6))
    svg = ['<svg xmlns="http://www.w3.org/2000/svg" width="800" height="560" viewBox="0 0 800 560">',
           '<rect width="800" height="560" fill="#f4f7fb"/>',
           '<text x="35" y="30" font-family="sans-serif" font-size="18" fill="#16324f">Gordon 曲面 - 实际等参数曲线</text>']
    for family, points in lines:
        poly = ' '.join('%.3f,%.3f' % (50+(x-xmin)*scale, 65+(y-ymin)*scale) for x, y in points)
        svg.append('<polyline points="%s" fill="none" stroke="%s" stroke-width="1.2"/>' %
                   (poly, '#116c95' if family == 0 else '#ad6225'))
    svg.append('</svg>')
    destination.write_text('\n'.join(svg), encoding='utf-8')


def build(draft, output, report):
    tol, samples = validate(draft)
    import FreeCAD as App
    import Part
    from freecad.Curves.BSplineAlgorithms import BSplineAlgorithms
    from freecad.Curves.gordon import InterpolateCurveNetwork
    report['runtime'] = dict(freecad=App.Version(), occ=Part.OCC_VERSION,
                             curveswb=json.loads((ROOT/'curveswb-provenance.json').read_text()))
    families = []
    for name in ('profiles', 'guides'):
        curves = []
        for entry in draft[name]:
            curve = Part.BSplineCurve()
            curve.interpolate(Points=[App.Vector(*p) for p in entry['points_mm']],
                              PeriodicFlag=False, Tolerance=1e-7)
            edge = curve.toShape()
            require(edge.isValid() and not curve.isClosed(), 'GORDON_CURVE_INVALID', entry['id'])
            curves.append((entry['id'], curve))
        families.append(curves)
    profiles, guides = families
    intersections = []
    parameters = {identity: [] for identity, _ in profiles + guides}
    algorithm = BSplineAlgorithms(1e-10)
    # The upstream solver reports some errors by printing; enforce its preconditions here.
    for profile_id, profile in profiles:
        for guide_id, guide in guides:
            distance = profile.toShape().distToShape(guide.toShape())[0]
            require(distance <= tol, 'GORDON_NETWORK_GAP', '%s / %s 间隙 %.8g 毫米' % (profile_id, guide_id, distance))
            pairs = algorithm.intersections(profile, guide, 1e-10)
            require(len(pairs) == 1, 'GORDON_INTERSECTION_COUNT',
                    '%s / %s 必须恰有一个无歧义交点，实际为 %s' % (profile_id, guide_id, len(pairs)))
            a, b = pairs[0]
            gap = profile.value(a).distanceToPoint(guide.value(b))
            require(gap <= tol, 'GORDON_INTERSECTION_GAP', '%s / %s' % (profile_id, guide_id))
            parameters[profile_id].append(a)
            parameters[guide_id].append(b)
            intersections.append(dict(profile=profile_id, guide=guide_id, gap_mm=gap))
    for identity, curve in profiles + guides:
        values = sorted(parameters[identity])
        require(all(b-a > 1e-9*(curve.LastParameter-curve.FirstParameter) for a, b in zip(values, values[1:])),
                'GORDON_NETWORK_COLLAPSED', '交点必须互不重合，曲线：'+identity)
        require(curve.value(values[0]).distanceToPoint(curve.value(curve.FirstParameter)) <= tol and
                curve.value(values[-1]).distanceToPoint(curve.value(curve.LastParameter)) <= tol,
                'GORDON_BOUNDARY_COVERAGE', '外侧交点必须覆盖曲线端点：'+identity)
    report['network_intersections'] = intersections
    scale = 0.5*(algorithm.scale([c for _, c in profiles])+algorithm.scale([c for _, c in guides]))
    network = InterpolateCurveNetwork([c for _, c in profiles], [c for _, c in guides],
                                     tol=tol/max(scale, 1e-7), tol2=1e-10)
    surface = network.surface()
    face = surface.toShape()
    require(face.isValid() and len(face.Faces) == 1 and face.Area > 1e-8,
            'GORDON_SHAPE_INVALID', '需要有效的单面曲面。')
    deviations = curve_deviations(profiles+guides, face, samples, Part)
    report['curve_deviations'] = deviations
    require(max(d['maximum_mm'] for d in deviations) <= tol,
            'GORDON_DEVIATION', '曲面与输入曲线的采样偏差超过指定公差。')
    # Finite regularity samples detect collapsed local parameterization; not global self-intersection proof.
    u0, u1, v0, v1 = surface.bounds()
    for i in range(15):
        for j in range(15):
            normal = surface.normal(u0+(u1-u0)*i/14, v0+(v1-v0)*j/14)
            require(all(math.isfinite(v) for v in (normal.x, normal.y, normal.z)) and normal.Length > 0.5,
                    'GORDON_SINGULAR_SAMPLE', '采样 UV 点处的曲面法线无效。')
    brep_path, step_path = output/'Surface.brep', output/'Surface.step'
    face.exportBrep(str(brep_path))
    face.exportStep(str(step_path))
    reopened = Part.Shape()
    reopened.read(str(step_path))
    require(reopened.isValid() and len(reopened.Faces) == 1 and len(reopened.Solids) == 0,
            'GORDON_STEP_INVALID', 'STEP 读回必须包含一个有效曲面面。')
    require(abs(reopened.Area-face.Area) <= max(1e-6, face.Area*1e-7),
            'GORDON_STEP_AREA', 'STEP 读回面积变化超过公差。')
    readback_deviations = curve_deviations(profiles+guides, reopened, samples, Part)
    require(max(d['maximum_mm'] for d in readback_deviations) <= tol,
            'GORDON_STEP_DEVIATION', '重开的 STEP 曲线采样偏差超过公差。')
    document = App.newDocument('GordonSurface')
    document.Label = 'Gordon曲面'
    try:
        for index, (identity, curve) in enumerate(profiles+guides):
            obj = document.addObject('Part::Feature', 'Curve_%d' % index)
            obj.Label = ('轮廓曲线_' if index < len(profiles) else '导向曲线_') + str(index + 1)
            obj.Shape = curve.toShape()
            obj.addProperty('App::PropertyVectorList', 'InterpolationPoints', '输入')
            obj.InterpolationPoints = [App.Vector(*p) for p in (draft['profiles']+draft['guides'])[index]['points_mm']]
        obj = document.addObject('Part::Feature', 'GordonSurface')
        obj.Label = 'Gordon曲面'
        obj.Shape = face
        obj.addProperty('App::PropertyString', 'RebuildInstruction', '输入')
        obj.RebuildInstruction = '通过 cad_build_gordon_surface 从 input.json 重新生成；FCStd 不支持自动重新计算。'
        document.recompute()
        document.saveAs(str(output/'Surface.FCStd'))
    finally:
        App.closeDocument(document.Name)
    saved = App.openDocument(str(output/'Surface.FCStd'))
    try:
        stored = saved.getObject('GordonSurface').Shape
        require(stored.isValid() and len(stored.Faces) == 1 and abs(stored.Area-face.Area) <= max(1e-6, face.Area*1e-7),
                'GORDON_FCSTD_INVALID', '保存的 FreeCAD 曲面未通过读回验证。')
        require(len(saved.Objects) == len(profiles)+len(guides)+1,
                'GORDON_FCSTD_CURVES', '保存的 FreeCAD 文档丢失了输入曲线。')
    finally:
        App.closeDocument(saved.Name)
    preview(surface, output/'preview.svg')
    box = face.BoundBox
    report.update(success=True, status='local_gordon_surface_verified',
                  area_mm2=face.Area, bounding_box_mm=dict(x=box.XLength, y=box.YLength, z=box.ZLength),
                  nurbs=dict(u_degree=surface.UDegree, v_degree=surface.VDegree,
                             u_poles=surface.NbUPoles, v_poles=surface.NbVPoles),
                  checks=dict(unique_pair_intersections=True, boundary_coverage=True, valid_single_sheet=True,
                              sampled_input_curve_deviation=True, sampled_normals=225, step_reopened=True, fcstd_reopened=True),
                  step_readback=dict(area_mm2=reopened.Area, curve_deviations=readback_deviations),
                  files={name:str(output/name) for name in ('Surface.step','Surface.brep','Surface.FCStd','preview.svg','input.json','report.json')})
    report['sha256'] = {name:hashlib.sha256((output/name).read_bytes()).hexdigest()
                        for name in ('Surface.step','Surface.brep','Surface.FCStd','input.json')}


def main():
    output = Path(sys.argv[1])
    require(output.is_absolute(), 'GORDON_OUTPUT', '输出目录必须使用绝对路径。')
    output.mkdir(parents=True, exist_ok=False)
    report = dict(success=False, status='failed', units='mm', solidworks_native_acceptance='not_run',
                  g1_g2_certification=False, global_self_intersection_certification=False,
                  verification_scope='对输入曲线距离、法线和 STEP 读回进行有限采样；验证一个开放曲面。',
                  fcstd_recompute='保存输入曲线、插值点及生成面；通过 input.json 和建模工具重新构建。')
    try:
        draft = json.load(sys.stdin)
        (output/'input.json').write_text(json.dumps(draft, ensure_ascii=False, indent=2), encoding='utf-8')
        build(draft, output, report)
    except Exception as exc:
        report['error'] = str(exc)
        (output/'failure.log').write_text(traceback.format_exc(), encoding='utf-8')
    (output/'report.json').write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding='utf-8')
    print(json.dumps(dict(success=report['success'], report=str(output/'report.json'))), flush=True)
    return 0 if report['success'] else 2


if __name__ == '__main__':
    sys.exit(main())
