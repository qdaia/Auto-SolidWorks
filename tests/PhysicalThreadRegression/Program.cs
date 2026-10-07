using System.Text.Json;
using CadModeling.Core;
using CadModeling.Ir;

var output=Path.GetFullPath(args.Single());Directory.CreateDirectory(output);var checks=new List<string>();
void Check(bool ok,string name){if(!ok)throw new Exception(name);checks.Add(name);}
void Reject(Action action,string name){try{action();}catch(ArgumentException){Check(true,name);return;}throw new Exception("accepted "+name);}
var t=new PhysicalThreadOptions{Designation="M10x1.5",MajorDiameterMm=10,PitchMm=1.5,LengthMm=10,RunoutAllowanceMm=1.5,
    ProfilePath=Path.GetFullPath("Metric Die.SLDLFP"),ProfileSha256=new string('a',64),AxisOriginMm=new(0,0,20),AxisIntoPart=new(0,0,-1)};
PhysicalThreadContract.Validate(t);Check(true,"明确规格、固定牙型身份及收尾空间合同");
foreach(var bad in new[]{t with{Designation="M10"},t with{PitchMm=1},t with{MajorDiameterMm=double.NaN},t with{PitchMm=0},
    t with{LengthMm=1},t with{LengthMm=400},t with{RunoutAllowanceMm=0},t with{RunoutAllowanceMm=4},
    t with{AxisIntoPart=new(0,0,0)},t with{AxisOriginMm=new(double.NaN,0,0)},t with{ProfileSha256="bad"},t with{ProfilePath="Metric Die.SLDLFP"},
    t with{ProfilePath=Path.GetFullPath("Metric Tap.SLDLFP")}})Reject(()=>PhysicalThreadContract.Validate(bad),"规格负例 "+checks.Count);
IReadOnlyList<Vector3> Helix(double radius,double length=10,double pitch=1.5,bool right=true)=>Enumerable.Range(0,1025).Select(i=>{
    var z=length*i/1024;var a=z/pitch*2*Math.PI*(right?1:-1);return new Vector3(radius*Math.Cos(a),-radius*Math.Sin(a),20-z);}).ToArray();
IReadOnlyList<IReadOnlyList<Vector3>> Good()=>[Helix(5),Helix(4.2)];
var read=PhysicalThreadContract.VerifyGeometry(t,Good());Check(read.HelicalEdgeCount==2&&read.MaximumPitchErrorMm<1e-10,"独立解析螺旋边验证牙长、实切深度、螺距与右旋");
PhysicalThreadContract.VerifyGeometry(t with{RightHanded=false},[Helix(5,right:false),Helix(4.2,right:false)]);Check(true,"独立左旋正例");
Reject(()=>PhysicalThreadContract.VerifyGeometry(t,[Helix(5),Helix(4.2,right:false)]),"保存后旋向反转拒绝");
Reject(()=>PhysicalThreadContract.VerifyGeometry(t,[Helix(5,pitch:1),Helix(4.2,pitch:1)]),"实际螺距与规格不符拒绝");
Reject(()=>PhysicalThreadContract.VerifyGeometry(t,[Helix(5,length:1),Helix(4.2,length:1)]),"仅入口残牙不得冒充10mm牙长");
Reject(()=>PhysicalThreadContract.VerifyGeometry(t,[Helix(5),Helix(5)]),"无实切牙深拒绝");
Reject(()=>PhysicalThreadContract.VerifyGeometry(t,[Helix(6),Helix(4.2)]),"外径超限拒绝");
Reject(()=>PhysicalThreadContract.VerifyGeometry(t,[Helix(5,length:15),Helix(4.2,length:15)]),"牙型收尾超出声明空间拒绝");
Reject(()=>PhysicalThreadContract.VerifyGeometry(t,[Helix(5).Take(64).ToArray(),Helix(4.2)]),"不完整边采样拒绝");
Reject(()=>PhysicalThreadContract.VerifyGeometry(t,[]),"装饰标注及空实体库存拒绝");
Reject(()=>PhysicalThreadContract.VerifyGeometry(t,[Helix(5)]),"只有一条边的部分证据拒绝");
var shifted=Good().Select(e=>(IReadOnlyList<Vector3>)e.Select(p=>p with{Z=p.Z+2}).ToArray()).ToArray();
Reject(()=>PhysicalThreadContract.VerifyGeometry(t,shifted),"错误入口及轴向拒绝");
var nonconstant=Good().Select(e=>(IReadOnlyList<Vector3>)e.Select((p,i)=>p with{Z=p.Z+.1*Math.Sin(i*Math.PI/1024)}).ToArray()).ToArray();
Reject(()=>PhysicalThreadContract.VerifyGeometry(t,nonconstant),"非恒定螺距曲线拒绝");
var invalid=new NativeFeatureOptions{Kind=NativeFeatureKind.PhysicalThread,PhysicalThread=t};var errors=new List<ModelingDiagnostic>();
AdvancedFeatureValidation.Validate(invalid,"thread",errors);Check(errors.Count>0,"缺少单一圆形入口不能编译");
var restored=JsonSerializer.Deserialize<PhysicalThreadOptions>(JsonSerializer.Serialize(t,ModelingIrJson.Options),ModelingIrJson.Options)!;
PhysicalThreadContract.Validate(restored);Check(restored==t,"实体螺纹合同完整JSON往返");
var inner=t with{Location=PhysicalThreadLocation.Internal,ProfilePath=Path.GetFullPath("Metric Tap.SLDLFP"),BoreDiameterMm=8.5,MaximumCutDiameterMm=10.123797625};
Check(JsonSerializer.Serialize(PhysicalThreadLocation.Internal)=="\"Internal\""&&JsonSerializer.Deserialize<PhysicalThreadLocation>("\"Internal\"")==PhysicalThreadLocation.Internal,
    "MCP 默认序列化边界保留内外位置字符串枚举");
