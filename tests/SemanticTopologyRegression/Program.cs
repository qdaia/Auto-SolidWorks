using System.Text.Json;
using CadModeling.Core;
using CadModeling.Ir;

if(args.Length!=1)throw new ArgumentException("提供新的纯离线输出目录。");
var output=Path.GetFullPath(args[0]);if(Directory.Exists(output))throw new IOException("请保留旧证据，使用新目录。");Directory.CreateDirectory(output);
var checks=new List<string>();
void Check(bool ok,string name){if(!ok)throw new Exception("FAIL: "+name);checks.Add(name);Console.WriteLine("PASS: "+name);}
string Sha(char c)=>new(c,64);
GeometrySignature Signature(double x,GeometryKind kind=GeometryKind.Line)=>new(){EntityKind=EntityKind.Edge,GeometryKind=kind,AnchorMm=new(x,0,10),Direction=new(0,1,0),RadiusMm=kind==GeometryKind.Circle?2:null};
GeometryDocumentIdentity Doc(int i)=>new(){DocumentId="document:one",ModelSha256=Sha((char)('A'+i)),SourceRevisionId="source:"+i,DocumentRevision="revision:"+i};
var reference=new GeometryRef{RefId="右上边",DocumentId="document:one",ModelSha256=Sha('A'),DocumentRevision="revision:0",SourceRevisionId="source:0",
    NativePersistentReference="native:root",EntityKind=EntityKind.Edge,GeometryKind=GeometryKind.Line,FeatureId="基体",OperationId="base",
    SourceFactIds=["width","height","fillet-radius"],Signature=Signature(40),
    Semantic=new(){SemanticKey="base.right-top-edge",RootToken="root",RootOwnerPersistentReference="owner:base",RootConfiguration="默认",AllowedOperationIds=["resize","fillet","join","delete"]}};
GeometryCandidate Candidate(string id,string pi,double x,string owner="owner:base",GeometryKind kind=GeometryKind.Line)=>new(){CandidateId=id,NativePersistentReference=pi,
    FeatureId=owner=="owner:base"?"基体":owner,OwnerFeaturePersistentReference=owner,Signature=Signature(x,kind)};
var root=Candidate("old-index-4","native:root",40);var foreign=Candidate("foreign-index-9","native:foreign",50,"owner:foreign");
var resized=Candidate("new-index-17","native:resized",50);
var filleted=Candidate("new-index-2","native:fillet",50,"owner:fillet",GeometryKind.Circle);
TopologySnapshot Snapshot(int revision,GeometrySignature expected,params (string Token,GeometryCandidate Candidate)[] elements)=>new(){RevisionKey="state:"+revision,Document=Doc(revision),Configuration="默认",
    Elements=elements.Select(e=>new TopologyElement{Token=e.Token,Candidate=e.Candidate}).ToArray(),
    SourceBindings=[new(){SemanticKey=reference.Semantic!.SemanticKey,SourceRevisionId="source:"+revision,SourceFactIds=reference.SourceFactIds,ExpectedSignature=expected}]};
var before=Snapshot(0,Signature(40),("root",root),("foreign",foreign));var after=Snapshot(1,Signature(50),("resized",resized),("foreign",foreign));
var afterFillet=Snapshot(2,Signature(50,GeometryKind.Circle),("filleted",filleted),("foreign",foreign));
TopologyMapping Map(TopologyRelation relation,string[] inputs,string[] outputs)=>new(){Relation=relation,InputTokens=inputs,OutputTokens=outputs};
var foreignUnchanged=Map(TopologyRelation.Unchanged,["foreign"],["foreign"]);
TopologyTransition Step(int a,int b,string operation,string owner,params TopologyMapping[] mappings)=>new(){BeforeRevisionKey="state:"+a,AfterRevisionKey="state:"+b,
    OperationId=operation,NativeOperationPersistentReference=owner,CompleteHistory=true,Mappings=mappings};
var resize=Step(0,1,"resize","owner:base",Map(TopologyRelation.Modified,["root"],["resized"]),foreignUnchanged);
var fillet=Step(1,2,"fillet","owner:fillet",Map(TopologyRelation.Modified,["resized"],["filleted"]),foreignUnchanged);
TopologyHistoryCapture Seal(TopologyHistoryCapture capture)=>capture with{Digest=SemanticTopologyResolver.Digest(capture)};
TopologyHistoryCapture Capture(TopologySnapshot[] snapshots,TopologyTransition[] transitions)=>Seal(new(){Provider="offline-independent-history-port",CompleteInventory=true,SavedModelIdentityVerified=true,Digest="",Snapshots=snapshots,Transitions=transitions});
var history=Capture([before,after],[resize]);var filletHistory=Capture([before,after,afterFillet],[resize,fillet]);
GeometryRefResolution Resolve(TopologyHistoryCapture? h,GeometryRef? r=null,GeometryDocumentIdentity? doc=null,IReadOnlyList<GeometryCandidate>? live=null)
{
    var last=h?.Snapshots.LastOrDefault()??before;
    return GeometryRefResolver.Resolve(r??reference,doc??last.Document,live??last.Elements.Select(e=>e.Candidate).ToArray(),h);
}
void Status(TopologyHistoryCapture? h,GeometryRefResolutionStatus expected,string name,GeometryRef? r=null,GeometryDocumentIdentity? doc=null,IReadOnlyList<GeometryCandidate>? live=null)
    =>Check(Resolve(h,r,doc,live).Status==expected,name);
