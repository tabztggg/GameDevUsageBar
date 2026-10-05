using System.Windows;
using System.Windows.Data;

namespace GameDevUsageBar.App.Presentation;
public sealed record LanguageChoice(string Id,string Label)
{
    public static LanguageChoice[] All {get;}=[new("zh-CN","中文"),new("en-US","English")];
    public static void Bind(DependencyObject target,DependencyProperty property,string english)=>
        BindingOperations.SetBinding(target,property,new Binding("["+L.Key(english)+"]"){Source=L.Instance,Mode=BindingMode.OneWay});
}
