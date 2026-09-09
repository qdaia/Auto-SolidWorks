"""Real SolidWorks fixtures for finite local geometry verification, not a drawing benchmark."""
import argparse, copy, datetime, hashlib, json, math
from pathlib import Path
from focused_three import Client, ROOT, base, feature, sketch, v, drawing_case

def surface(id,kind,points,normals,**kw):
    return dict(id=id,source_literal='Analytic fixture requirement: '+id,surface_kind=kind,points_mm=points,outward_normals=normals,**kw)
def hole(id,x=0,**kw):
    return feature(id,'Hole',diameter_mm=10,hole_centers=[dict(xmm=x,ymm=0)],frame=dict(origin_mm=v(0,0,10),x_direction=v(1),normal=v(0,0,-1)),**kw)
def half():
    d=base();d['operations'].append(hole('edge_hole',40))
    d['verification']=dict(surface_samples=[surface('half_wall','Cylinder',[v(35,0,2),v(36,3,5),v(35,0,8)],[v(1),v(.8,-.6),v(1)],diameter_mm=10,expected_area_mm2=50*math.pi)],
        boundary_clearances=[dict(id='open_half',source_literal='External half wall absent',points_mm=[v(45,0,5)],minimum_distance_mm=4)])
    return d
def intersect():
    d=base();d['operations'] += [hole('first',-3),hole('second',3)]
    d['verification']=dict(surface_samples=[surface('remaining_walls','Cylinder',[v(-8,0,5),v(8,0,5),v(-3,5,5),v(3,5,5)],[v(1),v(-1),v(0,-1),v(0,-1)],diameter_mm=10,expected_area_mm2=100*(2*math.pi-2*math.acos(.6)))],
        boundary_clearances=[dict(id='intersection_gap',source_literal='Overlapping holes remove the first wall at x=2',points_mm=[v(2,0,5)],minimum_distance_mm=2)])
    return d
def counterbore():
    d=base();d['operations'].append(hole('stepped',hole_kind='Counterbore',counterbore_diameter_mm=16,counterbore_depth_mm=3))
    d['verification']=dict(surface_samples=[surface('upper_wall','Cylinder',[v(8,0,8),v(0,8,9)],[v(-1),v(0,-1)],diameter_mm=16),
        surface('lower_wall','Cylinder',[v(5,0,4)],[v(-1)],diameter_mm=10),surface('shoulder','Plane',[v(6,0,7),v(0,6,7)],[v(0,0,1),v(0,0,1)],expected_area_mm2=39*math.pi)])
    return d
def countersink():
    d=base();d['operations'].append(hole('sunk',hole_kind='Countersink',countersink_diameter_mm=16,countersink_angle_degrees=90))
    d['verification']=dict(surface_samples=[surface('sink_cone','Cone',[v(6.5,0,8.5),v(0,7,9)],[v(-1,0,1),v(0,-1,1)],cone_half_angle_degrees=45,expected_area_mm2=math.pi*13*math.sqrt(18))])
    return d
def drilltip():
    d=base();d['operations'].append(hole('blind',through_all=False,depth_mm=5))
    height=5/math.tan(math.radians(59))
    profile=sketch('tip_profile',[dict(type='Polygon',points=[dict(xmm=0,ymm=5),dict(xmm=5,ymm=5),dict(xmm=0,ymm=5-height)])])
    profile['frame']=dict(origin_mm=v(),x_direction=v(1),normal=v(0,-1))
    d['operations'] += [profile,feature('tip_axis','ReferenceAxis',axis_start_mm=v(),axis_end_mm=v(0,0,10)),feature('tip','RevolveCut',sketch_id='tip_profile',axis_id='tip_axis')]
    d['verification']=dict(surface_samples=[surface('drill_tip','Cone',[v(2.5,0,5-height/2),v(0,2.5,5-height/2)],
        [v(-math.cos(math.radians(59)),0,math.sin(math.radians(59))),v(0,-math.cos(math.radians(59)),math.sin(math.radians(59)))],cone_half_angle_degrees=59,expected_area_mm2=math.pi*5*math.sqrt(25+height*height)),
        surface('blind_wall','Cylinder',[v(5,0,7)],[v(-1)],diameter_mm=10)],
        boundary_clearances=[dict(id='tip_interior',source_literal='Flat end at z=5 removed near axis',points_mm=[v(0,0,4.5)],minimum_distance_mm=1)])
    return d