var changed=Resolve(history);
Check(changed.Status==GeometryRefResolutionStatus.Resolved&&changed.Rebound&&changed.Candidate!.NativePersistentReference=="native:resized","改尺寸后坐标改变仍按源语义和 Modified 历史找到原目标");
Check(!GeometryRefResolver.Matches(reference,resized),"旧冻结签名不能自证语义解析");
Check(GeometryRefResolver.IsVerifiedResolution(changed),"完整回执重验通过");
var rounded=Resolve(filletHistory);
Check(rounded.Status==GeometryRefResolutionStatus.Resolved&&rounded.Candidate!.Signature.GeometryKind==GeometryKind.Circle&&rounded.SemanticReceipt!.OperationIds.SequenceEqual(new[]{"resize","fillet"}),"改尺寸再圆角后跟随两步来源历史及当前源几何");
Check(GeometryRefResolver.IsVerifiedResolution(rounded),"圆角变更回执完整重验");
var strictQuery=GeometryRefResolver.SelectionQuery(rounded);
Check(strictQuery.RequirePersistentIdentity&&strictQuery.PersistentReference=="native:fillet"&&!strictQuery.AllMatches,"语义修复选择保持严格实体身份，不能在原生端退化成坐标后备");
Check(!GeometryRefResolver.SelectionQuery(GeometryRefResolver.Resolve(reference with{Semantic=null},Doc(0),[root])).RequirePersistentIdentity,"已有非语义选择保持原合同");
List<ModelingDiagnostic> QueryErrors(EntityQuery q){var errors=new List<ModelingDiagnostic>();AdvancedFeatureValidation.Validate(new(){Selections=[q]},"selection",errors);return errors;}
Check(QueryErrors(strictQuery).Count==0,"严格身份选择合同有效");
Check(QueryErrors(strictQuery with{PersistentReference=null}).Count>0,"严格选择无持久目标必须在建模前拒绝");
Check(QueryErrors(strictQuery with{AllMatches=true}).Count>0,"严格选择不能变成全匹配");
try{GeometryRefResolver.SelectionQuery(rounded with{SemanticReceipt=null});throw new Exception("Accepted unverified selection");}
catch(ArgumentException){Check(true,"无回执成功状态不能转成可执行选择");}
Status(Capture([before],[]),GeometryRefResolutionStatus.Resolved,"未变修订根实体可解析");
Status(null,GeometryRefResolutionStatus.Unsupported,"没有历史时禁止冻结几何后备");
Status(history,GeometryRefResolutionStatus.WrongDocument,"不同文档拒绝",doc:Doc(1) with{DocumentId="other"});
Status(history,GeometryRefResolutionStatus.Stale,"终点模型哈希错误拒绝",doc:Doc(1) with{ModelSha256=Sha('F')});
Status(history,GeometryRefResolutionStatus.Stale,"终点源修订错误拒绝",doc:Doc(1) with{SourceRevisionId="unbound"});
Status(history,GeometryRefResolutionStatus.Stale,"起点模型错误拒绝",r:reference with{ModelSha256=Sha('F')});
Status(history,GeometryRefResolutionStatus.Stale,"起点原生实体身份错误拒绝",r:reference with{NativePersistentReference="another"});
Status(history,GeometryRefResolutionStatus.Stale,"来源特征身份错误拒绝",r:reference with{Semantic=reference.Semantic! with{RootOwnerPersistentReference="foreign"}});
Status(history,GeometryRefResolutionStatus.Stale,"原始配置错误拒绝",r:reference with{Semantic=reference.Semantic! with{RootConfiguration="other"}});
Status(history,GeometryRefResolutionStatus.Stale,"未授权变更操作拒绝",r:reference with{Semantic=reference.Semantic! with{AllowedOperationIds=["fillet"]}});
Status(history with{Digest=Sha('0')},GeometryRefResolutionStatus.Unsupported,"历史摘要损坏拒绝");
Status(Seal(history with{CompleteInventory=false}),GeometryRefResolutionStatus.Unsupported,"不完整库存拒绝");
Status(Seal(history with{SavedModelIdentityVerified=false}),GeometryRefResolutionStatus.Unsupported,"未证明已保存模型身份的历史拒绝");
Status(Seal(history with{Transitions=[resize with{CompleteHistory=false}]}),GeometryRefResolutionStatus.Unsupported,"不完整内核历史拒绝");
Status(Seal(history with{Transitions=[resize with{AfterRevisionKey="state:9"}]}),GeometryRefResolutionStatus.Unsupported,"不连续历史拒绝");
Status(Seal(history with{Transitions=[resize with{Mappings=[foreignUnchanged]}]}),GeometryRefResolutionStatus.Unsupported,"目标映射缺口拒绝");
Status(Seal(history with{Transitions=[resize with{Mappings=[..resize.Mappings,resize.Mappings[0]]}]}),GeometryRefResolutionStatus.Unsupported,"相互矛盾映射拒绝");
Status(Seal(history with{Transitions=[resize with{Mappings=[resize.Mappings[0] with{OutputTokens=["missing"]},foreignUnchanged]}]}),GeometryRefResolutionStatus.Unsupported,"映射到不存在实体拒绝");
Status(Seal(history with{Transitions=[resize with{Mappings=[resize.Mappings[0] with{Relation=(TopologyRelation)999},foreignUnchanged]}]}),GeometryRefResolutionStatus.Unsupported,"未知历史关系拒绝");
Status(Seal(history with{Transitions=[resize with{NativeOperationPersistentReference=""}]}),GeometryRefResolutionStatus.Unsupported,"操作持久身份缺失拒绝");
Status(Seal(history with{Transitions=[resize with{Mappings=[resize.Mappings[0] with{Relation=TopologyRelation.Unchanged},foreignUnchanged]}]}),GeometryRefResolutionStatus.Unsupported,"几何变化不能伪报 Unchanged");
var wrongOwner=after with{Elements=[new(){Token="resized",Candidate=resized with{OwnerFeaturePersistentReference="unrelated"}},after.Elements[1]]};
Status(Capture([before,wrongOwner],[resize]),GeometryRefResolutionStatus.Unsupported,"后继实体错误归属拒绝");
var wrongFacts=after with{SourceBindings=[after.SourceBindings[0] with{SourceFactIds=["width"]}]};
Status(Capture([before,wrongFacts],[resize]),GeometryRefResolutionStatus.Unsupported,"不能丢掉源事实来通过");
var staleSource=after with{SourceBindings=[after.SourceBindings[0] with{SourceRevisionId="source:0"}]};
Status(Capture([before,staleSource],[resize]),GeometryRefResolutionStatus.Unsupported,"旧版源几何不能冒充当前源");
var missingSource=after with{SourceBindings=[]};
Status(Capture([before,missingSource],[resize]),GeometryRefResolutionStatus.Unsupported,"缺源定义拒绝");
var ambiguousSource=after with{SourceBindings=[after.SourceBindings[0],after.SourceBindings[0]]};
Status(Capture([before,ambiguousSource],[resize]),GeometryRefResolutionStatus.Unsupported,"重复源语义拒绝");
var wrongExpected=after with{SourceBindings=[after.SourceBindings[0] with{ExpectedSignature=Signature(60)}]};
Status(Capture([before,wrongExpected],[resize]),GeometryRefResolutionStatus.Stale,"实际实体不满足独立当前源几何拒绝");
var frozenExpected=after with{SourceBindings=[after.SourceBindings[0] with{ExpectedSignature=Signature(40)}]};
Status(Capture([before,frozenExpected],[resize]),GeometryRefResolutionStatus.Stale,"不能沿用改尺寸前坐标后备");
Status(history,GeometryRefResolutionStatus.Stale,"历史实体已不在实际库存拒绝",live:[foreign]);
Status(history,GeometryRefResolutionStatus.Unsupported,"实际库存额外候选未入历史拒绝",live:[resized,foreign,Candidate("extra","extra",50)]);
Status(history,GeometryRefResolutionStatus.Stale,"实际库存持久身份与历史不同拒绝",live:[resized with{NativePersistentReference="replaced"},foreign]);
Status(history,GeometryRefResolutionStatus.Stale,"实际库存来源身份与历史不同拒绝",live:[resized with{OwnerFeaturePersistentReference="replaced"},foreign]);
var branchA=Candidate("branch-a","native:a",50,"owner:fillet");var branchB=Candidate("branch-b","native:b",60,"owner:fillet");
var splitSnapshot=Snapshot(1,Signature(50),("a",branchA),("b",branchB),("foreign",foreign));
var split=Step(0,1,"fillet","owner:fillet",Map(TopologyRelation.Split,["root"],["a","b"]),foreignUnchanged);
var splitHistory=Capture([before,splitSnapshot],[split]);
Check(Resolve(splitHistory).Candidate?.NativePersistentReference=="native:a","分裂后仅独立源判别条件可选定唯一分支");
var noPosition=splitSnapshot with{SourceBindings=[splitSnapshot.SourceBindings[0] with{ExpectedSignature=Signature(50) with{AnchorMm=null}}]};
Status(Capture([before,noPosition],[split]),GeometryRefResolutionStatus.Ambiguous,"分裂后相同方向两个分支必须报告歧义");
Status(Capture([before,splitSnapshot],[split with{Mappings=[split.Mappings[0] with{Relation=TopologyRelation.Modified},foreignUnchanged]}]),GeometryRefResolutionStatus.Unsupported,"不能把分裂伪装单一 Modified");
var joined=Candidate("joined","native:joined",50,"owner:join");var joinSnapshot=Snapshot(1,Signature(50),("joined",joined));
var join=Step(0,1,"join","owner:join",Map(TopologyRelation.Merged,["root","foreign"],["joined"]));
Status(Capture([before,joinSnapshot],[join]),GeometryRefResolutionStatus.Resolved,"明确允许的合并可保留源语义后继");
var deletedSnapshot=Snapshot(1,Signature(50),("foreign",foreign));var delete=Step(0,1,"delete","owner:base",Map(TopologyRelation.Deleted,["root"],[]),foreignUnchanged);
Status(Capture([before,deletedSnapshot],[delete]),GeometryRefResolutionStatus.Stale,"删除目标不能选几何相同的其他来源实体");
var regeneratedSnapshot=Snapshot(1,Signature(50),("generated",resized with{OwnerFeaturePersistentReference="owner:fillet",FeatureId="owner:fillet"}),("foreign",foreign));
var generated=Step(0,1,"fillet","owner:fillet",Map(TopologyRelation.Generated,["root"],["generated"]),foreignUnchanged);
Status(Capture([before,regeneratedSnapshot],[generated]),GeometryRefResolutionStatus.Resolved,"显式 Generated 来源路径可解析");
Check(!GeometryRefResolver.IsVerifiedResolution(changed with{SemanticReceipt=null}),"没有完整回执的成功状态不能驱动修复");
Check(!GeometryRefResolver.IsVerifiedResolution(changed with{ResolvedModelSha256=Sha('F')}),"回执与实际模型哈希不一致拒绝");
Check(!GeometryRefResolver.IsVerifiedResolution(changed with{CandidateIds=["foreign-index-9"]}),"回执候选 ID 被篡改拒绝");
Check(!GeometryRefResolver.IsVerifiedResolution(changed with{Candidate=resized with{OwnerFeaturePersistentReference="foreign"}}),"回执候选所有权篡改拒绝");
Check(!GeometryRefResolver.IsVerifiedResolution(changed with{SemanticReceipt=changed.SemanticReceipt! with{SelectedToken="foreign"}}),"回执被选 token 篡改拒绝");
Check(!GeometryRefResolver.IsVerifiedResolution(changed with{SemanticReceipt=changed.SemanticReceipt! with{ReferenceFingerprint=Sha('0')}}),"回执引用指纹篡改拒绝");
Check(!GeometryRefResolver.IsVerifiedResolution(changed with{Rebound=false}),"回执重绑定状态篡改拒绝");
Check(!GeometryRefResolver.IsVerifiedResolution(changed with{NativeReferenceRecovered=true}),"新实体不能冒充原生原身份恢复");
Check(!GeometryRefResolver.IsVerifiedResolution(changed with{SemanticReceipt=changed.SemanticReceipt! with{CurrentSourceRevisionId="old"}}),"回执源修订篡改拒绝");
Check(!GeometryRefResolver.IsVerifiedResolution(changed with{SemanticReceipt=changed.SemanticReceipt! with{CurrentConfiguration="other"}}),"回执配置篡改拒绝");
var legacy=reference with{Semantic=null};
Check(GeometryRefResolver.Resolve(legacy,Doc(0),[root]).Status==GeometryRefResolutionStatus.Resolved,"既有冻结签名接口保留");
Check(GeometryRefResolver.Fingerprint(legacy with{RefId="a|b",DocumentId="c"})!=GeometryRefResolver.Fingerprint(legacy with{RefId="a",DocumentId="b|c"}),"字段分隔符不能造成引用指纹碰撞");
Check(GeometryRefResolver.Fingerprint(legacy with{SourceFactIds=["a,b","c"]})!=GeometryRefResolver.Fingerprint(legacy with{SourceFactIds=["a","b,c"]}),"源事实逗号不能造成引用指纹碰撞");
Check(GeometryRefResolver.Fingerprint(reference)==GeometryRefResolver.Fingerprint(reference with{SourceFactIds=reference.SourceFactIds.Reverse().ToArray(),Semantic=reference.Semantic! with{AllowedOperationIds=reference.Semantic.AllowedOperationIds.Reverse().ToArray()}}),"无序源事实与允许操作集合指纹稳定");
Check(GeometryRefResolver.Fingerprint(reference)!=GeometryRefResolver.Fingerprint(reference with{Semantic=reference.Semantic! with{RootToken="other"}}),"语义根身份进入引用指纹");
Check(GeometryRefResolver.Fingerprint(reference with{Signature=reference.Signature with{AnchorMm=new(-0.0,0,0)}})==GeometryRefResolver.Fingerprint(reference with{Signature=reference.Signature with{AnchorMm=new(0,0,0)}}),"负零经 JSON 往返不改变引用指纹");
var json=JsonSerializer.Serialize(rounded,ModelingIrJson.Options);var reread=JsonSerializer.Deserialize<GeometryRefResolution>(json,ModelingIrJson.Options)!;
Check(GeometryRefResolver.IsVerifiedResolution(reread),"JSON 往返保留完整历史回执且可重验");
var zeroBefore=before with{Elements=[before.Elements[0] with{Candidate=root with{Signature=root.Signature with{AnchorMm=new(40,-0.0,10)}}},before.Elements[1]]};
var zeroAfter=after with{Elements=[after.Elements[0] with{Candidate=resized with{Signature=resized.Signature with{AnchorMm=new(50,-0.0,10)}}},after.Elements[1]]};
var zeroHistory=Capture([zeroBefore,zeroAfter],[resize]);var zeroResolved=Resolve(zeroHistory);
Check(GeometryRefResolver.IsVerifiedResolution(JsonSerializer.Deserialize<GeometryRefResolution>(JsonSerializer.Serialize(zeroResolved,ModelingIrJson.Options),ModelingIrJson.Options)!),"历史中的负零不能导致 JSON 回执误报陈旧");
File.WriteAllText(Path.Combine(output,"resize-and-fillet-receipt.json"),json);
var session=new FakeHistorySession(filletHistory);Check(session.Capture(reference,Doc(2))!.Digest==filletHistory.Digest,"只读历史端口保留完整观察与源证据");
void Invalid(GeometryRef r,string name){try{GeometryRefResolver.Validate(r);}catch(ArgumentException){Check(true,name);return;}throw new Exception("Accepted "+name);}
Invalid(reference with{Semantic=reference.Semantic! with{RootOwnerPersistentReference=""}},"缺来源特征持久身份拒绝");
Invalid(reference with{Semantic=reference.Semantic! with{RootConfiguration=""}},"缺原始配置拒绝");
Invalid(reference with{SourceFactIds=[]},"缺源事实拒绝");
Invalid(reference with{InputToFeature="圆角"},"尚未实现的特征输入历史明确拒绝");
Invalid(reference with{Contract="unknown"},"未知引用合同版本拒绝");
Invalid(reference with{Signature=reference.Signature with{Direction=new(double.MaxValue,double.MaxValue,0)}},"巨大方向范数溢出不能形成 NaN 匹配通过");
var dualJson=JsonSerializer.SerializeToNode(root,ModelingIrJson.Options)!;
dualJson["feature_id"]=null;dualJson["owner_feature_persistent_reference"]=null;
dualJson["owner_features"]=JsonSerializer.SerializeToNode(new[]{new{featureId="基体",persistentReference="owner:base"},new{featureId="圆角",persistentReference="owner:fillet"}},ModelingIrJson.Options);
var dual=dualJson.Deserialize<GeometryCandidate>(ModelingIrJson.Options)!;
var dualRef=reference with{FeatureId="圆角",Semantic=reference.Semantic! with{RootOwnerPersistentReference="owner:fillet"}};
Status(Capture([Snapshot(0,Signature(40),("root",dual))],[]),GeometryRefResolutionStatus.Resolved,"共有边保留两个实际来源并可按圆角来源认证",r:dualRef);
var dualCapture=Capture([Snapshot(0,Signature(40),("root",dual))],[]);
Status(dualCapture,GeometryRefResolutionStatus.Stale,"来源名称与持久身份必须配对，不能交叉拼接",r:dualRef with{FeatureId="基体"});
Status(Capture([Snapshot(0,Signature(40),("root",dual with{OwnerFeatures=[dual.OwnerFeatures[0],dual.OwnerFeatures[0]]}))],[]),GeometryRefResolutionStatus.Unsupported,"重复来源持久身份拒绝",r:dualRef);
Status(Capture([Snapshot(0,Signature(40),("root",dual with{FeatureId="圆角",OwnerFeaturePersistentReference="owner:fillet"}))],[]),GeometryRefResolutionStatus.Unsupported,"多个来源不能同时伪报为单一来源",r:dualRef);
Check(!GeometryRefResolver.TryValidate(dual with{OwnerFeatures=null!},out _),"JSON 空来源集合明确拒绝");
Check(!GeometryRefResolver.TryValidate(dual with{OwnerFeatures=[new("","owner:base")]},out _),"来源名称缺失拒绝");
var sharedOutput=filleted with{FeatureId=null,OwnerFeaturePersistentReference=null,OwnerFeatures=[new("基体","owner:base"),new("圆角","owner:fillet")]};
var sharedAfter=afterFillet with{Elements=[new(){Token="filleted",Candidate=sharedOutput},afterFillet.Elements[1]]};
var sharedHistory=Capture([before,after,sharedAfter],[resize,fillet]);
var sharedResolved=Resolve(sharedHistory);
Check(sharedResolved.Status==GeometryRefResolutionStatus.Resolved && GeometryRefResolver.IsVerifiedResolution(sharedResolved),"后继共有边的全部来源均须来自输入或当前圆角操作");
Status(Capture([before,after,sharedAfter with{Elements=[new(){Token="filleted",Candidate=sharedOutput with{OwnerFeatures=[..sharedOutput.OwnerFeatures,new("外来","owner:foreign")] }},afterFillet.Elements[1]]}],[resize,fillet]),GeometryRefResolutionStatus.Unsupported,"共有边混入无关来源仍拒绝，不能只校验其中一个来源");
Check(!GeometryRefResolver.IsVerifiedResolution(sharedResolved with{Candidate=sharedOutput with{OwnerFeatures=[sharedOutput.OwnerFeatures[0]]}}),"回执重验识别被删掉的第二来源");
Check(SemanticTopologyResolver.SameCandidate(sharedOutput,sharedOutput with{OwnerFeatures=sharedOutput.OwnerFeatures.Reverse().ToArray()}),"来源集合比较不依赖相邻面枚举顺序");
Check(GeometryRefResolver.IsVerifiedResolution(JsonSerializer.Deserialize<GeometryRefResolution>(JsonSerializer.Serialize(sharedResolved,ModelingIrJson.Options),ModelingIrJson.Options)!),"共有边历史与完整回执可 JSON 往返");
var dualNext=Snapshot(1,Signature(40),("root",dual with{OwnerFeatures=[dual.OwnerFeatures[0]]}));
Status(Capture([Snapshot(0,Signature(40),("root",dual)),dualNext],[Step(0,1,"resize","owner:base",Map(TopologyRelation.Unchanged,["root"],["root"]))]),GeometryRefResolutionStatus.Unsupported,"Unchanged 不能掩盖来源集合减少",r:dualRef);
Check(GeometryRefResolver.Resolve(dualRef with{Semantic=null},Doc(0),[dual]).Status==GeometryRefResolutionStatus.Resolved,"冻结引用也可按共有边的实际来源限定");
var revisionBody = new GeometryCandidate { CandidateId="body",NativePersistentReference="body:1",Signature=new(){EntityKind=EntityKind.Body} };
var revisionFace = new GeometryCandidate { CandidateId="face",NativePersistentReference="face:1",FeatureId="基体",OwnerFeaturePersistentReference="owner:base",
    Signature=new(){EntityKind=EntityKind.Face,GeometryKind=GeometryKind.Plane,AnchorMm=new(0,0,10),Direction=new(0,0,1),AreaMm2=4000} };
