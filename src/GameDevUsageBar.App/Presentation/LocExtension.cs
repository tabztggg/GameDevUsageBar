using System.Windows.Data;
using System.Windows.Markup;
using GameDevUsageBar.Core.Presentation;

namespace GameDevUsageBar.App.Presentation;
[MarkupExtensionReturnType(typeof(object))]
public sealed class LocExtension(string key) : MarkupExtension
{
    public string Key {get;set;}=key;
    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        // Commas in literal sentences are parsed as indexer arguments by WPF.
        // Known catalogue text uses the same stable lookup keys as other views.
        var lookup=Localizer.Catalogue.ContainsKey(Key)?Localizer.Key(Key):Key;
        return new Binding("["+lookup+"]") {Source=Localizer.Instance,Mode=BindingMode.OneWay}.ProvideValue(serviceProvider);
    }
}
