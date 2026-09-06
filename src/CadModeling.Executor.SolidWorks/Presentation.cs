using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

internal sealed partial class SolidWorksComExecutor
{
    private static void PrepareModelPresentation(IModelDoc2 model)
    {
        model.ClearSelection2(true);
        foreach (var toggle in new[] { swUserPreferenceToggle_e.swDisplayAxes,
                     swUserPreferenceToggle_e.swDisplayTemporaryAxes, swUserPreferenceToggle_e.swDisplayPlanes,
                     swUserPreferenceToggle_e.swDisplaySketches, swUserPreferenceToggle_e.swDisplayOrigins,
                     swUserPreferenceToggle_e.swDisplayCoordSystems })
            model.SetUserPreferenceToggle((int)toggle, false);
        model.ShowNamedView2("", (int)swStandardViews_e.swIsometricView);
        model.ViewZoomtofit2();
        model.GraphicsRedraw2();
    }
}