var revision = new SavedRevisionState { ModelSha256=Sha('A'),Configuration="默认",ParameterState="dimensions:10;features:base;equations:none",Complete=true,CleanSavedModel=true,NativeBodiesValid=true,
    Geometry=new(1,1,2,40000,10600,new(0,0,5),new(80,50,10)),
    Entities=[new(revisionBody,[0,0,0,80,50,10]),new(revisionFace,[0,0,10,80,50,10]),new(root,[0,50]),new(foreign,[0,50])] };
bool Cert(SavedRevisionState other,bool clean=true,Func<string,string,bool>? native=null)=>SavedRevisionCertifier.Compare(revision,other,native??((a,b)=>a==b),clean).Passed;
Check(Cert(revision),"保存修订完整解析库存一致时通过");
Check(Cert(revision with{CleanSavedModel=false},false),"重建脏状态只用于比较，不认证为干净终点");
Check(!Cert(revision with{CleanSavedModel=false}),"重开终点仍脏必须拒绝");
Check(!SavedRevisionCertifier.Compare(revision with{CleanSavedModel=false},revision,(a,b)=>a==b,true).Passed,"脏起点不能以文件 SHA 自证");
Check(!Cert(revision with{ModelSha256=Sha('B')}),"修订比较拒绝磁盘文件变化");
Check(!Cert(revision with{Configuration="其他"}),"修订比较拒绝配置变化");
Check(!Cert(revision with{ParameterState="dimensions:12"}),"参数变化不能由近似几何掩盖");
Check(!Cert(revision with{Complete=false}),"不完整库存拒绝");
Check(!Cert(revision with{NativeBodiesValid=false}),"原生体故障拒绝");
Check(!Cert(revision with{Geometry=revision.Geometry with{VolumeMm3=40001}}),"重建体积变化拒绝");
Check(!Cert(revision with{Entities=revision.Entities.Take(3).ToArray()}),"漏实体库存拒绝");
Check(!Cert(revision with{Entities=[..revision.Entities,new(root,[0,50])]}),"重复实体标识拒绝");
SavedRevisionState AlterRoot(GeometryCandidate c,IReadOnlyList<double>? data=null)=>revision with{Entities=[revision.Entities[0],revision.Entities[1],new(c,data??[0,50]),revision.Entities[3]]};
Check(!Cert(AlterRoot(root with{NativePersistentReference="other"})),"修订实体身份变化拒绝");
Check(!Cert(AlterRoot(root with{OwnerFeaturePersistentReference="owner:other"})),"修订来源身份变化拒绝");
Check(!Cert(AlterRoot(root with{Signature=Signature(41)})),"同持久身份几何变化拒绝");
Check(!Cert(AlterRoot(root,[0,49])),"修剪范围变化拒绝");
Check(!Cert(AlterRoot(root,[0,double.NaN])),"非有限修剪数据拒绝");
Check(!Cert(AlterRoot(root with{Signature=Signature(40) with{GeometryKind=GeometryKind.Any}})),"未支持的自由形状不能用有限采样认证");
Check(Cert(revision with{Entities=revision.Entities.Reverse().ToArray()}),"完整身份双射不依赖枚举顺序");
var alias=AlterRoot(root with{NativePersistentReference="native:alias"});
Check(Cert(alias,native:(a,b)=>a==b||a=="native:root"&&b=="native:alias"),"字节不同但原生等价身份可被认证");
Check(!Cert(alias,native:(a,b)=>a=="native:root"||a==b),"原生身份对应多个候选时拒绝");
Check(!SavedRevisionCertifier.Compare(revision with{ModelSha256=new string('X',64)},revision,(a,b)=>a==b,true).Passed,"非法 SHA 不能冒充保存证据");
Check(Cert(JsonSerializer.Deserialize<SavedRevisionState>(JsonSerializer.Serialize(revision,ModelingIrJson.Options),ModelingIrJson.Options)!),"修订库存 JSON 往返保持合同");
var sphereFace = revisionFace with { Signature = revisionFace.Signature with {
    GeometryKind=GeometryKind.Sphere,AnchorMm=new(0,0,0),RadiusMm=20,Direction=null } };
