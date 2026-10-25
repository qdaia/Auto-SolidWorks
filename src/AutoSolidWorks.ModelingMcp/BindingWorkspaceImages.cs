using System.IO;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CadModeling.Core;
using CadModeling.Ir;

public static class BindingWorkspaceImages
{
    public const string Revision = "source-binding-assist-1";
    public static string Hash(string path) => DrawingPlanValidation.FileHash(path);
    public static string NewDirectory(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("输出目录必须是绝对路径。");
        path=Path.GetFullPath(path);
        if(Directory.Exists(path)||File.Exists(path)) throw new ArgumentException("输出目录已存在；请使用新的目录。");
        Directory.CreateDirectory(path); return path;
    }
    public static T Sta<T>(Func<T> action)
    {
        T result=default!; Exception? error=null;
        var thread=new Thread(()=>{try {result=action();}catch(Exception e){error=e;}}){IsBackground=true};
        thread.SetApartmentState(ApartmentState.STA);thread.Start();thread.Join();
        if(error is not null) ExceptionDispatchInfo.Capture(error).Throw();
        return result;
    }
    public static BitmapSource Read(string path)
    {
        using var stream=File.OpenRead(path);
        var image=BitmapDecoder.Create(stream,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad).Frames[0];
        image.Freeze();return image;
    }
    public static void Save(string path,BitmapSource bitmap)
    {
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream=new FileStream(path,FileMode.CreateNew,FileAccess.Write);encoder.Save(stream);
    }
    public static BindingWorkspace Prepare(string inventoryPath,string inventorySha,string outputDirectory,BindingCrop[] crops,bool includeOverview=true)
    {
        var inventory=DrawingOmissionValidation.Load(inventoryPath,inventorySha);
        if(!inventory.CompleteExtraction)throw new ArgumentException("来源提取尚未完整，不能生成绑定工作区。");
        if(crops.Length>100)throw new ArgumentException("一次最多100个裁剪。");
        var requests=(includeOverview?inventory.Pages.Select(p=>new BindingCrop("page-"+p.PageNumber,p.PageNumber,new(0,0,1,1))):Enumerable.Empty<BindingCrop>()).Concat(crops).ToArray();
        if(requests.Select(c=>c.Id).Distinct().Count()!=requests.Length || requests.Any(c=>!Regex.IsMatch(c.Id,"^[A-Za-z0-9_-]{1,80}$")))
            throw new ArgumentException("裁剪ID必须唯一且只包含字母、数字、下划线或短横线。");
        foreach(var c in requests)
        {
            CheckBox(c.Bounds,1,1);
            if(!inventory.Pages.Any(p=>p.PageNumber==c.PageNumber))throw new ArgumentException("裁剪页码不存在。");
        }
        var output=NewDirectory(outputDirectory);
        return Sta(()=>
        {
            var frames=new List<BindingFrame>();
            foreach(var page in inventory.Pages.Where(p=>requests.Any(c=>c.PageNumber==p.PageNumber)))
            {
                var original=inventory.Regions.Single(r=>r.PageNumber==page.PageNumber && r.Purpose=="overview" && r.Bounds==new DrawingReviewBox(0,0,1,1));
                var bitmap=Read(original.ImagePath);
                if(bitmap.PixelWidth!=original.PixelWidth||bitmap.PixelHeight!=original.PixelHeight)throw new InvalidDataException("原页尺寸与库存不一致。");
                foreach(var c in requests.Where(c=>c.PageNumber==page.PageNumber))
                {
                    int left=(int)Math.Floor(c.Bounds.Left*bitmap.PixelWidth),top=(int)Math.Floor(c.Bounds.Top*bitmap.PixelHeight);
                    int right=(int)Math.Ceiling(c.Bounds.Right*bitmap.PixelWidth),bottom=(int)Math.Ceiling(c.Bounds.Bottom*bitmap.PixelHeight);
                    var crop=new CroppedBitmap(bitmap,new Int32Rect(left,top,right-left,bottom-top));
                    var image=Path.Combine(output,c.Id+".png");var grid=Path.Combine(output,c.Id+".grid.png");Save(image,crop);
                    var visual=new DrawingVisual();using(var dc=visual.RenderOpen())
                    {
                        dc.DrawImage(crop,new Rect(0,0,crop.PixelWidth,crop.PixelHeight));
                        var pen=new Pen(new SolidColorBrush(Color.FromArgb(95,20,120,220)),.7);
                        var step=Math.Max(25,(int)Math.Ceiling(Math.Max(crop.PixelWidth,crop.PixelHeight)/1200d)*50);
                        for(int x=0;x<crop.PixelWidth;x+=step){dc.DrawLine(pen,new(x,0),new(x,crop.PixelHeight));Label(dc,x.ToString(CultureInfo.InvariantCulture),x+2,2);}
                        for(int y=0;y<crop.PixelHeight;y+=step){dc.DrawLine(pen,new(0,y),new(crop.PixelWidth,y));Label(dc,y.ToString(CultureInfo.InvariantCulture),2,y+2);}
                    }
                    var rendered=new RenderTargetBitmap(crop.PixelWidth,crop.PixelHeight,96,96,PixelFormats.Pbgra32);rendered.Render(visual);Save(grid,rendered);
                    frames.Add(new(c.Id,page.PageNumber,bitmap.PixelWidth,bitmap.PixelHeight,left,top,crop.PixelWidth,crop.PixelHeight,image,Hash(image),grid,Hash(grid)));
                }
            }
            return new BindingWorkspace(Revision,inventory.SourcePath,inventory.SourceSha256,Path.GetFullPath(inventoryPath),inventorySha,frames);
        });
    }
    public static void Label(DrawingContext dc,string text,double x,double y)
    {
        var formatted=new FormattedText(text,CultureInfo.InvariantCulture,FlowDirection.LeftToRight,new Typeface("Segoe UI"),11,Brushes.DarkBlue,1);
        dc.DrawRectangle(Brushes.White,null,new Rect(x,y,formatted.Width+2,formatted.Height));dc.DrawText(formatted,new Point(x+1,y));
    }
    public static void Validate(BindingWorkspace w)
    {
        if(w.Revision!=Revision)throw new ArgumentException("工作区版本不兼容。");
        var inventory=DrawingOmissionValidation.Load(w.InventoryPath,w.InventorySha256);
        if(!SamePath(w.SourcePath,inventory.SourcePath)||!string.Equals(w.SourceSha256,inventory.SourceSha256,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("工作区来源身份不一致。");
        foreach(var f in w.Frames)
        {
            var p=inventory.Pages.Single(p=>p.PageNumber==f.PageNumber);
            if(f.PageWidth!=p.PixelWidth||f.PageHeight!=p.PixelHeight||f.Left<0||f.Top<0||f.Width<=0||f.Height<=0||f.Left+f.Width>f.PageWidth||f.Top+f.Height>f.PageHeight)
                throw new InvalidDataException("工作区坐标变换无效。");
            if(Hash(f.ImagePath)!=f.ImageSha256||Hash(f.GridPath)!=f.GridSha256)throw new InvalidDataException("裁剪图或坐标网格发生变化，必须重新准备。");
        }
    }
    public static bool SamePath(string a,string b)=>string.Equals(Path.GetFullPath(a),Path.GetFullPath(b),StringComparison.OrdinalIgnoreCase);
    public static void CheckBox(DrawingReviewBox b,double width,double height)
    {
        if(new[]{b.Left,b.Top,b.Right,b.Bottom}.Any(x=>!double.IsFinite(x))||b.Left<0||b.Top<0||b.Right>width||b.Bottom>height||b.Left>=b.Right||b.Top>=b.Bottom)
            throw new ArgumentException("区域必须位于当前坐标框架内，宽高为正；不能隐式裁断越界坐标。");
    }
    public static DrawingPagePoint Point(DrawingPagePoint p,BindingFrame f)
    {
        if(!double.IsFinite(p.X)||!double.IsFinite(p.Y)||p.X<0||p.Y<0||p.X>f.Width||p.Y>f.Height)throw new ArgumentException("像素点超出当前裁剪框架。");
        return new((f.Left+p.X)/f.PageWidth,(f.Top+p.Y)/f.PageHeight);
    }
    public static DrawingReviewBox Box(DrawingReviewBox b,BindingFrame f)
    {
        CheckBox(b,f.Width,f.Height);var a=Point(new(b.Left,b.Top),f);var z=Point(new(b.Right,b.Bottom),f);return new(a.X,a.Y,z.X,z.Y);
    }
    public static DrawingDimensionTrace? Trace(DrawingDimensionTrace? t,BindingFrame f)=>t is null?null:t with{
        Path=t.Path.Select(p=>Point(p,f)).ToArray(),StartExtension=t.StartExtension.Select(p=>Point(p,f)).ToArray(),EndExtension=t.EndExtension.Select(p=>Point(p,f)).ToArray(),
        AngularVertex=t.AngularVertex is {} p?Point(p,f):null,
        StartArrowPoint=t.StartArrowPoint is {} a?Point(a,f):null,EndArrowPoint=t.EndArrowPoint is {} b?Point(b,f):null};
}