PhysicalThreadContract.Validate(inner);Check(true,"名义内螺纹必须声明独立底孔与真实切削直径");
foreach(var bad in new[]{inner with{BoreDiameterMm=null},inner with{MaximumCutDiameterMm=null},inner with{BoreDiameterMm=10},
    inner with{BoreDiameterMm=double.NaN},inner with{MaximumCutDiameterMm=double.PositiveInfinity},inner with{MaximumCutDiameterMm=12},
    inner with{ProfilePath=t.ProfilePath},t with{BoreDiameterMm=8.5},t with{MaximumCutDiameterMm=10.1},inner with{Location=(PhysicalThreadLocation)99}})
    Reject(()=>PhysicalThreadContract.Validate(bad),"内螺纹显式合同负例 "+checks.Count);
var cutRadius=inner.MaximumCutDiameterMm!.Value/2;
var innerGood=PhysicalThreadContract.VerifyGeometry(inner,[Helix(4.25),Helix(cutRadius)]);
Check(Math.Abs(innerGood.MinimumRadiusMm-4.25)<1e-10&&Math.Abs(innerGood.MaximumRadiusMm-cutRadius)<1e-10,"内螺纹实际底孔、槽底、牙长、螺距、旋向核验");
PhysicalThreadContract.VerifyGeometry(inner with{RightHanded=false},[Helix(4.25,right:false),Helix(cutRadius,right:false)]);Check(true,"内螺纹左旋几何正例");
Reject(()=>PhysicalThreadContract.VerifyGeometry(inner with{MaximumCutDiameterMm=10},[Helix(4.25),Helix(cutRadius)]),"名义M10尺寸不能掩盖超出声明的实际槽底");
Reject(()=>PhysicalThreadContract.VerifyGeometry(inner,[Helix(4.1),Helix(cutRadius)]),"内螺纹侵入底孔拒绝");
Reject(()=>PhysicalThreadContract.VerifyGeometry(inner,[Helix(4.25),Helix(5)]),"槽底没有达到独立声明值拒绝");
Reject(()=>PhysicalThreadContract.VerifyGeometry(inner,[Helix(4.25),Helix(cutRadius,right:false)]),"内螺纹错误旋向拒绝");
Reject(()=>PhysicalThreadContract.VerifyGeometry(inner,[Helix(4.25),Helix(cutRadius,pitch:1)]),"内螺纹错误螺距拒绝");
Reject(()=>PhysicalThreadContract.VerifyGeometry(inner,[Helix(4.25,length:.75),Helix(cutRadius),Helix(cutRadius)]),"重复同一螺旋曲线不得凑足两条独立边");
var phased=Helix(cutRadius).Select(p=>p with{Z=p.Z-.1875}).ToArray();
var fragmentedBore=Enumerable.Range(0,14).Select(i=>(IReadOnlyList<Vector3>)Helix(4.25,length:.75).Select(p=>p with{Z=p.Z-i*.75}).ToArray()).ToArray();
PhysicalThreadContract.VerifyGeometry(inner,[..fragmentedBore,Helix(cutRadius),phased]);Check(true,"周期孔壁分段与两条独立完整槽底螺旋共同证明实体牙型");
var innerRestored=JsonSerializer.Deserialize<PhysicalThreadOptions>(JsonSerializer.Serialize(inner,ModelingIrJson.Options),ModelingIrJson.Options)!;
Check(innerRestored==inner,"内螺纹新增合同字段完整JSON往返");
File.WriteAllText(Path.Combine(output,"summary.json"),JsonSerializer.Serialize(new{status="pass",passed=checks.Count,checks,native_acceptance="not_run"},ModelingIrJson.Options));
Console.WriteLine("Offline physical thread checks passed: "+checks.Count);
