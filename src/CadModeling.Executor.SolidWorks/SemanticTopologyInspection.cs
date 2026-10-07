using CadModeling.Core;

internal sealed partial class SolidWorksComExecutor
{
    /// <summary>
    /// 原生只读库存仅能证明未变修订的根实体。跨重建／保存的 Modified/Generated 历史需
    /// 独立的持久历史提供者；Tracking ID 生命周期不能替代该证明。此处不制造变更映射。
    /// </summary>
    private sealed class NativeUnchangedTopologySession(IReadOnlyList<GeometryCandidate> candidates,string configuration) : ISemanticTopologyHistorySession
    {
        public TopologyHistoryCapture? Capture(GeometryRef reference,GeometryDocumentIdentity currentDocument)
        {
            if(reference.Semantic is not {} semantic || !reference.ModelSha256.Equals(currentDocument.ModelSha256,StringComparison.OrdinalIgnoreCase)
                || reference.SourceRevisionId!=currentDocument.SourceRevisionId || reference.DocumentRevision!=currentDocument.DocumentRevision)
                return null;
            var roots=candidates.Where(c=>c.NativePersistentReference==reference.NativePersistentReference
                && GeometryOwnership.HasOwner(c,semantic.RootOwnerPersistentReference,reference.FeatureId)).ToArray();
            if(roots.Length!=1)return null;
            var capture=new TopologyHistoryCapture
            {
                Provider="native_readonly_inventory_unchanged_revision_only",CompleteInventory=true,SavedModelIdentityVerified=true,Digest="",
                Snapshots=[new()
                {
                    RevisionKey="unchanged:"+currentDocument.ModelSha256,Document=currentDocument,
                    Configuration=configuration,
                    Elements=candidates.Select(c=>new TopologyElement{Token=c.CandidateId==roots[0].CandidateId?semantic.RootToken:"native:"+c.NativePersistentReference,Candidate=c}).ToArray(),
                    SourceBindings=[new(){SemanticKey=semantic.SemanticKey,SourceRevisionId=reference.SourceRevisionId!,
                        SourceFactIds=reference.SourceFactIds,ExpectedSignature=reference.Signature}]
                }]
            };
            return capture with{Digest=SemanticTopologyResolver.Digest(capture)};
        }
    }
}