var sphereRevision = revision with { Entities = [revision.Entities[0],
    new(sphereFace,[0,0,0,.02,0]) { BoundaryEdgeReferences=["native:root","native:foreign"] },revision.Entities[2],revision.Entities[3]] };
bool SphereCert(SavedRevisionState other,Func<string,string,bool>? native=null) =>
    SavedRevisionCertifier.Compare(sphereRevision,other,native??((a,b)=>a==b),true).Passed;
SavedRevisionState SphereChange(GeometryCandidate? candidate=null,IReadOnlyList<string>? boundary=null,bool omit=false,
    IReadOnlyList<double>? trim=null) => sphereRevision with { Entities = [sphereRevision.Entities[0],
        new(candidate??sphereFace,trim??sphereRevision.Entities[1].TrimData) {
            BoundaryEdgeReferences=omit?null:boundary??sphereRevision.Entities[1].BoundaryEdgeReferences },sphereRevision.Entities[2],sphereRevision.Entities[3]] };
Check(SphereCert(sphereRevision),"完整解析球面参数及边界身份可认证");
Check(SphereCert(SphereChange(boundary:["native:foreign","native:root"])),"球面边界身份比较不依赖枚举顺序");
Check(!SphereCert(SphereChange(omit:true)),"球面只有采样而无边界身份库存必须拒绝");
Check(!SphereCert(SphereChange(boundary:["native:root"])),"球面实际边界减少必须拒绝");
Check(!SphereCert(SphereChange(boundary:["native:root","native:root"])),"球面重复边界身份必须拒绝");
Check(!SphereCert(SphereChange(boundary:["native:root","other:edge"])),"球面边界身份替换必须拒绝");
var orphanBoundary = SphereChange(boundary:["native:root","other:edge"]);
Check(!SavedRevisionCertifier.Compare(orphanBoundary,orphanBoundary,(a,b)=>a==b,true).Passed,"相同未知球面边界不能冒充完整实体库存");
Check(!SphereCert(SphereChange(sphereFace with { Signature=sphereFace.Signature with {RadiusMm=21} })),"球面半径变化必须拒绝");
Check(!SphereCert(SphereChange(sphereFace with { Signature=sphereFace.Signature with {AnchorMm=new(0,0,1)} })),"球面中心变化必须拒绝");
Check(!SphereCert(SphereChange(sphereFace with { Signature=sphereFace.Signature with {RadiusMm=null} })),"球面缺解析半径必须拒绝");
Check(!SphereCert(SphereChange(sphereFace with { Signature=sphereFace.Signature with {AnchorMm=null} })),"球面缺解析中心必须拒绝");
Check(!SphereCert(SphereChange(trim:[0,0,0,.02,1])),"球面朝向变化必须拒绝");
Check(SphereCert(JsonSerializer.Deserialize<SavedRevisionState>(JsonSerializer.Serialize(sphereRevision,ModelingIrJson.Options),ModelingIrJson.Options)!),"球面参数及边界身份可 JSON 往返");
var trackedSpec = new BoxEdgeHistorySpec { WidthDimensionName="width@sketch",LengthDimensionName="length@sketch",ThicknessDimensionName="thickness@base",
    BaseFeatureName="base",WidthMm=80,LengthMm=50,InitialThicknessMm=10,ResizeOperationId="resize",FilletOperationId="fillet" };
