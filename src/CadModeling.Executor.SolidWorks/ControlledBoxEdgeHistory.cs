using System.Text.Json;
using CadModeling.Core;
using CadModeling.Ir;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using Environment = System.Environment;

internal sealed partial class SolidWorksComExecutor
{
    private static string NativeTopologyHistoryRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AutoSolidWorks", "semantic-history", "v1");

    private sealed class ControlledBoxEdgeHistory
    {
        private readonly ModelingPlan _plan;
        private readonly BoxEdgeHistorySpec _spec;
        private readonly string _path;
        private readonly string _id = Guid.NewGuid().ToString("N");
        private readonly DurableTopologyHistoryStore _store = new(NativeTopologyHistoryRoot);
        private readonly List<TopologySnapshot> _snapshots = [];
        private readonly List<TopologyTransition> _transitions = [];
        private readonly List<TopologyCheckpointProof> _proofs = [];
        private string? _filletInput;
        public GeometryRef RootReference { get; private set; } = null!;

        public ControlledBoxEdgeHistory(IModelDoc2 model, ModelingPlan plan)
        {
            BoxEdgeHistoryContract.Validate(plan); _plan = plan; _spec = plan.BoxEdgeHistory!; _path = Path.GetFullPath(plan.Output.NativePath!);
            new SolidWorksDesignIntentSession(model).EnsureEditable();
            if ((model.GetConfigurationNames() as string[] ?? []).Length != 1)
                throw new InvalidOperationException("受控历史当前只允许单配置源模型。");
            RequireDimensions(model, _spec.InitialThicknessMm);
            var g = MeasureGeometry(model);
            if (g.SolidBodyCount != 1 || g.SurfaceBodyCount != 0 || g.FaceCount != 6 || g.EdgeCount != 12
                || Math.Abs(g.VolumeMm3 - _spec.WidthMm * _spec.LengthMm * _spec.InitialThicknessMm) > 1e-5
                || Math.Abs(g.BoundingBoxMm.X - _spec.WidthMm) > 1e-5 || Math.Abs(g.BoundingBoxMm.Y - _spec.LengthMm) > 1e-5
                || Math.Abs(g.BoundingBoxMm.Z - _spec.InitialThicknessMm) > 1e-5)
                throw new InvalidOperationException("源零件不是声明尺寸的独立矩形基体。");
            var baseFeature = FindFinalDefinitionFeature(model, _spec.BaseFeatureName);
            var baseOwner = RequireDefinitionIdentity(model, baseFeature);
            SaveClean(model);
            var sha = ReadInspectionHash(_path);
            var doc = Document(0, sha);
            var expected = BoxEdgeHistoryContract.Expected(_spec, _spec.InitialThicknessMm);
            var queryReference = new GeometryRef { RefId = _spec.SemanticKey, DocumentId = doc.DocumentId,DocumentPath = _path,
                ModelSha256 = sha,EntityKind = EntityKind.Edge,GeometryKind=GeometryKind.Line,FeatureId=_spec.BaseFeatureName,Signature=expected };
            var candidates = BuildGeometryCandidates(model, queryReference);
            var roots = candidates.Where(c => GeometryOwnership.HasOwner(c, baseOwner, _spec.BaseFeatureName)
                && GeometryRefResolver.Matches(queryReference, c)).ToArray();
            if (roots.Length != 1) throw new InvalidOperationException("独立源定义不能唯一绑定根边。");
            RootReference = queryReference with { NativePersistentReference = roots[0].NativePersistentReference,
                DocumentRevision = doc.DocumentRevision, SourceRevisionId = doc.SourceRevisionId, OperationId = "source_base",
                SourceFactIds = [_spec.WidthDimensionName,_spec.LengthDimensionName,_spec.ThicknessDimensionName,
                    _spec.FilletOperationId+":radius", "source:right-top-edge", "source:front-positive-Y-end"],
                Semantic = new() { SemanticKey=_spec.SemanticKey,RootToken=Token(0,roots[0].CandidateId),RootOwnerPersistentReference=baseOwner,
                    RootConfiguration=model.ConfigurationManager.ActiveConfiguration.Name,AllowedOperationIds=[_spec.ResizeOperationId,_spec.FilletOperationId] } };
            RecordSnapshot(model, 0);
        }

