"""Local source-binding helper. Uses original MCP validation; no OCR or native execution.

Persistent state shares immutable input handles and caches contracts by runtime DLL hash.
The complete raw tool receipts always remain on disk; the terminal gets bounded output.
"""
import argparse
import datetime
import hashlib
import importlib.util
import json
from pathlib import Path
import time
import uuid
import sys

# Importing the helper from a task directory should work without caller-side path
# setup; composable-workflow imports its sibling composable_model module.
_scripts_dir=str(Path(__file__).resolve().parent)
if _scripts_dir not in sys.path:sys.path.insert(0,_scripts_dir)
spec=importlib.util.spec_from_file_location('composable_workflow',Path(__file__).with_name('composable-workflow.py'))
workflow=importlib.util.module_from_spec(spec);spec.loader.exec_module(workflow)

def load(path):return json.loads(Path(path).read_text(encoding='utf-8-sig'))
def digest(path):return hashlib.sha256(Path(path).read_bytes()).hexdigest()
def write(path,value):
    with Path(path).open('x',encoding='utf-8') as file:json.dump(value,file,ensure_ascii=False,indent=2,allow_nan=False)
def pointer(value,section):
    if not section:return value
    if not section.startswith('/'):raise ValueError('section must be a JSON pointer')
    for token in section[1:].split('/'):
        token=token.replace('~1','/').replace('~0','~')
        value=value[int(token)] if isinstance(value,list) else value[token]
    return value

class BindingSession:
    def __init__(self,state,plugin=None):
        self.plugin=Path(plugin or Path(__file__).resolve().parents[1]);self.state=Path(state).resolve();self.state.mkdir(parents=True,exist_ok=True)
        self.output=self.state/'calls'/(datetime.datetime.now(datetime.timezone.utc).strftime('%Y%m%dT%H%M%S')+'-'+uuid.uuid4().hex[:8])
        self.client=None;self.started=time.perf_counter();self.events=[];self.closed=False
        dlls=sorted((self.plugin/'runtime/mcp').glob('*.dll'))
        if not dlls:raise ValueError('No runtime DLLs in plugin')
        self.runtime_hash=hashlib.sha256(json.dumps([(p.name,digest(p)) for p in dlls],separators=(',',':')).encode()).hexdigest()
        self.cache=self.state/'contracts'/self.runtime_hash;self.cache.mkdir(parents=True,exist_ok=True)
    def call(self,name,args):
        if self.closed:raise RuntimeError('BindingSession is closed')
        started=time.perf_counter();event={'tool':name,'started_utc':datetime.datetime.now(datetime.timezone.utc).isoformat(),'success':False}
        try:
            if self.client is None:self.client=workflow.McpClient(self.plugin,self.output,self.state/'artifacts')
            result=self.client.call(name,args);event['success']=True;return result
        finally:event['seconds']=time.perf_counter()-started;self.events.append(event)
    def contract(self,name,section=''):
        # An enum-like contract name is never a filename supplied unchecked by the caller.
        if not name or any(c not in 'abcdefghijklmnopqrstuvwxyz_' for c in name):raise ValueError('Invalid contract name')
        suffix='-'+hashlib.sha256(section.encode()).hexdigest()[:16] if section else ''
        path=self.cache/(name+suffix+'.json');hit=path.exists()
        if hit:
            saved=load(path)
            expected=hashlib.sha256(json.dumps(saved['schema'],ensure_ascii=False,sort_keys=True,separators=(',',':')).encode()).hexdigest()
            if expected!=saved['schema_sha256']:raise ValueError('Cached contract hash mismatch')
            value=saved['schema']
        else:
            value=self.call('cad_get_contract',{'contract':name,'section':section})
            sha=hashlib.sha256(json.dumps(value,ensure_ascii=False,sort_keys=True,separators=(',',':')).encode()).hexdigest()
            write(path,{'schema':value,'schema_sha256':sha,'runtime_hash':self.runtime_hash})
        return {'contract':name,'section':section,'runtime_hash':self.runtime_hash,'cache_hit':hit,'schema_path':str(path),'value':value,'fragment_is_standalone_schema':True}
    def close(self):
        if self.closed:return
        self.closed=True
        if self.client:self.client.close()
        self.output.mkdir(parents=True,exist_ok=True)
        write(self.output/'binding-timing.json',{'elapsed_seconds':time.perf_counter()-self.started,'events':self.events,'runtime_hash':self.runtime_hash,'native_execution':False})
    def __enter__(self):return self
    def __exit__(self,*exc):self.close()