var trackedResize = new NativeFeatureOperation {Id="resize",Name="改厚度",Options=new(){Kind=NativeFeatureKind.SetDimension,DimensionName="thickness@base",DimensionValue=12}};
var trackedFillet = new NativeFeatureOperation {Id="fillet",Name="单边圆角",DependsOn=["resize"],Options=new(){Kind=NativeFeatureKind.Fillet,RadiusMm=2,TangentPropagation=false,
    Selections=[new(){Kind=EntityKind.Edge,Geometry=GeometryKind.Line,PositionMm=new(40,0,12),Direction=new(0,1,0),ToleranceMm=.01}]}};
var trackedPlan = new ModelingPlan {PlanId="controlled",Name="受控编辑",SourceModelPath=Path.Combine(output,"source.SLDPRT"),BoxEdgeHistory=trackedSpec,
    Recovery=new(){Enabled=false},Output=new(){NativePath=Path.Combine(output,"target.SLDPRT")},Operations=[trackedResize,trackedFillet]};
bool TrackingValid(ModelingPlan p){try{BoxEdgeHistoryContract.Validate(p);return true;}catch(ArgumentException){return false;}}
Check(TrackingValid(trackedPlan),"受控两步源尺寸历史合同有效");
Check(!TrackingValid(trackedPlan with{SourceModelPath=null}),"不能在没有实际源副本时捕获历史");
Check(!TrackingValid(trackedPlan with{Recovery=new(){Enabled=true}}),"未认证的恢复历史拒绝");
Check(!TrackingValid(trackedPlan with{Output=trackedPlan.Output with{OverwriteAllowed=true}}),"受控历史不覆盖已有文档");
Check(!TrackingValid(trackedPlan with{Operations=[trackedFillet,trackedResize]}),"历史操作顺序拒绝倒置");
Check(!TrackingValid(trackedPlan with{Operations=[trackedResize,trackedFillet with{Options=trackedFillet.Options with{TangentPropagation=true}}]}),"传播圆角不能冒充单输入因果历史");
Check(!TrackingValid(trackedPlan with{BoxEdgeHistory=trackedSpec with{ThicknessDimensionName="other@base"}}),"修改维度与声明源维度不符拒绝");
Check(!TrackingValid(trackedPlan with{BoxEdgeHistory=trackedSpec with{WidthMm=double.NaN}}),"源定义非有限值拒绝");
Check(!TrackingValid(trackedPlan with{Operations=[trackedResize,trackedFillet with{Options=trackedFillet.Options with{Selections=[trackedFillet.Options.Selections[0] with{PersistentReference="caller:fake"}]}}]}),"调用者持久目标不能替代生产端输入捕获");
Check(BoxEdgeHistoryContract.Expected(trackedSpec,12,2).AnchorMm==new Vector3(38,25,10),"圆角前端语义从独立源尺寸推导");
var storePath=Path.Combine(output,"durable-history");var store=new DurableTopologyHistoryStore(storePath);
var savedPath=Path.Combine(output,"saved-target.SLDPRT");
var proofs=new List<TopologyCheckpointProof>();var savedDocs=new List<GeometryDocumentIdentity>();
for(int i=0;i<3;i++) {
    File.WriteAllText(savedPath,"offline-checkpoint:"+i);
    var fileSha=DrawingPlanValidation.FileHash(savedPath);
    proofs.Add(store.SaveCheckpoint(savedPath,fileSha));
    savedDocs.Add(GeometryDocumentIdentity.FromSavedPath(savedPath,fileSha,Doc(i).DocumentRevision) with{SourceRevisionId=Doc(i).SourceRevisionId});
}
var durableReference=reference with{DocumentId=savedDocs[0].DocumentId,DocumentPath=savedPath,ModelSha256=savedDocs[0].ModelSha256};
var durableCapture=Seal(sharedHistory with{Provider="native-controlled-box-edge-history/v1",Snapshots=sharedHistory.Snapshots.Select((s,i)=>s with{Document=savedDocs[i]}).ToArray()});