def cross_holes():
    d=base();d['operations'] += [hole('vertical'),feature('cross','Hole',diameter_mm=4,hole_centers=[dict(xmm=0,ymm=0)],frame=dict(origin_mm=v(-40,0,5),x_direction=v(0,1),normal=v(1)))]
    # Source-derived analytic areas, integrated independently of model measurements.
    def integrate(fn):
        n=2000;h=math.pi/2/n
        return h/3*(fn(0)+fn(math.pi/2)+sum((4 if i%2 else 2)*fn(i*h) for i in range(1,n)))
    vertical_area=100*math.pi-32*integrate(lambda t:math.cos(t)**2/math.sqrt(1-.16*math.sin(t)**2))
    cross_area=320*math.pi-80*integrate(lambda t:math.sqrt(1-.16*math.sin(t)**2))
    d['verification']=dict(surface_samples=[surface('vertical_remains','Cylinder',[v(0,5,5),v(0,-5,5),v(5,0,1),v(-5,0,9)],[v(0,-1),v(0,1),v(-1),v(1)],diameter_mm=10,expected_area_mm2=vertical_area),
        surface('cross_remains','Cylinder',[v(x,y,z) for x in (-15,15) for y,z in [(2,5),(-2,5),(0,7),(0,3)]],[v(0,-y,-(z-5)) for x in (-15,15) for y,z in [(2,5),(-2,5),(0,7),(0,3)]],diameter_mm=4,expected_area_mm2=cross_area)],
        boundary_clearances=[dict(id='cross_intersection',source_literal='Cross drill removes main wall at x=5',points_mm=[v(5,0,5)],minimum_distance_mm=1.9)])
    return d

def fillet():
    d=base();d['operations'].append(feature('rounded','Fillet',radius_mm=2,selections=[dict(kind='Edge',geometry='Line',direction=v(0,0,1),all_matches=True)]))
    a=math.sqrt(2);points=[v(sx*(38+a),sy*(23+a),5) for sx,sy in [(1,1),(-1,1),(-1,-1),(1,-1)]]
    normals=[v(sx,sy) for sx,sy in [(1,1),(-1,1),(-1,-1),(1,-1)]]
    d['verification']=dict(surface_samples=[surface('four_rounds','Cylinder',points,normals,diameter_mm=4,expected_area_mm2=40*math.pi)])
    return d

def chamfer():
    d=base();d['operations'].append(feature('beveled','Chamfer',distance_mm=1,chamfer_mode='EqualDistance',selections=[dict(kind='Edge',geometry='Line',direction=v(0,0,1),all_matches=True)]))
    points=[v(sx*39.5,sy*24.5,5) for sx,sy in [(1,1),(-1,1),(-1,-1),(1,-1)]]
    normals=[v(sx,sy) for sx,sy in [(1,1),(-1,1),(-1,-1),(1,-1)]]
    d['verification']=dict(surface_samples=[surface('four_chamfers','Plane',points,normals,expected_area_mm2=40*math.sqrt(2))])
    return d

