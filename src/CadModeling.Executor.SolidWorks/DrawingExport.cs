using System.Security.Cryptography;
using CadModeling.Core;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using Environment = System.Environment;

internal sealed partial class SolidWorksComExecutor
{
    public async Task<DrawingExportResult> ExportDrawingAsync(DrawingExportRequest request,CancellationToken cancellationToken=default)
    {
        await _serialGate.WaitAsync(cancellationToken);
        try { return await _sta.InvokeAsync(()=>ExportDrawingOnSta(request),cancellationToken); }
        finally { _serialGate.Release(); }
    }

    private static DrawingExportResult ExportDrawingOnSta(DrawingExportRequest request)
    {
        SldWorks? app=null; IModelDoc2? source=null; IModelDoc2? document=null;
        bool ownedSource=false; string? previousTitle=null;string stage="validation";
        try
        {
            foreach(var path in new[]{request.InputPath,request.NativePath,request.PdfPath})
                if(!Path.IsPathFullyQualified(path)) throw new ArgumentException("Drawing paths must be absolute.");
            if(!File.Exists(request.InputPath)||!Path.GetExtension(request.InputPath).Equals(".sldprt",StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Drawing export requires an existing native SLDPRT.");
            if(!Path.GetExtension(request.NativePath).Equals(".slddrw",StringComparison.OrdinalIgnoreCase)||
               !Path.GetExtension(request.PdfPath).Equals(".pdf",StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Outputs must use .SLDDRW and .PDF extensions.");
            if(File.Exists(request.NativePath)||File.Exists(request.PdfPath)) throw new IOException("Drawing export does not overwrite existing outputs.");
            var sourceHash=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(request.InputPath)));
            app=(SldWorks)Activator.CreateInstance(Type.GetTypeFromProgID("SldWorks.Application",true)!)!;
            previousTitle=(app.IActiveDoc2)?.GetTitle();
            source=(IModelDoc2?)app.GetOpenDocumentByName(request.InputPath);
            int errors=0,warnings=0;
            if(source is null)
            {
                source=(IModelDoc2?)app.OpenDoc6(request.InputPath,(int)swDocumentTypes_e.swDocPART,
                    (int)(swOpenDocOptions_e.swOpenDocOptions_Silent|swOpenDocOptions_e.swOpenDocOptions_ReadOnly),"",ref errors,ref warnings);
                ownedSource=source is not null&&Path.GetFullPath(source.GetPathName()).Equals(Path.GetFullPath(request.InputPath),StringComparison.OrdinalIgnoreCase);
            }
            if(source is null||!Path.GetFullPath(source.GetPathName()).Equals(Path.GetFullPath(request.InputPath),StringComparison.OrdinalIgnoreCase))
                throw new IOException($"Could not open the exact source part (errors={errors}). Use a unique basename.");
            if(source.GetSaveFlag()) throw new InvalidOperationException("Source has unsaved changes; export requires a saved reference state.");
            stage="inspection";
            var inspection=InspectOnSta(new(request.InputPath),app);
            if(!inspection.Success) throw new IOException(inspection.Message);
            var dimensions=inspection.Features!.SelectMany(f=>f.Dimensions).ToArray();
            var template=request.TemplatePath??FindDrawingTemplate(app);
            if(!Path.IsPathFullyQualified(template)||!File.Exists(template)||!Path.GetExtension(template).Equals(".drwdot",StringComparison.OrdinalIgnoreCase))
                throw new IOException("An existing absolute .drwdot template is required.");
            var modelViewNames=(string[])source.GetModelViewNames();
            stage="create drawing";
            document=(IModelDoc2?)app.NewDocument(template,(int)swDwgPaperSizes_e.swDwgPaperA3size,0.420,0.297)
                ??throw new IOException("Could not create drawing from template.");
            var drawing=(IDrawingDoc)document;
            foreach(var toggle in new[]{swUserPreferenceToggle_e.swDisplayAxes,swUserPreferenceToggle_e.swDisplayTemporaryAxes,swUserPreferenceToggle_e.swDisplayPlanes})
                document.Extension.SetUserPreferenceToggle((int)toggle,(int)swUserPreferenceOption_e.swDetailingNoOptionSpecified,false);
            var sheet=(ISheet)drawing.GetCurrentSheet(); sheet.SetName("Views");
            if(!drawing.SetupSheet6("Views",(int)swDwgPaperSizes_e.swDwgPaperA3size,(int)swDwgTemplates_e.swDwgTemplateNone,
                1,1,true,"",0.420,0.297,"",true,0,0,0,0,0,0)) throw new IOException("First-angle A3 setup failed.");
            var specs=new[]{("Front","*Front",.115,.195),("Top","*Top",.115,.075),("Left","*Left",.300,.195),("Isometric","*Isometric",.300,.075)};
            var views=new List<(IView View,string Name,string Orientation,double X,double Y)>();
            foreach(var (name,orientation,x,y) in specs)
            {
                stage="create view";
            var chinese=name switch {"Front"=>"*前视","Top"=>"*上视","Left"=>"*左视",_=>"*等轴测"};
                var nativeOrientation=modelViewNames.FirstOrDefault(n=>n.Equals(orientation,StringComparison.OrdinalIgnoreCase)||n.Equals(chinese,StringComparison.Ordinal))
                    ??throw new IOException($"Standard {name} view not found. Available: {string.Join(", ",modelViewNames)}");
                var view=(IView?)drawing.CreateDrawViewFromModelView3(request.InputPath,nativeOrientation,x,y,0)
                    ??throw new IOException($"Could not create {name} view ({nativeOrientation}).");
                view.SetName2(name);view.UseSheetScale=0;view.ScaleDecimal=1;
                view.SetDisplayMode3(false,(int)(name=="Isometric"?swDisplayMode_e.swHIDDEN:swDisplayMode_e.swHIDDEN_GREYED),false,true);
                views.Add((view,name,orientation,x,y));
            }
            document.ForceRebuild3(false);
            var fit=views.Min(v=> {var o=(double[])v.View.GetOutline();return v.View.ScaleDecimal*Math.Min(.150/Math.Max(o[2]-o[0],.001),.095/Math.Max(o[3]-o[1],.001));});
            var scales=new[]{.05,.1,.125,.2,.25,.5,.75,1,1.25,1.5,2,4,5,10};
            var scale=scales.Where(s=>s<=fit).DefaultIfEmpty(fit*.9).Max();
            foreach(var v in views) {v.View.ScaleDecimal=scale;v.View.Position=new double[]{v.X,v.Y,0};}
            document.ClearSelection2(true);
            drawing.ActivateView("Front");
            stage="import annotations";
            drawing.InsertModelAnnotations3((int)swImportModelItemsSource_e.swImportModelItemsFromEntireModel,
                (int)(swInsertAnnotation_e.swInsertDimensionsMarkedForDrawing|swInsertAnnotation_e.swInsertDimensionsNotMarkedForDrawing),true,true,true,false);
            document.ForceRebuild3(false);
            stage="enumerate drawing dimensions";
            var imported=new HashSet<string>(StringComparer.Ordinal);
            foreach(var v in views)
            {
                document.ClearSelection2(true);
                var visited=new HashSet<string>(); int count=0;
                for(var display=v.View.GetFirstDisplayDimension5();display is not null;display=display.GetNext5())
                {
                    if(++count>5000) throw new InvalidOperationException("Drawing dimension enumeration exceeded limit.");
                    var dim=(IDimension?)display.GetDimension2(0);
                    if(dim is null) continue;
                    var name=string.Join('@',dim.FullName.Split('@').Take(2));
                    if(!visited.Add(name)) throw new InvalidOperationException("Drawing dimension enumeration repeated an entry.");
                    imported.Add(name);
                    var annotation=(IAnnotation)display.GetAnnotation();
                    var format=(ITextFormat)annotation.GetTextFormat(0);format.TypeFaceName="Arial";format.CharHeight=.0032;format.WidthFactor=1;
                    annotation.SetTextFormat(0,false,format);
                    annotation.Select3(true,null);
                }
                if(count>1) document.Extension.AlignDimensions((int)swAlignDimensionType_e.swAlignDimensionType_AutoArrange,.008);
            }
            stage="view labels";
            drawing.ActivateView("");
            AddDrawingNote(document,$"{Path.GetFileNameWithoutExtension(request.InputPath)}  |  MODEL-DERIVED REFERENCE",.020,.280,14);
            AddDrawingNote(document,$"FIRST ANGLE  /  mm  /  scale {scale:0.###}:1  /  {inspection.Geometry?.SolidBodyCount} solid bodies",.020,.269,10);
            foreach(var v in views) AddDrawingNote(document,v.Name.ToUpperInvariant(),v.X-.070,v.Y+.050,10);
            AddDrawingNote(document,"Native model dimensions imported where available. See parameter schedule for coverage gaps.",.020,.018,9);
            var viewResults=views.Select(v=>new ExportedDrawingView(v.Name,v.Orientation,scale,(double[])v.View.GetOutline())).ToArray();
            stage="schedule sheet";
            if(!drawing.NewSheet4("Parameters",(int)swDwgPaperSizes_e.swDwgPaperA3size,(int)swDwgTemplates_e.swDwgTemplateNone,
                1,1,true,"",.420,.297,"",0,0,0,0,0,0)) throw new IOException("Could not create parameter schedule.");
            AddDrawingNote(document,"REFERENCE PARAMETER SCHEDULE",.020,.280,14);
            AddDrawingNote(document,"Values read from the saved part. mm / deg / count are distinct. These are not additional drawing constraints.",.020,.267,10);
            for(int i=0;i<dimensions.Length;i++)
            {
                var d=dimensions[i];var col=i/22;var row=i%22;
                if(col>1) throw new InvalidOperationException("Parameter schedule currently supports up to 44 native parameters.");
                var unit=d.Unit switch {"Millimeter"=>"mm","Degree"=>"deg","Unitless"=>"count",_=>"unknown"};
                AddDrawingNote(document,$"{i+1:00}  {d.Name} = {d.Value:0.####} {unit}  [{(imported.Contains(d.Name)?"view":"schedule only")}]",
                    .020+col*.200,.245-row*.009,10);
            }
            var missing=dimensions.Where(d=>!imported.Contains(d.Name)).Select(d=>d.Name).ToArray();
            AddDrawingNote(document,$"Unique source parameters: {dimensions.Length}   |   Placed in views: {dimensions.Count(d=>imported.Contains(d.Name))}   |   Schedule only: {missing.Length}",.020,.035,10);
            AddDrawingNote(document,"Generated from reference model; not an independent source drawing. No manufacturing completeness certification.",.020,.020,9);
            drawing.ActivateSheet("Views");document.ForceRebuild3(false);
            Directory.CreateDirectory(Path.GetDirectoryName(request.NativePath)!);Directory.CreateDirectory(Path.GetDirectoryName(request.PdfPath)!);
            stage="native save";
            if(!document.Extension.SaveAs(request.NativePath,(int)swSaveAsVersion_e.swSaveAsCurrentVersion,(int)swSaveAsOptions_e.swSaveAsOptions_Silent,null,ref errors,ref warnings)||errors!=0)
                throw new IOException($"Native drawing save failed (errors={errors}, warnings={warnings}).");
            stage="pdf save";
            var pdf=(IExportPdfData)app.GetExportFileData((int)swExportDataFileType_e.swExportPdfData);
            pdf.ViewPdfAfterSaving=false;
            if(!pdf.SetSheets((int)swExportDataSheetsToExport_e.swExportData_ExportSpecifiedSheets,new string[]{"Views","Parameters"})) throw new IOException("Could not select PDF sheets.");
            errors=0;warnings=0;
            if(!document.Extension.SaveAs(request.PdfPath,(int)swSaveAsVersion_e.swSaveAsCurrentVersion,(int)swSaveAsOptions_e.swSaveAsOptions_Silent,pdf,ref errors,ref warnings)||errors!=0)
                throw new IOException($"PDF export failed (errors={errors}, warnings={warnings}).");
            if(!File.Exists(request.NativePath)||new FileInfo(request.NativePath).Length==0||!File.Exists(request.PdfPath)||new FileInfo(request.PdfPath).Length==0)
                throw new IOException("Drawing output verification failed.");
            if(sourceHash!=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(request.InputPath)))) throw new IOException("Source hash changed during drawing export.");
            stage="saved drawing readback";
            app.CloseDoc(document.GetTitle());document=null;
            errors=0;warnings=0;
            document=(IModelDoc2?)app.OpenDoc6(request.NativePath,(int)swDocumentTypes_e.swDocDRAWING,
                (int)(swOpenDocOptions_e.swOpenDocOptions_Silent|swOpenDocOptions_e.swOpenDocOptions_ReadOnly),"",ref errors,ref warnings)
                ??throw new IOException($"Saved drawing could not reopen (errors={errors}).");
            drawing=(IDrawingDoc)document;
            if(!((string[])drawing.GetSheetNames()).SequenceEqual(new[]{"Views","Parameters"})) throw new IOException("Saved drawing sheet inventory changed.");
            drawing.ActivateSheet("Views");
            var savedProperties=(double[])((ISheet)drawing.GetCurrentSheet()).GetProperties2();
            if(savedProperties[4]==0||Math.Abs(savedProperties[5]-.420)>.00001||Math.Abs(savedProperties[6]-.297)>.00001)
                throw new IOException("Saved drawing projection or sheet dimensions changed.");
            var savedViews=new List<string>();
            for(var view=((IView)drawing.GetFirstView()).GetNextView() as IView;view is not null;view=view.GetNextView() as IView)
            {
                if(savedViews.Count>=4) throw new IOException("Unexpected additional saved drawing view.");
                if(!Path.GetFullPath(view.GetReferencedModelName()).Equals(Path.GetFullPath(request.InputPath),StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Saved drawing references a different model.");
                savedViews.Add(view.GetName2());
            }
            if(!savedViews.Order().SequenceEqual(views.Select(v=>v.Name).Order())) throw new IOException("Saved drawing view inventory changed.");
            return new(true,"Exported first-angle views and native parameter schedule; source file preserved.")
            { NativePath=request.NativePath,PdfPath=request.PdfPath,SourceSha256=sourceHash,Views=viewResults,
              ImportedDimensionNames=imported.Order().ToArray(),SourceDimensions=dimensions,UnplacedDimensionNames=missing,Reopened=true };
        }
        catch(Exception ex) { return new(false,stage+": "+ex.Message); }
        finally
        {
            try {if(document is not null&&app is not null) app.CloseDoc(document.GetTitle());} catch(System.Runtime.InteropServices.COMException) {}
            try {if(ownedSource&&source is not null&&app is not null) app.CloseDoc(source.GetTitle());} catch(System.Runtime.InteropServices.COMException) {}
            try {if(previousTitle is not null&&app is not null) {int error=0;app.ActivateDoc3(previousTitle,false,(int)swRebuildOnActivation_e.swDontRebuildActiveDoc,ref error);}} catch(System.Runtime.InteropServices.COMException) {}
            ReleaseCom(app);
        }
    }

    private static void AddDrawingNote(IModelDoc2 document,string text,double x,double y,int points)
    {
        document.ClearSelection2(true);
        var note=(INote?)document.InsertNote(text)??throw new IOException("Could not create drawing note.");
        note.SetHeightInPoints(points);
        var annotation=(IAnnotation)note.GetAnnotation();
        var format=(ITextFormat)annotation.GetTextFormat(0);format.TypeFaceName="Arial";format.CharHeight=points*.0254/72;
        format.WidthFactor=1;
        annotation.SetTextFormat(0,false,format);
        annotation.SetPosition2(x,y,0);
    }

    private static string FindDrawingTemplate(SldWorks app)
    {
        var root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"SOLIDWORKS");
        if(Directory.Exists(root))
        {
            var match=Directory.EnumerateFiles(root,"gb_a3.drwdot",SearchOption.AllDirectories).OrderDescending().FirstOrDefault();
            if(match is not null) return match;
        }
        var configured=app.GetUserPreferenceStringValue((int)swUserPreferenceStringValue_e.swDefaultTemplateDrawing);
        return File.Exists(configured)?configured:throw new IOException("No native drawing template found; provide templatePath.");
    }
}
