using System.Reflection;
using System.Text.Json;

internal static class PluginCapabilityManifest
{
    public static JsonElement Current { get; } = Load();
    private static JsonElement Load()
    {
        var assembly=typeof(PluginCapabilityManifest).Assembly;
        using var stream=assembly.GetManifestResourceStream("AutoSolidWorks.capability-manifest.json")??throw new InvalidOperationException("捆绑的能力manifest文件缺失。");
        using var document=JsonDocument.Parse(stream);
        var result=document.RootElement.Clone();
        var version=assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if(result.GetProperty("version").GetString()!=version)throw new InvalidOperationException("运行时和能力表现版本不同。");
        return result;
    }
}