def main():
    if hasattr(sys.stdout,'reconfigure'):sys.stdout.reconfigure(encoding='utf-8')
    parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('--state',required=True);parser.add_argument('--plugin')
    sub=parser.add_subparsers(dest='command',required=True)
    sub.add_parser('guide')
    a=sub.add_parser('assemble');a.add_argument('sheet');g=a.add_mutually_exclusive_group(required=True);g.add_argument('--draft');g.add_argument('--base');a.add_argument('--workspace',required=True)
    c=sub.add_parser('contract');c.add_argument('name');c.add_argument('--section',default='')
    s=sub.add_parser('store');s.add_argument('file');s.add_argument('--kind',choices=['draft','drawing_context'],default='draft')
    p=sub.add_parser('patch');p.add_argument('base');p.add_argument('file');p.add_argument('--kind',choices=['draft','drawing_context'],default='draft')
    w=sub.add_parser('workspace');w.add_argument('inventory');w.add_argument('--sha256',required=True);w.add_argument('--output',required=True);w.add_argument('--crops')
    b=sub.add_parser('bind');b.add_argument('base');b.add_argument('workspace');b.add_argument('file');b.add_argument('--kind',choices=['draft','drawing_context'],default='drawing_context')
    c=sub.add_parser('compile');c.add_argument('base');c.add_argument('workspace');c.add_argument('--native-output',required=True);c.add_argument('--review-output',required=True)
    r=sub.add_parser('read');r.add_argument('artifact');r.add_argument('--section',default='');r.add_argument('--offset',type=int,default=0);r.add_argument('--limit',type=int,default=50)
    args=parser.parse_args();session=BindingSession(args.state,args.plugin)
    try:
        if args.command=='guide':result=session.call('cad_get_binding_guide',{})
        elif args.command=='assemble':
            request={'workspaceId':args.workspace,'sheet':load(args.sheet)}
            if args.draft:request['draft']=load(args.draft)
            else:request['baseInputId']=args.base
            result=session.call('cad_assemble_source_bindings',request)
        elif args.command=='contract':result=session.contract(args.name,args.section)
        elif args.command=='store':result=session.call('cad_store_model_input',{'kind':args.kind,'input':load(args.file)})
        elif args.command=='patch':result=session.call('cad_store_model_input',{'kind':args.kind,'baseInputId':args.base,'edits':load(args.file)})
        elif args.command=='workspace':result=session.call('cad_prepare_binding_workspace',{'inventoryPath':str(Path(args.inventory).resolve()),'inventorySha256':args.sha256,'outputDirectory':str(Path(args.output).resolve()),'crops':load(args.crops) if args.crops else []})
        elif args.command=='bind':result=session.call('cad_bind_source_dimensions',{'baseInputId':args.base,'kind':args.kind,'workspaceId':args.workspace,'batch':load(args.file)})
        elif args.command=='compile':result=session.call('cad_compile_binding_review',{'draftId':args.base,'workspaceId':args.workspace,'nativeOutputPath':str(Path(args.native_output).resolve()),'reviewOutputDirectory':str(Path(args.review_output).resolve())})
        else:result=session.call('cad_read_result',{'artifactId':args.artifact,'section':args.section,'offset':args.offset,'limit':args.limit})
        session.output.mkdir(parents=True,exist_ok=True);result_path=session.output/'command-result.json';write(result_path,result)
        text=json.dumps(result,ensure_ascii=False,allow_nan=False)
        if len(text)>24000:text=json.dumps({'result_path':str(result_path),'output_truncated':True,'top_level_fields':list(result),'instruction':'Read the saved result or use a narrower contract/result section; no diagnostics were discarded.'},ensure_ascii=False)
        print(text)
    finally:session.close()
if __name__=='__main__':main()
