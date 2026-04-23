using System.Windows.Markup;

namespace BlockParam.Localization;

/// <summary>
/// XAML markup extension for localized strings.
/// Usage: Content="{loc:Loc Dialog_Apply}"
/// </summary>
[MarkupExtensionReturnType(typeof(string))]
public class LocExtension : MarkupExtension
{
    public string Key { get; set; }

    public LocExtension(string key)
    {
        Key = key;
    }

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        return Res.Get(Key);
    }
}
