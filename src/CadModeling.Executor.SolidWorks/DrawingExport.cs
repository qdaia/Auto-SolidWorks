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
        try { return await _sta.InvokeAsync(()=>ExportDrawingOnSta(request,cancellationToken),cancellationToken); }
        finally { _serialGate.Release(); }
    }

    private static DrawingExportResult ExportDrawingOnSta(DrawingExportRequest request,CancellationToken cancellationToken=default)
    {
        SldWorks? app=null; IModelDoc2? source=null; IModelDoc2? document=null;
        bool ownedSource=false; string? previousTitle=null;string stage="validation";
        try
        {
            DrawingExportValidation.ValidateOutputs(request);
            var layout=DrawingLayoutPlanner.Create(request.Layout);
            var paper=(int)swDwgPaperSizes_e.swDwgPapersUserDefined;
            cancellationToken.ThrowIfCancellationRequested();
            var isAssembly=Path.GetExtension(request.InputPath).Equals(".sldasm",StringComparison.OrdinalIgnoreCase);
            if(!File.Exists(request.InputPath)||(!isAssembly&&!Path.GetExtension(request.InputPath).Equals(".sldprt",StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("绘制导出需要现有的原生 SLDPRT 或 SLDASM。");
            var sourceHash=DrawingSourceHash(request.InputPath);
            app=(SldWorks)Activator.CreateInstance(Type.GetTypeFromProgID("SldWorks.Application",true)!)!;
            previousTitle=(app.IActiveDoc2)?.GetTitle();
            source=(IModelDoc2?)app.GetOpenDocumentByName(request.InputPath);
            int errors=0,warnings=0;
            if(source is null)
            {
                source=(IModelDoc2?)app.OpenDoc6(request.InputPath,(int)(isAssembly?swDocumentTypes_e.swDocASSEMBLY:swDocumentTypes_e.swDocPART),
                    (int)(swOpenDocOptions_e.swOpenDocOptions_Silent|swOpenDocOptions_e.swOpenDocOptions_ReadOnly),"",ref errors,ref warnings);
                ownedSource=source is not null&&Path.GetFullPath(source.GetPathName()).Equals(Path.GetFullPath(request.InputPath),StringComparison.OrdinalIgnoreCase);
            }
            if(source is null||!Path.GetFullPath(source.GetPathName()).Equals(Path.GetFullPath(request.InputPath),StringComparison.OrdinalIgnoreCase))
                throw new IOException($"无法打开源模型的精确版本（错误={errors}）。请使用唯一的基名。");
            if(source.GetSaveFlag()) throw new InvalidOperationException("源文件有未保存的更改；导出需要保存的参考状态。");
            stage="inspection";
            // Read without rebuilding the open source. The general inspector deliberately
            // rejects open documents; drawing export owns/borrows this document already.
            var dimensions=ReadDrawingSourceDimensions(source);
            var components=isAssembly?(((IAssemblyDoc)source).GetComponents(false) as object[]??[]).Cast<IComponent2>().ToArray():[];
            var componentHashes=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
            foreach(var component in components)
            {
                if(component.IsSuppressed()) continue;
                var path=component.GetPathName();
                if(string.IsNullOrWhiteSpace(path)||!File.Exists(path)||component.GetModelDoc2() is not IModelDoc2 componentModel)
                    throw new IOException($"绘制需要已解决并保存的组件：{component.Name2}");
                if(componentModel.GetSaveFlag()) throw new IOException($"组件有未保存的更改:{component.Name2}");
                componentHashes.TryAdd(path,DrawingSourceHash(path));
            }
            var template=request.TemplatePath??FindDrawingTemplate(app);
            if(!Path.IsPathFullyQualified(template)||!File.Exists(template)||!Path.GetExtension(template).Equals(".drwdot",StringComparison.OrdinalIgnoreCase))
                throw new IOException("需要一个现有的绝对 .drwdot 模板。");
            var modelViewNames=(string[])source.GetModelViewNames();
            stage="创建图纸";
            document=(IModelDoc2?)app.NewDocument(template,paper,layout.Width,layout.Height)
                ??throw new IOException("无法从模板创建图纸。");
            var drawing=(IDrawingDoc)document;
            foreach(var toggle in new[]{swUserPreferenceToggle_e.swDisplayAxes,swUserPreferenceToggle_e.swDisplayTemporaryAxes,swUserPreferenceToggle_e.swDisplayPlanes})
                document.Extension.SetUserPreferenceToggle((int)toggle,(int)swUserPreferenceOption_e.swDetailingNoOptionSpecified,false);
            var sheet=(ISheet)drawing.GetCurrentSheet(); sheet.SetName(GeneratedChineseText.ViewsSheet);
            if(!drawing.SetupSheet6(GeneratedChineseText.ViewsSheet,paper,(int)swDwgTemplates_e.swDwgTemplateNone,
                1,1,layout.FirstAngle,"",layout.Width,layout.Height,"",true,0,0,0,0,0,0)) throw new IOException("绘制图纸设置失败。");
            var specs=layout.Views.Select(v=>(v.Name,"*"+v.Name,v.X,v.Y));
            var views=new List<(IView View,string Name,string Orientation,double X,double Y)>();
            foreach(var (name,orientation,x,y) in specs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                stage="创建视图";
            var chinese=name switch {"Front"=>"*前视","Top"=>"*上视","Left"=>"*左视","Right"=>"*右视","Bottom"=>"*下视","Back"=>"*后视",_=>"*等轴测"};
                var nativeOrientation=modelViewNames.FirstOrDefault(n=>n.Equals(orientation,StringComparison.OrdinalIgnoreCase)||n.Equals(chinese,StringComparison.Ordinal))
                    ??throw new IOException($"标准{name}视图未找到。可用：{string.Join(", ",modelViewNames)}");
                var view=(IView?)drawing.CreateDrawViewFromModelView3(request.InputPath,nativeOrientation,x,y,0)
                    ??throw new IOException($"无法创建{name}视图 ({nativeOrientation}).");
                view.SetName2(GeneratedChineseText.ViewName(name));view.UseSheetScale=0;view.ScaleDecimal=1;
                view.SetDisplayMode3(false,(int)(name=="Isometric"?swDisplayMode_e.swHIDDEN:swDisplayMode_e.swHIDDEN_GREYED),false,true);
                views.Add((view,GeneratedChineseText.ViewName(name),orientation,x,y));
            }
            document.ForceRebuild3(false);
            var fit=views.Min(v=> {var o=(double[])v.View.GetOutline();return v.View.ScaleDecimal*Math.Min((layout.CellWidth-.02)/Math.Max(o[2]-o[0],.001),(layout.CellHeight-.02)/Math.Max(o[3]-o[1],.001));});
            var scales=new[]{.05,.1,.125,.2,.25,.5,.75,1,1.25,1.5,2,4,5,10};
            var scale=scales.Where(s=>s<=fit).DefaultIfEmpty(fit*.9).Max();
            foreach(var v in views) {v.View.ScaleDecimal=scale;v.View.Position=new double[]{v.X,v.Y,0};}
            document.ClearSelection2(true);
            drawing.ActivateView(GeneratedChineseText.ViewName("Front"));
            stage="导入标注";
            drawing.InsertModelAnnotations3((int)swImportModelItemsSource_e.swImportModelItemsFromEntireModel,
                (int)(swInsertAnnotation_e.swInsertDimensionsMarkedForDrawing|swInsertAnnotation_e.swInsertDimensionsNotMarkedForDrawing),true,false,true,false);
            document.ForceRebuild3(false);
            stage="列出绘图尺寸";
            var imported=new HashSet<string>(StringComparer.Ordinal);
            var placedDimensions=new List<DrawingViewDimension>();
            foreach(var v in views)
            {
                document.ClearSelection2(true);
                int count=0;
                foreach(var display in (v.View.GetDisplayDimensions() as object[]??[]).Cast<IDisplayDimension>())
                {
                    if(++count>5000) throw new InvalidOperationException("绘制尺寸标注的枚举超过限制。");
                    var dim=(IDimension?)display.GetDimension2(0);
                    if(dim is null) continue;
                    var name=isAssembly?dim.FullName:string.Join('@',dim.FullName.Split('@').Take(2));
                    imported.Add(name);
                    placedDimensions.Add(new(v.Name,ReadDrawingDimension(display,name)));
                    var annotation=(IAnnotation)display.GetAnnotation();
                    var format=(ITextFormat)annotation.GetTextFormat(0);format.TypeFaceName=GeneratedChineseText.DrawingFont;format.CharHeight=.0032;format.WidthFactor=1;
                    annotation.SetTextFormat(0,false,format);
                    annotation.Select3(true,null);
                }
                if(count>1) document.Extension.AlignDimensions((int)swAlignDimensionType_e.swAlignDimensionType_AutoArrange,.008);
            }
            if(placedDimensions.Count==0) throw new IOException("没有在图纸视图中放置原生模型尺寸。在导出有尺寸的图纸之前，先添加模型尺寸。");
            stage="视图标签";
            drawing.ActivateView("");
            AddDrawingNote(document,$"{Path.GetFileNameWithoutExtension(request.InputPath)}  |  模型参考工程图",layout.Margin,layout.Height-.017,14);
            AddDrawingNote(document,$"{(layout.FirstAngle?"第一角投影":"第三角投影")}  /  毫米  /  比例 {scale:0.###}:1  /  {(isAssembly?"装配体":"零件")}",layout.Margin,layout.Height-.028,10);
            foreach(var v in views) AddDrawingNote(document,v.Name,v.X-layout.CellWidth/2,v.Y+layout.CellHeight/2-.004,10);
            AddDrawingNote(document,"视图显示原生尺寸；参数表列出未在视图中标注的参数。",layout.Margin,.018,9);
            var viewResults=views.Select(v=>new ExportedDrawingView(v.Name,v.Orientation,scale,(double[])v.View.GetOutline())).ToArray();
            stage="排程表";
            var sheetNames=layout.SheetNames(dimensions.Length);
            var scheduleTexts=new Dictionary<string,List<string>>(StringComparer.Ordinal);
            foreach(var (sheetName,page) in sheetNames.Skip(1).Select((name,index)=>(name,index)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if(!drawing.NewSheet4(sheetName,paper,(int)swDwgTemplates_e.swDwgTemplateNone,
                    1,1,layout.FirstAngle,"",layout.Width,layout.Height,"",0,0,0,0,0,0))throw new IOException("无法创建参数表页。");
                scheduleTexts[sheetName]=[];
                AddDrawingNote(document,$"参考参数表  {page+1}/{sheetNames.Count-1}",layout.Margin,layout.Height-.017,14);
                AddDrawingNote(document,"已保存模型参数：毫米／度／个。“仅参数表”表示视图中未标注。",layout.Margin,layout.Height-.030,9);
                if(dimensions.Length==0)AddDrawingNote(document,$"无装配体级参数。视图中已标注 {placedDimensions.Count} 个零部件尺寸。",layout.Margin,layout.Height-.052,10);
                for(int rowIndex=0;rowIndex<layout.ScheduleCapacity&&page*layout.ScheduleCapacity+rowIndex<dimensions.Length;rowIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var i=page*layout.ScheduleCapacity+rowIndex;var d=dimensions[i];
                    if(d.Value is null||!double.IsFinite(d.Value.Value))throw new IOException("参数表需要有限的类型源值。");
                    var unit=GeneratedChineseText.ParameterUnit(d.Unit);
                    var label=GeneratedChineseText.ParameterLabel(d.Name,d.Unit,i+1);
                    var text=FormattableString.Invariant($"{i+1:00}  {label} = {d.Value:0.####} {unit}  [{(imported.Contains(d.Name)?"已标注":"仅参数表")}]");
                    var (x,y)=layout.SchedulePosition(rowIndex);
                    AddDrawingNote(document,text,x,y,10);scheduleTexts[sheetName].Add(text);
                }
                AddDrawingNote(document,$"源模型参数：{dimensions.Length} | 视图已标注：{dimensions.Count(d=>imported.Contains(d.Name))}",layout.Margin,.035,9);
                AddDrawingNote(document,"模型参考工程图；未认证制造信息完整性。",layout.Margin,.020,9);
            }
            var missing=dimensions.Where(d=>!imported.Contains(d.Name)).Select(d=>d.Name).ToArray();
            drawing.ActivateSheet(GeneratedChineseText.ViewsSheet);document.ForceRebuild3(false);
            Directory.CreateDirectory(Path.GetDirectoryName(request.NativePath)!);Directory.CreateDirectory(Path.GetDirectoryName(request.PdfPath)!);
            stage="保存为原生格式";
            cancellationToken.ThrowIfCancellationRequested();
            if(!document.Extension.SaveAs(request.NativePath,(int)swSaveAsVersion_e.swSaveAsCurrentVersion,(int)swSaveAsOptions_e.swSaveAsOptions_Silent,null,ref errors,ref warnings)||errors!=0)
                throw new IOException($"原生绘制保存失败 (errors={errors}, 警告={warnings}).");
            stage="保存为pdf";
            var pdf=(IExportPdfData)app.GetExportFileData((int)swExportDataFileType_e.swExportPdfData);
            pdf.ViewPdfAfterSaving=false;
            if(!pdf.SetSheets((int)swExportDataSheetsToExport_e.swExportData_ExportSpecifiedSheets,sheetNames.ToArray())) throw new IOException("无法选择 PDF 草图页。");
            errors=0;warnings=0;
            if(!document.Extension.SaveAs(request.PdfPath,(int)swSaveAsVersion_e.swSaveAsCurrentVersion,(int)swSaveAsOptions_e.swSaveAsOptions_Silent,pdf,ref errors,ref warnings)||errors!=0)
                throw new IOException($"PDF导出失败 (errors={errors}, warnings={warnings}).");
            if(!File.Exists(request.NativePath)||new FileInfo(request.NativePath).Length==0||!File.Exists(request.PdfPath)||new FileInfo(request.PdfPath).Length==0)
                throw new IOException("绘制输出验证失败。");
            if(sourceHash!=DrawingSourceHash(request.InputPath)) throw new IOException("源哈希在绘图导出期间更改。");
            stage="保存的绘图读回";
            app.CloseDoc(document.GetTitle());document=null;
            errors=0;warnings=0;
            stage="重新打开原生图纸";
            document=(IModelDoc2?)app.OpenDoc6(request.NativePath,(int)swDocumentTypes_e.swDocDRAWING,
                (int)(swOpenDocOptions_e.swOpenDocOptions_Silent|swOpenDocOptions_e.swOpenDocOptions_ReadOnly|swOpenDocOptions_e.swOpenDocOptions_LoadModel),"",ref errors,ref warnings)
                ??throw new IOException($"保存的绘图无法重新打开（错误={errors}）。");
            drawing=(IDrawingDoc)document;
            stage="保存的图纸页面验证";
            if(!((string[])drawing.GetSheetNames()).SequenceEqual(sheetNames)) throw new IOException("保存绘图表册库存发生变化。");
            foreach(var name in sheetNames)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if(!drawing.ActivateSheet(name))throw new IOException("无法激活绘图板保存的图纸。");
                var props=(double[])((ISheet)drawing.GetCurrentSheet()).GetProperties2();
                if((props[4]!=0)!=layout.FirstAngle||Math.Abs(props[5]-layout.Width)>.00001||Math.Abs(props[6]-layout.Height)>.00001)throw new IOException("保存的曲面布局/投影已更改。");
                if(scheduleTexts.TryGetValue(name,out var entries))
                {
                    var sheetView=(IView)drawing.GetFirstView();
                    var notes=(sheetView.GetAnnotations() as object[]??[]).OfType<IAnnotation>().Select(a=>a.GetSpecificAnnotation()).OfType<INote>().Select(n=>n.GetText()).ToList();
                    foreach(var expected in entries)
                    {
                        var index=notes.IndexOf(expected);
                        if(index<0)throw new IOException("保存的计划项失去身份、值、单位或页面："+expected);
                        notes.RemoveAt(index);
                    }
                }
            }
            drawing.ActivateSheet(GeneratedChineseText.ViewsSheet);
            var savedProperties=(double[])((ISheet)drawing.GetCurrentSheet()).GetProperties2();
            if((savedProperties[4]!=0)!=layout.FirstAngle||Math.Abs(savedProperties[5]-layout.Width)>.00001||Math.Abs(savedProperties[6]-layout.Height)>.00001)
                throw new IOException("保存绘图的投影或曲面尺寸发生变化。");
            var savedViews=new List<string>();
            var savedDimensions=new List<DrawingViewDimension>();
            stage="保存绘图视图枚举";
            for(var view=((IView)drawing.GetFirstView()).GetNextView() as IView;view is not null;view=view.GetNextView() as IView)
            {
                if(savedViews.Count>=layout.Views.Count) throw new IOException("意外保存的绘图视图附加视图。");
                if(!Path.GetFullPath(view.GetReferencedModelName()).Equals(Path.GetFullPath(request.InputPath),StringComparison.OrdinalIgnoreCase))
                    throw new IOException("保存绘图参考的模型不同。");
                savedViews.Add(view.GetName2());
                if(!view.IsModelLoaded()&&view.LoadModel()!=0) throw new IOException("保存的绘图视图无法加载其尺寸模型。");
                if(view.IsLightweight()) view.SetLightweightToResolved();
                if(view.IsLightweight()) throw new IOException("保存的绘图视图在读回尺寸后仍保持轻量级。");
                stage="保存的尺寸在"+view.GetName2();
                int dimensionCount=0;
                foreach(var display in (view.GetDisplayDimensions() as object[]??[]).Cast<IDisplayDimension>())
                {
                    var dim=(IDimension?)display.GetDimension2(0)??throw new IOException("保存的绘图尺寸失去了其模型引用。");
                    var name=isAssembly?dim.FullName:string.Join('@',dim.FullName.Split('@').Take(2));
                    if(++dimensionCount>5000) throw new IOException("保存绘图的尺寸枚举超过了其限制。");
                    savedDimensions.Add(new(view.GetName2(),ReadDrawingDimension(display,name)));
                }
            }
            if(!savedViews.Order().SequenceEqual(views.Select(v=>v.Name).Order())) throw new IOException("保存绘图视图库存发生变化。");
            var unmatched=new List<DrawingViewDimension>(savedDimensions);
            foreach(var expected in placedDimensions)
            {
                var index=unmatched.FindIndex(actual=>actual.ViewName==expected.ViewName&&actual.Dimension.Name==expected.Dimension.Name&&
                    actual.Dimension.Unit==expected.Dimension.Unit&&actual.Dimension.Kind==expected.Dimension.Kind&&
                    Math.Abs(actual.Dimension.SystemValue-expected.Dimension.SystemValue)<=1e-10);
                if(index<0)
                {
                    throw new IOException("保存的绘图尺寸改变了身份、值、类型或视图位置。");
                }
                unmatched.RemoveAt(index);
            }
            if(unmatched.Count>0) throw new IOException("保存的绘图包含意外的尺寸。");
            foreach(var entry in componentHashes)
                if(entry.Value!=DrawingSourceHash(entry.Key))
                    throw new IOException($"源组件哈希在绘图导出期间更改：{entry.Key}");
            return new(true,"导出配置视图和分页的原生参数表单；源文件保留。")
            { NativePath=request.NativePath,PdfPath=request.PdfPath,SourceSha256=sourceHash,Views=viewResults,
              ImportedDimensionNames=imported.Order().ToArray(),ViewDimensions=savedDimensions,DimensionsReopened=true,
              SourceDimensions=dimensions,UnplacedDimensionNames=missing,Reopened=true,Projection=layout.FirstAngle?"FirstAngle":"ThirdAngle",
              Sheets=sheetNames,ScheduleEntryCount=dimensions.Length,ScheduleReopened=true };
        }
        catch(Exception ex) { return new(false,stage+": "+ex.Message) { NativePath=request.NativePath,PdfPath=request.PdfPath }; }
        finally
        {
            try {if(document is not null&&app is not null) app.CloseDoc(document.GetTitle());} catch(System.Runtime.InteropServices.COMException) {}
            try {if(ownedSource&&source is not null&&app is not null) app.CloseDoc(source.GetTitle());} catch(System.Runtime.InteropServices.COMException) {}
            try {if(previousTitle is not null&&app is not null) {int error=0;app.ActivateDoc3(previousTitle,false,(int)swRebuildOnActivation_e.swDontRebuildActiveDoc,ref error);}} catch(System.Runtime.InteropServices.COMException) {}
            ReleaseCom(app);
        }
    }

    private static ModelDimension ReadDrawingDimension(IDisplayDimension display,string name)
    {
        var dim=(IDimension)display.GetDimension2(0);
        // Reopened drawing dimensions can lack a model-configuration collection:
        // GetSystemValue2("") returns 0 and GetSystemValue3 returns null there.
        // SystemValue reads the actual dimension represented by this drawing view.
        var value=dim.SystemValue;
        if(!double.IsFinite(value)) throw new IOException("绘图尺寸有无限值。");
        var type=(swDimensionParamType_e)dim.GetType();
        var unit=type switch {swDimensionParamType_e.swDimensionParamTypeDoubleLinear=>"Millimeter",
            swDimensionParamType_e.swDimensionParamTypeDoubleAngular=>"Degree",swDimensionParamType_e.swDimensionParamTypeInteger=>"Unitless",_=>"Unknown"};
        return new(name,value,((swDimensionType_e)display.Type2).ToString()) {ParameterType=type.ToString(),Unit=unit,
            Value=unit=="Millimeter"?value*1000:unit=="Degree"?value*180/Math.PI:unit=="Unitless"?value:null};
    }

    private static string DrawingSourceHash(string path)
    {
        // SOLIDWORKS can hold component write handles even for a read-only parent.
        using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static ModelDimension[] ReadDrawingSourceDimensions(IModelDoc2 source)
    {
        var visited=new HashSet<int>();
        var dimensions=new Dictionary<string,ModelDimension>(StringComparer.Ordinal);
        void Visit(IFeature feature)
        {
            if(!visited.Add(feature.GetID())) return;
            if(visited.Count>1000) throw new InvalidOperationException("绘制源特征库存超过1000个特征。");
            // Component feature parameters belong to their own part/configuration, not
            // the assembly parameter namespace (which can have identical short names).
            if(source is IAssemblyDoc&&feature.GetTypeName2()=="Reference") return;
            if(source is IAssemblyDoc)
            {
                int count=0;
                for(var display=feature.GetFirstDisplayDimension() as IDisplayDimension;display is not null;display=feature.GetNextDisplayDimension(display) as IDisplayDimension)
                {
                    if(++count>5000) throw new IOException("源图尺寸枚举超出其限制。");
                    var dim=(IDimension)display.GetDimension2(0);
                    dimensions.TryAdd(dim.FullName,ReadDrawingDimension(display,dim.FullName));
                }
            }
            else foreach(var dim in ReadNativeFeatureDimensions(source,feature)) dimensions.TryAdd(dim.Name,dim);
            for(var child=feature.IGetFirstSubFeature();child is not null;child=child.IGetNextSubFeature()) Visit(child);
        }
        for(var feature=source.IFirstFeature();feature is not null;feature=feature.IGetNextFeature()) Visit(feature);
        return dimensions.Values.ToArray();
    }

    private static void AddDrawingNote(IModelDoc2 document,string text,double x,double y,int points)
    {
        document.ClearSelection2(true);
        var note=(INote?)document.InsertNote(text)??throw new IOException("无法创建绘图批注。");
        note.SetHeightInPoints(points);
        var annotation=(IAnnotation)note.GetAnnotation();
        var format=(ITextFormat)annotation.GetTextFormat(0);format.TypeFaceName=GeneratedChineseText.DrawingFont;format.CharHeight=points*.0254/72;
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
        return File.Exists(configured)?configured:throw new IOException("未找到原生绘图模板；请提供 templatePath。");
    }
}