var spline3 = new SavedSplineCurve(2,3,2,false,[0,0,1,1],[0,0,0,.04,0,0]);
var trim2 = new SavedSplineCurve(2,2,2,false,[0,0,1,1],[0,0,1,0]);
var net = new SavedSplineSurface(2,2,2,2,3,false,false,true,false,[0,0,1,1],[0,0,1,1],[0,0,0,.04,0,0,0,.02,0,.04,.02,.01]);
var splineFace = revisionFace with { Signature=revisionFace.Signature with { GeometryKind=GeometryKind.BSpline,Direction=null } };
var thirdEdge=root with { CandidateId="spline:third",NativePersistentReference="native:third" };
var splineEdge=root with { Signature=root.Signature with { GeometryKind=GeometryKind.BSpline } };
var trimLoop=new SavedSplineTrimLoop(true,[new("native:root",true,trim2),new("native:foreign",true,trim2),new("native:third",false,trim2)]);
var splineRevision=revision with { Geometry=revision.Geometry with{EdgeCount=3},Entities=[revision.Entities[0],
    new(splineFace,[0,1,0,1]){BoundaryEdgeReferences=["native:root","native:foreign","native:third"],SplineSurface=net,SplineTrimLoop=trimLoop},
    new(splineEdge,[0,1]){SplineCurve=spline3},revision.Entities[3],new(thirdEdge,[0,1])] };