        public NativeFeatureOperation Prepare(IModelDoc2 model, NativeFeatureOperation operation)
        {
            if (operation.Id != _spec.FilletOperationId) return operation;
            var expected = BoxEdgeHistoryContract.Expected(_spec, ((NativeFeatureOperation)_plan.Operations[0]).Options.DimensionValue);
            var reference = RootReference with { Semantic=null,GeometryKind=expected.GeometryKind,Signature=expected };
            var matches = BuildGeometryCandidates(model, reference).Where(c => GeometryRefResolver.Matches(reference,c)).ToArray();
            if (matches.Length != 1 || matches[0].NativePersistentReference is not { Length: > 0 } pid)
                throw new InvalidOperationException("圆角输入源边不唯一或缺原生身份。");
            _filletInput = pid;
            return operation with { Options=operation.Options with { Selections=[operation.Options.Selections[0] with {
                PersistentReference=pid,RequirePersistentIdentity=true }] } };
        }

        public void RecordOperation(IModelDoc2 model, NativeFeatureOperation operation, IFeature feature)
        {
            var index = _snapshots.Count;
            RequireDimensions(model, ((NativeFeatureOperation)_plan.Operations[0]).Options.DimensionValue);
            SaveClean(model); RecordSnapshot(model,index);
            var before = _snapshots[index-1]; var after = _snapshots[index];
            var mappings = new List<TopologyMapping>(); var used = new HashSet<string>(); var lost = new List<TopologyElement>();
            foreach (var e in before.Elements)
            {
                var matches = after.Elements.Where(n => SamePersistentIdentity(model,e.Candidate.NativePersistentReference!,n.Candidate.NativePersistentReference!)).ToArray();
                if (matches.Length > 1 || matches.Length == 1 && !used.Add(matches[0].Token))
                    throw new InvalidOperationException("受控操作原生身份映射不唯一。");
                if (matches.Length == 0) { lost.Add(e); continue; }
                mappings.Add(new() { Relation=TopologyRelation.Modified,InputTokens=[e.Token],OutputTokens=[matches[0].Token] });
            }
            var generated = after.Elements.Where(e => !used.Contains(e.Token)).ToArray();
            var owner = RequireDefinitionIdentity(model,feature);
            if (operation.Id == _spec.ResizeOperationId)
            {
                if (lost.Count != 0 || generated.Length != 0 || before.Elements.Count != after.Elements.Count)
                    throw new InvalidOperationException("改厚度的完整库存不满足一对一受控历史。");
            }
            else
            {
                if (lost.Count != 1 || generated.Length != 4 || lost[0].Candidate.NativePersistentReference != _filletInput
                    || generated.Any(e => !GeometryOwnership.HasOwner(e.Candidate,owner,feature.Name)
                        || !GeometryOwnership.HasOwner(e.Candidate,RootReference.Semantic!.RootOwnerPersistentReference,_spec.BaseFeatureName)))
                    throw new InvalidOperationException("单边圆角的完整输入／四后继因果历史不成立。");
                mappings.Add(new() { Relation=TopologyRelation.Generated,InputTokens=[lost[0].Token],OutputTokens=generated.Select(e=>e.Token).ToArray() });
            }
            _transitions.Add(new() { BeforeRevisionKey=before.RevisionKey,AfterRevisionKey=after.RevisionKey,OperationId=operation.Id,
                NativeOperationPersistentReference=owner,CompleteHistory=true,Mappings=mappings });
        }

