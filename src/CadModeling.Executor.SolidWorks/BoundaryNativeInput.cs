using System.Runtime.InteropServices;
using System.Text;
using CadModeling.Core;
using CadModeling.Ir;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

internal sealed partial class SolidWorksComExecutor
{
    private static bool BoundaryNeedsNormal(BoundaryDefinitionRequest request)=>
        request.Start==SurfaceEndCondition.NormalToProfile||request.End==SurfaceEndCondition.NormalToProfile;

    private static IFeature[] RequireBoundaryNormalSources(IModelDoc2 model,BoundaryDefinitionRequest request)
    {
        if(request.Profiles.Count!=2||request.Guides.Count!=0)
            throw new InvalidOperationException("BOUNDARY_EDITOR_SCOPE_UNSUPPORTED：当前法向原生编辑路径需要两条平面单样条轮廓且没有第二方向曲线。");
        return request.Profiles.Select(p=>
        {
            int state=0;var f=model.Extension.GetObjectByPersistReference3(Convert.FromBase64String(p.FeatureReference),out state) as IFeature;
            if(state!=0||f?.GetSpecificFeature2() is not ISketch sketch||sketch.Is3D())
                throw new InvalidOperationException("BOUNDARY_EDITOR_SCOPE_UNSUPPORTED：法向轮廓必须是已绑定的原生平面草图。");
            var all=sketch.GetSketchSegments() as object[]??[];
            var segments=all.OfType<ISketchSegment>().Where(s=>!s.ConstructionGeometry).ToArray();
            if(all.Length>4096||all.Any(s=>s is not ISketchSegment)||segments.Length!=1
                ||segments[0].GetType()!=(int)swSketchSegments_e.swSketchSPLINE)
                throw new InvalidOperationException("BOUNDARY_EDITOR_SCOPE_UNSUPPORTED：法向路径只认证完整单样条草图，不能替换为单段选择。");
            return f;
        }).ToArray();
    }

    private static void ApplyBoundaryNormalControls(IModelDoc2 model,IFeature feature,BoundaryDefinitionRequest request)
    {
        var profiles=RequireBoundaryNormalSources(model,request);var app=ExistingBoundaryApplication();
        var reference=RequireDefinitionIdentity(model,feature);var name=feature.Name;bool started=false;
        try
        {
            started=true;StartBoundaryEditor(app,model,feature);
            var view=new BoundaryEditorInput(app,model,name);
            view.SetProfiles(profiles.Select(p=>p.Name).ToArray(),
                [BoundarySurfaceContract.ExpectedTangency(request,0,0,2),BoundarySurfaceContract.ExpectedTangency(request,0,1,2)]);
            FinishBoundaryEditor(app,model,name,true);started=false;
            if(!SamePersistentIdentity(model,reference,RequireDefinitionIdentity(model,feature))||feature.IsSuppressed()
                ||feature.GetErrorCode2(out _)!=0)
                throw new InvalidOperationException("BOUNDARY_EDITOR_IDENTITY：确认编辑后特征身份或有效性改变。");
            var actual=ReadBoundaryDefinition(model,feature);
            BoundarySurfaceContract.VerifyDeclared(actual,request,(a,b)=>SamePersistentIdentity(model,a,b));
            RequireBoundaryNormalGeometry(model,feature,request);
        }
        finally
        {
            if(started&&app.IActiveDoc2 is {} active&&app.IsSame(active,model)==1)
            {
                app.GetRunningCommandInfo(out var command,out var currentName,out var editing);
                if(editing&&command==BoundaryEditCommand&&currentName==name)FinishBoundaryEditor(app,model,name,false);
                else if(editing)throw new InvalidOperationException("BOUNDARY_EDITOR_PENDING：命令归属改变，不能取消或重放。");
            }
        }
    }