def run(package,output,extended_only=False,reuse_models=()):
    output.mkdir(parents=True);client=Client(package,output);results={}
    def save(name,data):(output/(name+'.json')).write_text(json.dumps(data,ensure_ascii=False,indent=2),encoding='utf-8')
    def check(name,condition,data=None):
        results[name]=bool(condition);save('results',results)
        if not condition:raise AssertionError((name,data))
        print('PASS '+name,flush=True)
    def compile(d,name):
        path=output/(name+'_'+output.name+'.SLDPRT')
        r=client.call('cad_create_model_plan',draft=d,nativeOutputPath=str(path),exportPaths=[str(path.with_suffix('.STEP'))]);save(name+'-plan',r);return r
    def inspect(name,path,spec):
        r=client.call('cad_inspect_model',inputPath=str(path),verification=spec);save(name,r);return r
    try:
        save('runtime',dict(package=str(package),executor_sha256=hashlib.sha256((package/'runtime/executor/CadModeling.Executor.SolidWorks.dll').read_bytes()).hexdigest(),mcp_sha256=hashlib.sha256((package/'runtime/mcp/AutoSolidWorks.ModelingMcp.dll').read_bytes()).hexdigest()))
        save('tools',client.rpc('tools/list',{}))
        save('capabilities',client.call('cad_get_capabilities'))
        d=drawing_case();d['verification']['surface_samples']=[surface('top','Plane',[v(0,0,10)],[v(0,0,1)],source_dimension_ids=['depth'])]
        d['verification']['bindings'].append(dict(dimension_id='depth',check_id='top',parameter_path='points_mm.0.z'))
        d['drawing_context']['features'][0]['verification_check_ids'].append('top')
        r=compile(d,'bound_local');check('local_source_binding_compiles',r['success'],r)
        broken=copy.deepcopy(d);broken['verification']['surface_samples'][0]['points_mm'][0]['z']=9
        r=compile(broken,'bound_wrong');check('wrong_local_source_binding_rejected',not r['success'],r)
        broken=copy.deepcopy(d);broken['verification']['surface_samples'][0]['outward_normals']=[]
        r=compile(broken,'no_normals');check('missing_normals_rejected',not r['success'],r)
        models={}
        fixtures=[('cross_holes',cross_holes),('fillet',fillet),('chamfer',chamfer)]
        if not extended_only:fixtures=[('half',half),('intersect',intersect),('counterbore',counterbore),('countersink',countersink),('drilltip',drilltip)]+fixtures
        for name,make in fixtures:
            d=make();d['recovery']=dict(enabled=False)
            r=compile(d,name);check(name+'_compile',r['success'],r)
            reused=None
            for folder in reuse_models:
                record=folder/(name+'.json')
                if record.is_file():
                    prior=json.loads(record.read_text(encoding='utf-8'))
                    candidate=prior.get('native_path') or prior.get('input_path')
                    if prior.get('success') and candidate and Path(candidate).suffix.upper()=='.SLDPRT' and Path(candidate).is_file():
                        reused=Path(candidate);break
            if reused:
                original_hashes={p:hashlib.sha256(p.read_bytes()).hexdigest() for p in [reused,reused.with_suffix('.STEP')]}
                r=inspect(name,reused,d['verification']);check(name+'_native_readback',r['success'] and r['verification']['passed'],r)
                path=reused
            else:
                r=client.call('cad_build_model',irJson=r['ir_json']);save(name,r);check(name+'_saved_native',r['success'] and r['verification']['passed'],r)
                path=Path(r['native_path'])
            models[name]=path
            save('fixture_models',{key:str(value) for key,value in models.items()})
            hashes={p:hashlib.sha256(p.read_bytes()).hexdigest() for p in [path,path.with_suffix('.STEP')]}
            if reused:hashes=original_hashes
            r=inspect(name+'_step',path.with_suffix('.STEP'),d['verification']);check(name+'_step',r['success'] and r['verification']['passed'],r)
            check(name+'_files_unchanged',all(hashlib.sha256(p.read_bytes()).hexdigest()==h for p,h in hashes.items()))
        for name,make in fixtures:
            spec=copy.deepcopy(make()['verification'])
            area=next(c for c in spec['surface_samples'] if 'expected_area_mm2' in c)
            area['expected_area_mm2']*=.9
            r=inspect(name+'_wrong_area',models[name],spec)
            check(name+'_wrong_area_rejected',not r['success'] and not r['verification']['passed'],r)
        if extended_only:return results
        negatives=[('half_missing_side','half',dict(surface_samples=[surface('missing','Cylinder',[v(45,0,5)],[v(-1)],diameter_mm=10)])),
            ('intersection_trimmed_extension','intersect',dict(surface_samples=[surface('removed','Cylinder',[v(2,0,5)],[v(-1)],diameter_mm=10)]))]
        for name,model,spec in negatives:
            r=inspect(name,models[model],spec);check(name+'_rejected',not r['success'] and not r['verification']['passed'],r)
        for name,model,make,mutate in [
            ('normal','half',half,lambda s:s['surface_samples'][0]['outward_normals'][0].update(x=-1)),
            ('diameter','half',half,lambda s:s['surface_samples'][0].update(diameter_mm=9)),
            ('shoulder_depth','counterbore',counterbore,lambda s:s['surface_samples'][2]['points_mm'][0].update(z=6)),
            ('cone_angle','countersink',countersink,lambda s:s['surface_samples'][0].update(cone_half_angle_degrees=44)),
            ('drill_angle','drilltip',drilltip,lambda s:s['surface_samples'][0].update(cone_half_angle_degrees=60))]:
            spec=copy.deepcopy(make()['verification']);mutate(spec);r=inspect('wrong_'+name,models[model],spec);check('wrong_'+name+'_rejected',not r['success'] and not r['verification']['passed'],r)
        strict=dict(cylinder_groups=[dict(id='full',source_literal='Must be complete cylinder',diameter_mm=10,axis_starts_mm=[v(40)],length_mm=10)])
        r=inspect('half_strict',models['half'],strict);check('full_wall_contract_stays_strict',not r['success'],r)
        # Keep the same independent expectation, deliberately omit the second intersecting hole.
        d=intersect();d['operations'].pop();d['recovery']=dict(enabled=False)
        r=compile(d,'missing_cross_cut');assert r['success'],r
        r=client.call('cad_build_model',irJson=r['ir_json']);save('missing_cross_cut',r)
        check('missing_cross_cut_not_delivered',not r['success'] and r['native_path'] is None,r)
        gap=next(c for c in r['verification']['checks'] if c['id']=='intersection_gap')
        check('uncut_wall_clearance_rejected',not gap['passed'],gap)
        d=half();d['operations'].append(feature('remote_notch','Hole',diameter_mm=2,through_all=False,depth_mm=1,hole_centers=[dict(xmm=36,ymm=-3)],frame=dict(origin_mm=v(0,0,10),x_direction=v(1),normal=v(0,0,-1))))
        d['recovery']=dict(enabled=False)
        r=compile(d,'remote_notch');assert r['success'],r
        r=client.call('cad_build_model',irJson=r['ir_json']);save('remote_notch',r)
        check('remote_notch_not_delivered',not r['success'] and r['native_path'] is None,r)
        wall=next(c for c in r['verification']['checks'] if c['id']=='half_wall')
        check('area_detects_defect_between_samples',not wall['passed'] and 'Total area' in wall['message'] and 'Point ' not in wall['message'],wall)
        return results
    finally:client.close()

if __name__=='__main__':
    p=argparse.ArgumentParser();p.add_argument('--package',type=Path,default=ROOT/'plugins/auto-solidworks');p.add_argument('--output',type=Path);p.add_argument('--extended-only',action='store_true');p.add_argument('--reuse-models',type=Path,nargs='*',default=[])
    a=p.parse_args();out=a.output or ROOT/'artifacts'/('local-geometry-'+datetime.datetime.now().strftime('%Y%m%d-%H%M%S'))
    print(out,flush=True);run(a.package.resolve(),out.resolve(),a.extended_only,a.reuse_models)