SavedRevisionState ChangeSplineFace(SavedSplineSurface? n=null,SavedSplineTrimLoop? l=null)=>splineRevision with{Entities=splineRevision.Entities.Select(e=>e.Candidate.CandidateId=="face"?e with{SplineSurface=n??net,SplineTrimLoop=l??trimLoop}:e).ToArray()};
bool SplineCert(SavedRevisionState other)=>SavedRevisionCertifier.Compare(splineRevision,other,(a,b)=>a==b,true).Passed;
Check(SplineCert(splineRevision),"完整非周期样条面、外环和三维样条边可认证");
Check(SplineCert(ChangeSplineFace(l:trimLoop with{Edges=[trimLoop.Edges[1],trimLoop.Edges[2],trimLoop.Edges[0]]})),"外环起始共边的循环移位不改变身份");
Check(!SplineCert(ChangeSplineFace(net with{ControlPoints=net.ControlPoints.Select((v,i)=>i==11?v+.001:v).ToArray()})),"改变未由包围盒或旧采样定义的内部控制点仍拒绝");
Check(!SplineCert(ChangeSplineFace(net with{UKnots=[0,0,.9,1]})),"完整 U 节点变化拒绝");
Check(!SplineCert(ChangeSplineFace(net with{VKnots=[0,0,.9,1]})),"完整 V 节点变化拒绝");
Check(!SplineCert(ChangeSplineFace(net with{UOrder=3})),"不完整阶数与节点关系拒绝");
Check(!SplineCert(ChangeSplineFace(net with{ControlPoints=net.ControlPoints.Take(11).ToArray()})),"漏控制坐标拒绝");
Check(!SplineCert(ChangeSplineFace(net with{ControlPoints=net.ControlPoints.Select((v,i)=>i==4?double.NaN:v).ToArray()})),"非有限控制网拒绝");
Check(!SplineCert(ChangeSplineFace(net with{UPeriodic=true})),"周期样条未认证");
Check(!SplineCert(ChangeSplineFace(net with{SameOrientation=false})),"转换方向不明的曲面拒绝");
Check(!SplineCert(ChangeSplineFace(net with{FaceInSurfaceSense=true})),"实际面方向变化拒绝");
Check(!SplineCert(ChangeSplineFace(l:trimLoop with{Outer=false})),"外环改内环拒绝");
Check(!SplineCert(ChangeSplineFace(l:trimLoop with{Edges=trimLoop.Edges.Reverse().ToArray()})),"外环顺序反转拒绝");
Check(!SplineCert(ChangeSplineFace(l:trimLoop with{Edges=[trimLoop.Edges[0] with{Sense=false},trimLoop.Edges[1],trimLoop.Edges[2]]})),"共边方向变化拒绝");
Check(!SplineCert(ChangeSplineFace(l:trimLoop with{Edges=[trimLoop.Edges[0] with{EdgeReference="native:other"},trimLoop.Edges[1],trimLoop.Edges[2]]})),"修剪曲线必须对应实际边界身份");
Check(!SplineCert(ChangeSplineFace(l:trimLoop with{Edges=[trimLoop.Edges[0] with{ParametricCurve=trim2 with{ControlPoints=[0,0,.9,0]}},trimLoop.Edges[1],trimLoop.Edges[2]]})),"参数空间修剪控制点变化拒绝");
Check(!SplineCert(splineRevision with{Entities=splineRevision.Entities.Select(e=>e.SplineCurve is null?e:e with{SplineCurve=spline3 with{ControlPoints=[0,0,0,.041,0,0]}}).ToArray()}),"三维样条边控制点变化拒绝");
Check(SplineCert(JsonSerializer.Deserialize<SavedRevisionState>(JsonSerializer.Serialize(splineRevision,ModelingIrJson.Options),ModelingIrJson.Options)!),"完整样条证据 JSON 往返保留");
var rationalNet=net with{Dimension=4,ControlPoints=[0,0,0,1,.04,0,0,1,0,.02,0,1,.04,.02,.01,2]};
var rationalState=ChangeSplineFace(rationalNet);
Check(SavedRevisionCertifier.Compare(rationalState,rationalState,(a,b)=>a==b,true).Passed,"实际有理控制网权重可保留并认证");
Check(!SavedRevisionCertifier.Compare(rationalState,ChangeSplineFace(rationalNet with{ControlPoints=rationalNet.ControlPoints.Select((v,i)=>i==15?3:v).ToArray()}),(a,b)=>a==b,true).Passed,"权重变化不能由相同控制坐标掩盖");
Check(!SplineCert(ChangeSplineFace(rationalNet with{ControlPoints=rationalNet.ControlPoints.Select((v,i)=>i==15?0:v).ToArray()})),"非正权重拒绝");
double Packed(int a,int b)=>BitConverter.Int64BitsToDouble(unchecked((long)(uint)a|((long)b<<32)));
var rawTrim=new List<double>{Packed(1,3),Packed(3,0)};
for(int n=0;n<3;n++){rawTrim.Add(Packed(2,0));rawTrim.Add(Packed(2,2));}
for(int n=0;n<3;n++)rawTrim.AddRange([0,0,1,1]);
for(int n=0;n<3;n++)rawTrim.AddRange([0,0,1,0]);rawTrim.Add(Packed(1,1));
Check(SavedSplineTrimDataReader.Read(rawTrim).Count==3,"打包整数必须按位解码，不按极小双精度数比较");
bool RejectTrim(IReadOnlyList<double> raw){try{SavedSplineTrimDataReader.Read(raw);return false;}catch(ArgumentException){return true;}}
Check(RejectTrim(rawTrim.Take(rawTrim.Count-1).ToArray()),"缺尾部修剪块拒绝");
Check(RejectTrim([..rawTrim,0]),"未消费的数据拒绝");
Check(RejectTrim(rawTrim.Select((v,i)=>i==0?Packed(2,3):v).ToArray()),"多外环尚未认证");
Check(RejectTrim(rawTrim.Select((v,i)=>i==rawTrim.Count-1?Packed(2,1):v).ToArray()),"多份周期修剪曲面尚未认证");
Check(RejectTrim(rawTrim.Select((v,i)=>i==2?Packed(2,1):v).ToArray()),"周期修剪曲线拒绝");
Check(RejectTrim(rawTrim.Select((v,i)=>i==10?double.PositiveInfinity:v).ToArray()),"修剪数据非有限节点拒绝");
var durable=new DurableTopologyHistory(durableReference,durableCapture,proofs);var recordPath=store.Save(durable);
Check(store.Load(durableReference,savedDocs[2]) is not null,"生产持久历史保存重读并检查不可变文件身份");
Check(store.Load(durableReference,savedDocs[2] with{SourceRevisionId=null,DocumentRevision=null}) is not null,"可信持久端点可提供缺省修订元数据");
bool StoreReject(Action action){try{action();return false;}catch(Exception ex) when(ex is ArgumentException or InvalidOperationException or IOException){return true;}}
Check(StoreReject(()=>store.Load(durableReference,savedDocs[2] with{ModelSha256=Sha('F')})),"陈旧当前模型不能复用持久历史");
Check(StoreReject(()=>store.Load(durableReference,savedDocs[2] with{SourceRevisionId="wrong"})),"调用者错误源修订不能覆盖认证元数据");
Check(store.Load(durableReference with{Semantic=durableReference.Semantic! with{RootOwnerPersistentReference="fake"}},savedDocs[2]) is null,"错误根来源无生产记录");
Check(StoreReject(()=>store.Save(durable with{Capture=durableCapture with{Provider="caller:fake"}})),"调用者自称提供者不能写成功记录");
Check(StoreReject(()=>store.Save(durable with{Checkpoints=[proofs[0] with{RelativePath="../outside.SLDPRT"},proofs[1],proofs[2]]})),"历史文件路径不能越出生产库");
var signedBytes=File.ReadAllText(recordPath);var signed=JsonSerializer.Deserialize<Dictionary<string,string>>(signedBytes,ModelingIrJson.Options)!;
signed["mac_hex"]=new string('0',64);File.WriteAllText(recordPath,JsonSerializer.Serialize(signed,ModelingIrJson.Options));
Check(StoreReject(()=>store.Load(durableReference,savedDocs[2])),"被篡改的历史 MAC 明确拒绝");
File.WriteAllText(recordPath,signedBytes);var checkpoint=Path.Combine(storePath,proofs[0].RelativePath);File.WriteAllText(checkpoint,"changed-by-test");
Check(StoreReject(()=>store.Load(durableReference,savedDocs[2])),"被替换的根检查点即使 MAC 原样也拒绝");
File.WriteAllText(Path.Combine(output,"summary.json"),JsonSerializer.Serialize(new{status="pass",passed=checks.Count,
    scope="offline_source_semantics_complete_topology_history_and_receipt_replay",native_acceptance="not_run",native_history_backend="unchanged_revision_only; changed_kernel_history_still_unavailable",checks},ModelingIrJson.Options));
Console.WriteLine($"通过 {checks.Count} 项纯离线检查；未读取原生模型。");

sealed class FakeHistorySession(TopologyHistoryCapture capture):ISemanticTopologyHistorySession
{ public TopologyHistoryCapture? Capture(GeometryRef reference,GeometryDocumentIdentity currentDocument)=>capture; }
