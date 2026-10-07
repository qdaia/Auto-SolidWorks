using CadModeling.Core;
using CadModeling.Ir;

internal static class ConcentricTreeChecks
{
    internal static void Run(List<string> checks)
    {
        void Pass(string label, Action test) { test(); checks.Add(label); Console.WriteLine("PASS: " + label); }
        void Reject(string label, Action test)
        {
            try { test(); }
            catch(ArgumentException e) when(e.Message.Contains("ASSEMBLY_MOBILITY",StringComparison.Ordinal))
            { checks.Add(label); Console.WriteLine("PASS: rejected " + label); return; }
            throw new Exception("非法开链观测被接受：" + label);
        }
        void Check(bool ok) { if(!ok) throw new Exception("开链运动基断言失败。"); }
        NativeCylindricalJoint Joint(string a,string b,Vector3? p=null,Vector3? axis=null,bool locked=false,bool stop=false)
        {
            p??=new(0,0,0);axis??=new(0,0,1);
            return new(){FirstComponentId=a,SecondComponentId=b,ConcentricMateIdentity="mate:"+a+":"+b,
                FirstAxis=new(p,axis,10),SecondAxis=new(p,axis,20),LockRotation=locked,NativeDefinitionsVerified=true,
                AxialMateIdentity=stop?"stop:"+a+":"+b:null,
                AxialCoincidence=stop?new(p,p,axis,axis):null};
        }
        NativeConcentricTreeReadback Tree(params NativeCylindricalJoint[] joints)=>new(){CompleteMateInventory=true,AllResolvedTopLevelParts=true,
            ComponentIds=joints.SelectMany(j=>new[]{j.FirstComponentId,j.SecondComponentId}).Distinct(StringComparer.Ordinal).ToArray(),
            FixedComponentIds=["a"],ActiveMateCount=joints.Sum(j=>j.AxialCoincidence is null?1:2),Joints=joints};
        IReadOnlyList<AssemblyMotion> Basis(NativeConcentricTreeReadback r,string moving="b",string reference="a")=>ConcentricTreeMobilityContract.Basis(r,moving,reference);
        var single=Tree(Joint("a","b"));
        Pass("开链同心对两个运动",()=>Check(Basis(single).Count==2));
        Pass("开链轴向端面保留旋转",()=>Check(Basis(Tree(Joint("a","b",stop:true))).Single().Kind==AssemblyMotionKind.Rotation));
        Pass("开链锁定同心保留平移",()=>Check(Basis(Tree(Joint("a","b",locked:true))).Single().Kind==AssemblyMotionKind.Translation));
        Pass("开链旋转锁定及轴向端面为零运动",()=>Check(Basis(Tree(Joint("a","b",locked:true,stop:true))).Count==0));
        Pass("反向圆柱及反向端面法向等价",()=>Check(Basis(Tree(Joint("a","b",stop:true) with
            {SecondAxis=new(new(0,0,100),new(0,0,-2),15),AxialCoincidence=new(new(10,20,0),new(-10,-20,0),new(0,0,2),new(0,0,-3))})).Count==1));
        var coaxial=Tree(Joint("a","b"),Joint("b","c"));
        Pass("同轴串联关节的相对秩不是配合数量",()=>Check(Basis(coaxial,"c").Count==2));
        var offset=Tree(Joint("a","b"),Joint("b","c",new(20,10,0)));
        Pass("不同位置平行串联圆柱轴的相对运动秩为三",()=>Check(Basis(offset,"c").Count==3));
        Pass("移动参考组件取消共同上游关节",()=>Check(Basis(offset,"c","b").Count==2));
        Pass("交换相对端点保持相同运动子空间",()=>Check(Basis(offset,"b","c").Count==2));
        Pass("分支组件相对路径包含两个支路",()=>Check(Basis(Tree(Joint("a","b",axis:new(1,0,0)),Joint("a","c",axis:new(0,1,0))),"b","c").Count==4));
        Pass("三轴开链端点最多六维相对运动",()=>Check(Basis(Tree(Joint("a","b",axis:new(1,0,0)),Joint("b","c",axis:new(0,1,0)),Joint("c","d")),"d").Count==6));
        Pass("上游刚性关节不增加相对运动",()=>Check(Basis(Tree(Joint("a","b",locked:true,stop:true),Joint("b","c")),"c").Count==2));
        Pass("非共线平面三转轴具有完整三维端点运动",()=>Check(Basis(Tree(Joint("a","b",stop:true),Joint("b","c",new(20,0,0),stop:true),Joint("c","d",new(20,20,0),stop:true)),"d").Count==3));
        Reject("共线平面三转轴的端点奇异投影不能当作两自由度",()=>Basis(Tree(Joint("a","b",stop:true),Joint("b","c",new(20,0,0),stop:true),Joint("c","d",new(40,0,0),stop:true)),"d"));
        Reject("未证明结构性秩的相关多关节端点运动",()=>Basis(Tree(Joint("a","b",stop:true),Joint("b","c",new(20,0,0),stop:true),Joint("c","d",new(20,20,0),stop:true),Joint("d","e",new(0,20,0),stop:true)),"e"));
        var remote=Tree(Joint("a","b",new(100000,500000,700000),stop:true),Joint("b","c",new(100020,500010,700000),stop:true));
        Pass("远离原点的开链仍核对实际轴线位置",()=>Check(Basis(remote,"c").Count==2));
        var max=Tree(Enumerable.Range(0,31).Select(i=>Joint(i==0?"a":"n"+i,"n"+(i+1))).ToArray());
        Pass("完整三十二组件开链库存",()=>Check(Basis(max,"n31").Count==2));
        Reject("三十三组件超过有界库存",()=>Basis(Tree(Enumerable.Range(0,32).Select(i=>Joint(i==0?"a":"n"+i,"n"+(i+1))).ToArray()),"n32"));
        foreach(var bad in new[]{single with{CompleteMateInventory=false},single with{AllResolvedTopLevelParts=false},
            single with{FixedComponentIds=[]},single with{FixedComponentIds=["a","b"]},single with{FixedComponentIds=["unknown"]},
            single with{ActiveMateCount=0},single with{ActiveMateCount=2},single with{ComponentIds=["a","b","b"]},single with{Joints=[]},
            single with{Joints=null!},single with{ComponentIds=null!},single with{FixedComponentIds=null!}})
            Reject("开链完整库存负例 "+checks.Count,()=>Basis(bad));
        Reject("未知相对端点",()=>Basis(single,"unknown"));
        Reject("相对自己无有效端点",()=>Basis(single,"a"));
        foreach(var bad in new[]{Joint("a","b") with{NativeDefinitionsVerified=false},Joint("a","b") with{ConcentricMateIdentity=""},
            Joint("a","b") with{FirstAxis=null!},Joint("a","b") with{FirstAxis=new(new(0,0,0),new(0,0,0),10)},
            Joint("a","b") with{FirstAxis=new(new(double.NaN,0,0),new(0,0,1),10)},
            Joint("a","b") with{FirstAxis=new(new(0,0,0),new(0,0,1),0)},
            Joint("a","b") with{SecondAxis=new(new(1,0,0),new(0,0,1),20)},
            Joint("a","b") with{SecondAxis=new(new(0,0,0),new(0,1,0),20)},
            Joint("a","b") with{FirstAxis=new(new(0,0,0),new(double.MaxValue,double.MaxValue,0),10)},
            Joint("a","b",stop:true) with{AxialMateIdentity=null},Joint("a","b",stop:true) with{AxialCoincidence=null},
            Joint("a","b",stop:true) with{AxialMateIdentity="mate:a:b"},
            Joint("a","b",stop:true) with{AxialCoincidence=new(new(0,0,0),new(0,0,1),new(0,0,1),new(0,0,1))},
            Joint("a","b",stop:true) with{AxialCoincidence=new(new(0,0,0),new(0,0,0),new(1,0,0),new(1,0,0))}})
            Reject("开链原生关节与实际几何负例 "+checks.Count,()=>Basis(Tree(bad)));
        Reject("重复组件对不能隐藏多配合",()=>Basis(Tree(Joint("a","b"),Joint("b","a"))));
        Reject("重用原生配合身份",()=>Basis(Tree(Joint("a","b"),Joint("b","c") with{ConcentricMateIdentity="mate:a:b"}),"c"));
        Reject("额外闭环配合不能按开链验收",()=>Basis(Tree(Joint("a","b"),Joint("b","c"),Joint("c","a"))));
        Reject("断开组件和另一分支的闭环不能相互抵消库存数",()=>Basis(new NativeConcentricTreeReadback
            {CompleteMateInventory=true,AllResolvedTopLevelParts=true,ComponentIds=["a","b","c","d"],FixedComponentIds=["a"],ActiveMateCount=3,
                Joints=[Joint("a","b"),Joint("b","c"),Joint("c","a")]}));
        Reject("近相关运动秩不能自动凑自由度",()=>Basis(Tree(Joint("a","b",stop:true),Joint("b","c",new(.000001,0,0),stop:true)),"c"));
        var observed=Basis(offset,"c");
        var plan=new AssemblyPlan{Name="开链合同",NativePath="uncreated.SLDASM",Components=[new(){Id="a",Path="never-opened",Fixed=true},new(){Id="b",Path="never-opened"},new(){Id="c",Path="never-opened"}],
            Mobility=new(){Requirements=[new(){ComponentId="c",RelativeToComponentId="a",SourceLiteral="独立设计意图：两个错位的平行圆柱关节",ExpectedBasis=observed}]}};
        var session=new TreeSession(offset,observed);
        Pass("开链证据经过公开运动合同重新计算并保留",()=>Check(AssemblyMobilityContract.Verify(plan,session).Single().ConcentricTreeReadback==offset));
        Pass("开链证据保存重开运动基不变",()=>AssemblyMobilityContract.VerifySaved(plan,new TreeSession(offset,observed),AssemblyMobilityContract.Verify(plan,session)));
        Reject("开链证据来源不能冒充旧适配器",()=>AssemblyMobilityContract.Verify(plan,new TreeSession(offset,observed){Source="offline"}));
        Reject("原生库存遗漏组件不能保持完整状态",()=>AssemblyMobilityContract.Verify(plan,new TreeSession(single,observed)));
        Reject("声明运动基与完整关节重新计算不符",()=>AssemblyMobilityContract.Verify(plan,new TreeSession(offset,[observed[0]])));
        Reject("伪造完整原生关节库存被重新校验",()=>AssemblyMobilityContract.Verify(plan,new TreeSession(offset with{ActiveMateCount=9},observed)));
    }

    private sealed class TreeSession(NativeConcentricTreeReadback tree,IReadOnlyList<AssemblyMotion> basis):IAssemblyMobilitySession
    {
        public string Source { get; init; }="native_complete_grounded_concentric_tree_relative_spatial_basis";
        public string Configuration=>"离线配置";
        public IReadOnlyDictionary<string,string> ComponentIdentities { get; }=new Dictionary<string,string>{{"a","instance:a"},{"b","instance:b"},{"c","instance:c"}};
        public AssemblyMobilityObservation Inspect(string componentId,string relativeToComponentId)=>new(){ComponentId=componentId,RelativeToComponentId=relativeToComponentId,
            ComponentIdentity=ComponentIdentities[componentId],ReferenceIdentity=ComponentIdentities[relativeToComponentId],Configuration=Configuration,EvidenceSource=Source,
            State=AssemblyMobilityEvidenceState.Complete,InAssemblyCoordinates=true,RegularConfiguration=true,DegreesOfFreedom=basis.Count,Basis=basis,ConcentricTreeReadback=tree};
    }
}
