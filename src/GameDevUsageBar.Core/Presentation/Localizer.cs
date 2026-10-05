using System.ComponentModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GameDevUsageBar.Core.Presentation;

// Presentation only: provider IDs, account labels, credentials and transport data
// never pass through this catalogue.
public sealed class Localizer : INotifyPropertyChanged
{
    public static Localizer Instance {get;}=new();
    private static readonly Dictionary<string,string> translations=Load();
    private static readonly Dictionary<string,string> sources=translations.Keys.ToDictionary(Key);
    public static IReadOnlyDictionary<string,string> Catalogue=>translations;
    public static string Language {get;private set;}="en-US";
    public static CultureInfo Culture=>CultureInfo.GetCultureInfo(Language);
    public static event Action? Changed;
    public event PropertyChangedEventHandler? PropertyChanged;
    public string this[string key]=>T(sources.TryGetValue(key,out var source)?source:key);
    public static string Key(string source)=>"L"+Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)))[..10].ToLowerInvariant();
    private static Dictionary<string,string> Load()
    {
        using var stream=typeof(Localizer).Assembly.GetManifestResourceStream("GameDevUsageBar.Core.Presentation.zh-CN.json")!;
        return JsonSerializer.Deserialize<Dictionary<string,string>>(stream)!;
    }
    public static string Resolve(string? language)=>language is "zh-CN" or "en-US" ? language
        : CultureInfo.CurrentUICulture.TwoLetterISOLanguageName=="zh" ? "zh-CN" : "en-US";
    public static void SetLanguage(string? language)
    {
        var next=Resolve(language);if(Language==next)return;Language=next;
        Instance.PropertyChanged?.Invoke(Instance,new("Item[]"));Changed?.Invoke();
    }
    public static string T(string source)=>Language=="zh-CN" && translations.TryGetValue(source,out var text)?text:source;
    public static string F(string source,params object[] values)=>string.Format(Culture,T(source),values);
    public static string Date(DateTimeOffset date,bool seconds=false)=>date.ToLocalTime().ToString(Language=="zh-CN"?(seconds?"M月d日 HH:mm:ss":"M月d日 HH:mm"):(seconds?"MMM d, HH:mm:ss":"MMM d, HH:mm"),Culture);
    public static string Compact(string value)
    {
        var cached=value.StartsWith('~');if(cached)value=value[1..];
        var demo=value.StartsWith("DEMO ",StringComparison.Ordinal);if(demo)value=value[5..];
        value=T(value);
        if(Language=="zh-CN" && value.EndsWith(" cr",StringComparison.Ordinal))value=value[..^3]+" 积分";
        return (cached?"~":"")+(demo?T("DEMO")+" ":"")+value;
    }
}