    private sealed class BoundaryEditorInput
    {
        private readonly ISldWorks app;
        private readonly IModelDoc2 model;
        private readonly string featureName;
        private readonly uint pid;
        private readonly IntPtr root,list,combo;
        public BoundaryEditorInput(ISldWorks application,IModelDoc2 document,string name)
        {
            app=application;model=document;featureName=name;pid=checked((uint)app.GetProcessID());
            var candidates=new List<IntPtr>();
            Native.EnumWindows((h,_)=>{Native.GetWindowThreadProcessId(h,out var p);
                if(p==pid&&Native.IsWindowVisible(h)&&Text(h).Contains(model.GetTitle(),StringComparison.Ordinal))candidates.Add(h);
                return true;},IntPtr.Zero);
            candidates=candidates.OrderByDescending(Area).ToList();
            if(candidates.Count==0||candidates.Count>1&&Area(candidates[0])==Area(candidates[1]))
                throw new InvalidOperationException("BOUNDARY_EDITOR_WINDOW：自有活动文档窗口缺失或歧义。");
            root=candidates[0];var controls=new List<IntPtr>();
            Native.EnumChildWindows(root,(h,_)=>{if(Native.IsWindowVisible(h))controls.Add(h);return true;},IntPtr.Zero);
            IntPtr Unique(int id,string kind)
            {
                var found=controls.Where(h=>Native.GetDlgCtrlID(h)==id&&Class(h)==kind).ToArray();
                return found.Length==1?found[0]:throw new InvalidOperationException("BOUNDARY_EDITOR_WINDOW：原生属性控件缺失或歧义。");
            }
            list=Unique(2556,"ListBox");combo=Unique(8938,"ComboBox");
            if(Send(list,0x18b)!=2||Send(combo,0x146)!=3
                ||!Enumerable.Range(0,3).Select(i=>Send(combo,0x150,i)).SequenceEqual(new long[]{0,2,1}))
                throw new InvalidOperationException("BOUNDARY_EDITOR_CONTROL：原生轮廓库存或端条件枚举数据不符。");
        }
        private static long Area(IntPtr h)
        {if(!Native.GetWindowRect(h,out var r))return -1;return (long)(r.Right-r.Left)*(r.Bottom-r.Top);}
        private static string Text(IntPtr h)
        {var b=new StringBuilder(512);Native.GetWindowTextW(h,b,b.Capacity);return b.ToString();}
        private static string Class(IntPtr h)
        {var b=new StringBuilder(128);Native.GetClassNameW(h,b,b.Capacity);return b.ToString();}
        private void Verify(IntPtr h)
        {
            RequireBoundaryEditorActive(app,model,featureName);
            Native.GetWindowThreadProcessId(root,out var rp);Native.GetWindowThreadProcessId(h,out var hp);
            if(rp!=pid||hp!=pid||!Native.IsWindow(h)||!Native.IsWindowVisible(h)||!Native.IsWindowEnabled(h)
                ||!Text(root).Contains(model.GetTitle(),StringComparison.Ordinal))
                throw new InvalidOperationException("BOUNDARY_EDITOR_WINDOW：控件归属、文档标题或可用状态改变。");
        }
        private long Send(IntPtr h,uint message,long wp=0,IntPtr lp=default)
        {
            Verify(h);
            if(Native.SendMessageTimeoutW(h,message,new UIntPtr(unchecked((ulong)wp)),lp,2,2000,out var result)==IntPtr.Zero)
                throw new InvalidOperationException("BOUNDARY_EDITOR_PENDING：自有控件消息未返回，不重放输入。");
            return unchecked((long)result.ToUInt64());
        }
        private string Item(IntPtr h,uint message,int index)
        {
            var buffer=Marshal.AllocHGlobal(1024);
            try{var length=Send(h,message,index,buffer);if(length is <0 or >=512)
                throw new InvalidOperationException("BOUNDARY_EDITOR_CONTROL：轮廓文本缺失或超限。");
                return Marshal.PtrToStringUni(buffer)??"";}
            finally{Marshal.FreeHGlobal(buffer);}
        }
        private void ClickItem(IntPtr h,int index)
        {
            var memory=Marshal.AllocHGlobal(Marshal.SizeOf<Native.Rect>());
            try
            {
                if(Send(h,0x198,index,memory)==-1)throw new InvalidOperationException("BOUNDARY_EDITOR_CONTROL：轮廓项目矩形不可访问。");
                var r=Marshal.PtrToStructure<Native.Rect>(memory);
                if(r.Right<=r.Left||r.Bottom<=r.Top||!Native.GetClientRect(h,out var bounds)||r.Top<0||r.Bottom>bounds.Bottom)
                    throw new InvalidOperationException("BOUNDARY_EDITOR_CONTROL：输入项目不在自有可见控件中。");
                var position=new IntPtr(((r.Top+r.Bottom)/2<<16)|((r.Left+r.Right)/2&0xffff));
                Send(h,0x201,1,position);Send(h,0x202,0,position);
            }
            finally{Marshal.FreeHGlobal(memory);}
        }
        public void SetProfiles(string[] names,int[] values)
        {
            if(names.Length!=2||values.Length!=2||names.Distinct(StringComparer.Ordinal).Count()!=2)
                throw new InvalidOperationException("BOUNDARY_EDITOR_IDENTITY：两个轮廓名称必须唯一。");
            for(int index=0;index<2;index++)
            {
                ClickItem(list,index);Thread.Sleep(100);
                var caption=Item(list,0x189,index);
                var selectedIndex=Send(list,0x188);
                if(selectedIndex!=index||(caption!=names[index]&&caption!=names[index]+"-法向"))
                    throw new InvalidOperationException($"BOUNDARY_EDITOR_IDENTITY：所选原生轮廓顺序与绑定来源不符（期望序号={index}，实际序号={selectedIndex}，期望来源={names[index]}，实际文本={caption}）。");
                int item=Array.FindIndex(new[]{0,2,1},value=>value==values[index]);
                if(item<0)throw new InvalidOperationException("BOUNDARY_EDITOR_CONTROL：未知端条件。");
                if(Send(combo,0x147)!=item)
                {
                    var info=new Native.ComboInfo{Size=Marshal.SizeOf<Native.ComboInfo>()};
                    Verify(combo);
                    if(!Native.GetComboBoxInfo(combo,ref info))throw new InvalidOperationException("BOUNDARY_EDITOR_CONTROL：下拉列表不可访问。");
                    Send(combo,0x14f,1);ClickItem(info.List,item);Thread.Sleep(100);
                }
                if(Send(combo,0x147)!=item||Send(combo,0x150,item)!=values[index])
                    throw new InvalidOperationException("BOUNDARY_EDITOR_CONTROL：实际端条件没有变为声明枚举。");
            }
        }
        private static class Native
        {
            internal delegate bool Callback(IntPtr h,IntPtr parameter);
            [StructLayout(LayoutKind.Sequential)] internal struct Rect{internal int Left,Top,Right,Bottom;}
            [StructLayout(LayoutKind.Sequential)] internal struct ComboInfo
            {internal int Size;internal Rect Item,Button;internal uint ButtonState;internal IntPtr Combo,Edit,List;}
            [DllImport("user32.dll")] internal static extern bool EnumWindows(Callback callback,IntPtr parameter);
            [DllImport("user32.dll")] internal static extern bool EnumChildWindows(IntPtr parent,Callback callback,IntPtr parameter);
            [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr h,out uint pid);
            [DllImport("user32.dll")] internal static extern bool IsWindow(IntPtr h);
            [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr h);
            [DllImport("user32.dll")] internal static extern bool IsWindowEnabled(IntPtr h);
            [DllImport("user32.dll")] internal static extern bool GetWindowRect(IntPtr h,out Rect rect);
            [DllImport("user32.dll")] internal static extern bool GetClientRect(IntPtr h,out Rect rect);
            [DllImport("user32.dll")] internal static extern int GetDlgCtrlID(IntPtr h);
            [DllImport("user32.dll",CharSet=CharSet.Unicode)] internal static extern int GetWindowTextW(IntPtr h,StringBuilder text,int count);
            [DllImport("user32.dll",CharSet=CharSet.Unicode)] internal static extern int GetClassNameW(IntPtr h,StringBuilder text,int count);
            [DllImport("user32.dll")] internal static extern bool GetComboBoxInfo(IntPtr h,ref ComboInfo info);
            [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)] internal static extern IntPtr SendMessageTimeoutW(IntPtr h,uint message,UIntPtr wp,IntPtr lp,uint flags,uint timeout,out UIntPtr result);
        }
    }
}
