using CadModeling.Core;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

internal sealed partial class SolidWorksComExecutor
{
    private static NativeBodyValidity CheckNativeBodies(IModelDoc2 model)
    {
        try
        {
            if(model is not IPartDoc part) throw new InvalidOperationException("原生实体检查需要零件文档。");
            var bodies = new[]{swBodyType_e.swSolidBody,swBodyType_e.swSheetBody}
                .SelectMany(kind => part.GetBodies2((int)kind,false) as object[] ?? []).Cast<IBody2>().ToArray();
            if(bodies.Length==0) throw new InvalidOperationException("没有可验收的实体或曲面体。");
            int count=0;
            foreach(var body in bodies)
            {
                var faults=body.Check3 ?? throw new InvalidOperationException("原生实体有效性检查未返回故障对象。");
                if(faults.Count<0) throw new InvalidOperationException("原生故障计数无效。");
                count=checked(count+faults.Count);
            }
            return new(true,bodies.Length,count);
        }
        catch(Exception ex){ return new(false,0,0,ex.Message); }
    }

    private static NativeBodyValidity RequireValidNativeBodies(IModelDoc2 model)
    {
        var validity=CheckNativeBodies(model);
        if(!validity.Passed) throw new InvalidOperationException("NATIVE_BODY_INVALID: "+(validity.Error??$"检测到 {validity.FaultCount} 项原生实体故障。"));
        return validity;
    }
}
