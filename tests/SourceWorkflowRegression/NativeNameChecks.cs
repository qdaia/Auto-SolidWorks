using CadModeling.Core;
using CadModeling.Ir;
static class NativeNameChecks
{
    public static void Run(Action<bool,string> check)
    {
        var op=new GenericOperationDraft{Id="ear",Name="耳板",Type=GenericOperationKind.ProfileSketch,DrivenProfile=new(){Kind=DrivenProfileKind.RoundEar,RadiusMm=10,WebHeightMm=30}};
        var verification=new ModelVerificationSpec{NativeDimensions=[new(){Id="radius",SourceLiteral="R10",DimensionName="轮廓半径@耳板",Value=10}]};
        var draft=new GenericModelDraft{SourceText="source",Operations=[op],Verification=verification};
        check(NativeDimensionPreflight.Validate(draft).Any(d=>d.Code=="NATIVE_DIMENSION_NAME_UNKNOWN"),"unknown recipe dimension is rejected before native execution");
        check(!NativeDimensionPreflight.Validate(draft with{Verification=new(){NativeDimensions=[verification.NativeDimensions[0] with{DimensionName="耳板宽度@耳板",Value=20}]}}).Any(d=>d.Severity==DiagnosticSeverity.Error),"actual recipe driving name remains valid");
        var cylinder=new CylinderGroupCheck{Id="outer",SourceLiteral="R10",SourceDimensionIds=["R"],DiameterMm=20,AxisStartsMm=[new(0,0,0)],Direction=new(0,0,1),Interior=false,Coverage=CylinderCoverage.TrimmedAxisAndDiameter};
        var spec=new ModelVerificationSpec{CylinderGroups=[cylinder],Bindings=[new("R","outer","radius_mm")]};
        var context=new DrawingPlanContext{SourcePath="fixture",Dimensions=[new(){Id="R",OperationId="ear",ParameterPath="driven_profile.radius_mm",SourceLiteral="R10",Value=10,Unit=DrawingValueUnit.Millimeter,Status=DrawingFactStatus.Stated}]};
        check(!ModelVerification.Validate(spec,context).Any(),"source radius binds to analytic measured cylinder diameter divided by two");
        check(ModelVerification.Validate(spec with{CylinderGroups=[cylinder with{DiameterMm=22}]},context).Any(),"changed diameter cannot silently preserve stale source radius acceptance");
        var datum=op with{DrivenProfile=null,RequireEditable=true,Primitives=[new(){Type=GenericPrimitiveKind.OpenCurve,Construction=true}]};
        check(NativeDimensionPreflight.Validate(draft with{Operations=[datum],DrawingContext=context}).Any(d=>d.Code=="SOURCE_DIMENSION_UNCONSUMED_DATUM"),"unconsumed construction-only source dimension cannot substitute for part measurement");
    }
}
