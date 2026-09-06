def assembly_cases(root,output):
    def source(name):return str(sorted((root/'artifacts').glob('*/'+name+'.SLDPRT'))[-1])
    def plan(name,components,mates=[],interference=0):
        return dict(name=name,native_path=str(output/(name+'.SLDASM')),export_paths=[str(output/(name+'.STEP'))],
            components=components,mates=mates,_expected_interference=interference)
    return {
        'assembly_distance':plan('assembly_distance',[
            dict(id='base',path=source('box_fillet'),fixed=True),dict(id='pin',path=source('driven_circle'),translation_mm=dict(x=0,y=0,z=30))
        ],mates=[dict(name='gap',kind='Distance',value=5,anti_aligned=True,
            first=dict(component_id='base',entity=dict(kind='Face',geometry='Plane',position_mm=dict(x=0,y=0,z=10))),
            second=dict(component_id='pin',entity=dict(kind='Face',geometry='Plane',position_mm=dict(x=0,y=0,z=0))))]),
        'assembly_lock':plan('assembly_lock',[
            dict(id='base',path=source('box_fillet'),fixed=True),dict(id='pin',path=source('driven_circle'),translation_mm=dict(x=100,y=0,z=0))
        ],mates=[dict(name='locked',kind='Lock',first=dict(component_id='base'),second=dict(component_id='pin'))]),
        'assembly_angle':plan('assembly_angle',[
            dict(id='base',path=source('box_fillet'),fixed=True),dict(id='pin',path=source('driven_circle'),translation_mm=dict(x=100,y=0,z=0))
        ],mates=[dict(name='angle',kind='Angle',value=30,
            first=dict(component_id='base',entity=dict(kind='Face',geometry='Plane',position_mm=dict(x=0,y=0,z=10))),
            second=dict(component_id='pin',entity=dict(kind='Face',geometry='Plane',position_mm=dict(x=0,y=0,z=0))))]),
        'assembly_parallel':plan('assembly_parallel',[
            dict(id='base',path=source('box_fillet'),fixed=True),dict(id='pin',path=source('driven_circle'),translation_mm=dict(x=100,y=0,z=0),rotation_degrees=dict(x=10,y=0,z=0))
        ],mates=[dict(name='parallel',kind='Parallel',
            first=dict(component_id='base',entity=dict(kind='Face',geometry='Plane',position_mm=dict(x=0,y=0,z=10))),
            second=dict(component_id='pin',entity=dict(kind='Face',geometry='Plane',position_mm=dict(x=0,y=0,z=0))))]),
        'assembly_perpendicular':plan('assembly_perpendicular',[
            dict(id='base',path=source('box_fillet'),fixed=True),dict(id='pin',path=source('driven_circle'),translation_mm=dict(x=100,y=0,z=0),rotation_degrees=dict(x=80,y=0,z=0))
        ],mates=[dict(name='perpendicular',kind='Perpendicular',
            first=dict(component_id='base',entity=dict(kind='Face',geometry='Plane',position_mm=dict(x=0,y=0,z=10))),
            second=dict(component_id='pin',entity=dict(kind='Face',geometry='Plane',position_mm=dict(x=0,y=0,z=0))))]),
        'assembly_interference':plan('assembly_interference',[
            dict(id='block_a',path=source('move_body'),fixed=True),
            dict(id='block_b',path=source('move_body'),translation_mm=dict(x=5,y=0,z=0),fixed=True)
        ],interference=750),
        'assembly_mate':plan('assembly_mate',[
            dict(id='base',path=source('box_fillet'),fixed=True),
            dict(id='pin',path=source('driven_circle'),translation_mm=dict(x=0,y=0,z=20))
        ],mates=[dict(name='touching_faces',kind='Coincident',anti_aligned=True,
            first=dict(component_id='base',entity=dict(kind='Face',geometry='Plane',position_mm=dict(x=0,y=0,z=10))),
            second=dict(component_id='pin',entity=dict(kind='Face',geometry='Plane',position_mm=dict(x=0,y=0,z=0))))]),
        'assembly_concentric':plan('assembly_concentric',[
            dict(id='tube',path=source('tube_chamfer'),fixed=True),
            dict(id='pin',path=source('driven_circle'),translation_mm=dict(x=50,y=0,z=10))
        ],mates=[dict(name='aligned_axes',kind='Concentric',
            first=dict(component_id='tube',entity=dict(kind='Face',geometry='Cylinder',radius_mm=30,position_mm=dict(x=30,y=0,z=50))),
            second=dict(component_id='pin',entity=dict(kind='Face',geometry='Cylinder',radius_mm=10,position_mm=dict(x=10,y=0,z=5))))])
    }