        public (string RecordPath, GeometryDocumentIdentity Endpoint) Complete(IModelDoc2 model)
        {
            if (_snapshots.Count != 3 || _transitions.Count != 2) throw new InvalidOperationException("受控历史未完成全部操作。");
            var old = _snapshots[^1]; var last = Snapshot(model,2);
            if (old.Elements.Count != last.Elements.Count || old.Elements.Any(e => !last.Elements.Any(n => n.Token == e.Token
                && SemanticTopologyResolver.SameCandidate(e.Candidate,n.Candidate))))
                throw new InvalidOperationException("最终保存展示改变了受控操作终点库存；不能重标终点哈希。");
            _snapshots[^1] = last; _proofs[^1] = _store.SaveCheckpoint(_path,last.Document.ModelSha256);
            var capture = new TopologyHistoryCapture { Provider="native-controlled-box-edge-history/v1",CompleteInventory=true,
                SavedModelIdentityVerified=true,Snapshots=_snapshots.ToArray(),Transitions=_transitions.ToArray(),Digest="" };
            capture = capture with { Digest=SemanticTopologyResolver.Digest(capture) };
            return (_store.Save(new(RootReference,capture,_proofs.ToArray())),last.Document);
        }

        private void RecordSnapshot(IModelDoc2 model,int index)
        { var snapshot=Snapshot(model,index);_snapshots.Add(snapshot);_proofs.Add(_store.SaveCheckpoint(_path,snapshot.Document.ModelSha256)); }
        private TopologySnapshot Snapshot(IModelDoc2 model,int index)
        {
            if (model.GetSaveFlag() || !RequireValidNativeBodies(model).Passed) throw new InvalidOperationException("受控历史快照未保存或原生体故障。");
            var sha=ReadInspectionHash(_path);var candidates=BuildGeometryCandidates(model,RootReference);
            if (model.GetSaveFlag() || ReadInspectionHash(_path)!=sha) throw new InvalidOperationException("受控历史库存读取期间保存身份变化。");
            var thickness=index==0?_spec.InitialThicknessMm:((NativeFeatureOperation)_plan.Operations[0]).Options.DimensionValue;
            double? radius=index==2?((NativeFeatureOperation)_plan.Operations[1]).Options.RadiusMm:null;
            var doc=Document(index,sha);
            return new() { RevisionKey="controlled:"+_id+":"+index,Document=doc,Configuration=model.ConfigurationManager.ActiveConfiguration.Name,
                Elements=candidates.Select(c=>new TopologyElement{Token=Token(index,c.CandidateId),Candidate=c}).ToArray(),
                SourceBindings=[new(){SemanticKey=_spec.SemanticKey,SourceRevisionId=doc.SourceRevisionId!,SourceFactIds=RootReference.SourceFactIds,
                    ExpectedSignature=BoxEdgeHistoryContract.Expected(_spec,thickness,radius)}] };
        }
        private GeometryDocumentIdentity Document(int index,string sha) => GeometryDocumentIdentity.FromSavedPath(_path,sha,"controlled:"+_id+":"+index)
            with { SourceRevisionId="controlled-source:"+_id+":"+index };
        private string Token(int index,string id)=>"controlled:"+_id+":"+index+":"+id;
        private void SaveClean(IModelDoc2 model)
        {
            if (!model.ForceRebuild3(false)) throw new InvalidOperationException("受控快照重建失败。");
            int errors=0,warnings=0;
            if (!model.Save3((int)swSaveAsOptions_e.swSaveAsOptions_Silent,ref errors,ref warnings) || errors!=0 || model.GetSaveFlag())
                throw new IOException("受控历史快照保存失败。");
        }
        private void RequireDimensions(IModelDoc2 model,double thickness)
        {
            foreach (var pair in new[] { (_spec.WidthDimensionName,_spec.WidthMm),(_spec.LengthDimensionName,_spec.LengthMm),(_spec.ThicknessDimensionName,thickness) })
            {
                if (model.Parameter(pair.Item1) is not IDimension d || d.GetType()!=(int)swDimensionParamType_e.swDimensionParamTypeDoubleLinear
                    || d.DrivenState!=(int)swDimensionDrivenState_e.swDimensionDriving || HasActiveDimensionEquation(model,pair.Item1)
                    || Math.Abs(d.GetSystemValue2("")*1000-pair.Item2)>1e-6)
                    throw new InvalidOperationException("独立源尺寸的原生身份／驱动值不符："+pair.Item1);
            }
        }
    }
}
