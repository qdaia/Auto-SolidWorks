"""Make bounded contact sheets for manual review of every declared native preview."""
import argparse,hashlib,json
from pathlib import Path
from PIL import Image,ImageDraw
parser=argparse.ArgumentParser();parser.add_argument('--verification',type=Path,required=True);args=parser.parse_args()
base=args.verification.resolve();out=base/'visual-review';out.mkdir(exist_ok=False);manifest=[]
groups=[]
for name in ('flange','plate','blind','counterbore'):groups.append(('recipe-'+name,list((base/'recipes-final').glob(name+'.*.bmp'))))
groups.append(('independent-hole-positions',list((base/'parametric-final').glob('flange.*.bmp'))))
for name in ('front','front-reverse','top','right','midplane','frame'):groups.append(('direction-'+name,list((base/'matrix-final').glob(name+'.*.bmp'))))
for name in ('UpToNext','UpToSurface'):groups.append((name,list((base/'delivery-readback'/name).glob('*.bmp'))))
for name in ('default-1','declared-1'):groups.append(('benchmark-'+name,list((base/'benchmark-final').glob(name+'.*.bmp'))))
for name,files in groups:
    assert len(files) in (2,5,6),(name,len(files))
    canvas=Image.new('RGB',(1440,1740),'#e3e7ed');draw=ImageDraw.Draw(canvas)
    ordered=sorted(files,key=lambda p:('isometric','front','top','right','left','back').index(p.stem.split('.')[-1]))
    for i,file in enumerate(ordered):
        pic=Image.open(file).convert('RGB');pic.thumbnail((720,540))
        x=(i%2)*720;y=(i//2)*580;canvas.paste(pic,(x,y+30));draw.text((x+12,y+8),name+' / '+file.stem.split('.')[-1],fill='black')
    output=out/(name+'.jpg');canvas.save(output,quality=95)
    manifest.append(dict(case=name,contact_sheet=str(output),files=[dict(path=str(f),sha256=hashlib.sha256(f.read_bytes()).hexdigest()) for f in ordered],review_status='pending'))
(out/'manifest.json').write_text(json.dumps(manifest,indent=2),encoding='utf-8')
print(json.dumps(dict(contact_sheets=len(manifest),native_previews=sum(len(g['files']) for g in manifest))))
