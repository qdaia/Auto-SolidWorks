"""Small native reproductions from the hollow-shaft task; dimensions are test fixtures."""
import math
import json
from pathlib import Path

def v(x=0,y=0,z=0): return dict(x=x,y=y,z=z)
def frame(origin,normal,xdir): return dict(origin_mm=origin,normal=normal,x_direction=xdir)
def native(name,kind,**kwargs): return dict(type='NativeFeature',id=name,name=name,feature=dict(kind=kind,**kwargs))
def sketch(name,primitives,location):
    return dict(type='ProfileSketch',id=name,name=name,plane='Front',frame=location,primitives=primitives)
def circ(d,x=0,y=0): return dict(type='Circle',center_xmm=x,center_ymm=y,diameter_mm=d)
def block():
    return [
        sketch('BlankProfile',[dict(type='CenteredRectangle',center_xmm=0,center_ymm=0,width_mm=385,height_mm=385)],
            frame(v(),v(1),v(0,1))),
        dict(type='ExtrudeBoss',id='Blank',name='Blank',sketch_id='BlankProfile',depth_mm=1555,end_condition='Blind'),
        native('VisibleAxis','ReferenceAxis',axis_start_mm=v(),axis_end_mm=v(1600))
    ]
def hole(name,location,centers,d=14,depth=35,reverse=True,**kwargs):
    return native(name,'Hole',frame=location,hole_centers=centers,diameter_mm=d,depth_mm=depth,
                  through_all=False,reverse=reverse,**kwargs)
def draft(name,ops,volume):
    return dict(name=name,source_text='Independent native regression for drawing workflow reliability.',
                operations=ops,expected_solid_body_count=1,expected_bounding_box_mm=v(1555,385,385),
                expected_volume_mm3=volume,volume_tolerance_percent=.001)

centers=[dict(xmm=85,ymm=0),dict(xmm=1465,ymm=0)]
side_ops=block()
for name,n in [('Top',v(0,1)),('Front',v(0,0,1)),('Rear',v(0,0,-1))]:
    origin={k:192.5*a for k,a in n.items()}
    side_ops.append(hole(name,frame(origin,n,v(1)),centers))
bolt_centers=[dict(xmm=150*math.cos(i*math.pi/6),ymm=150*math.sin(i*math.pi/6)) for i in range(12)]
end_ops=block()+[hole('EndTaps',frame(v(),v(1),v(0,1)),bolt_centers,d=21,depth=38,reverse=False,
                     hole_kind='Tapped',thread_major_diameter_mm=24,thread_designation='M24-7H')]
workflow_cases={
    'workflow_opposite_holes':draft('workflow_opposite_holes',side_ops,1555*385*385-6*math.pi*7**2*35),
    'workflow_end_taps':draft('workflow_end_taps',end_ops,1555*385*385-12*math.pi*10.5**2*38)
}
expected_holes={'workflow_opposite_holes':(7,6),'workflow_end_taps':(10.5,12)}

# Private real-drawing fixtures are intentionally not distributed.

def verify_workflow(call,native_path,name):
    groups=[(10.5,12),(8.75,24),(7,6),(10,2)] if name=='workflow_shaft_holes' else [expected_holes[name]]
    counts={}
    r=call('cad_inspect_model',dict(inputPath=str(native_path),queries=[
        dict(kind='Face',geometry='Cylinder',radius_mm=radius,tolerance_mm=.001,all_matches=True)
        for radius,_ in groups]))
    assert r['success'],r
    for radius,count in groups:
        actual_count=sum(abs(e['radius_mm']-radius)<.001 for e in r['entities'])
        assert actual_count==count,(name,'cylinder count',actual_count,count)
        counts[str(radius)]=count
    if name in ('workflow_end_taps','workflow_shaft_end_taps','workflow_shaft_holes'):
        annotations=[f for f in r['features'] if f['type']=='CosmeticThread']
        expected=24 if name=='workflow_shaft_holes' else 12
        assert len(annotations)==expected,(name,'native cosmetic thread count',[(f['name'],f['type']) for f in r['features'] if 'Thread' in f['name']])
        assert all(not f['suppressed'] for f in annotations),'Thread annotation unexpectedly suppressed'
    native_geometry=r['geometry']
    import hashlib
    step=native_path.with_suffix('.STEP')
    before=hashlib.sha256(step.read_bytes()).hexdigest()
    imported=call('cad_inspect_model',dict(inputPath=str(step)))
    assert imported['success'] and imported['geometry'],imported
    assert hashlib.sha256(step.read_bytes()).hexdigest()==before,'STEP inspection changed the source file'
    actual=imported['geometry']
    assert actual['solid_body_count']==native_geometry['solid_body_count'],(name,'STEP solid count')
    for axis in ('x','y','z'):
        assert abs(actual['bounding_box_mm'][axis]-native_geometry['bounding_box_mm'][axis])<.01,(name,'STEP bounds',axis)
    relative_volume=abs(actual['volume_mm3']-native_geometry['volume_mm3'])/native_geometry['volume_mm3']
    assert relative_volume<1e-5,(name,'STEP volume',relative_volume)
    return dict(cylinders_by_radius_mm=counts,step_reimport=True,step_volume_relative_difference=relative_volume,step_geometry=actual)
