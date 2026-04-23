using System.Globalization;
using System.Resources;

namespace BlockParam.Localization;

/// <summary>
/// Provides access to localized strings from Strings.resx.
/// Wraps ResourceManager for easy use with string.Format.
/// </summary>
public static class Res
{
    private static readonly ResourceManager Mgr =
        new("BlockParam.Localization.Strings", typeof(Res).Assembly);

    /// <summary>Gets a localized string by key.</summary>
    public static string Get(string key) =>
        Mgr.GetString(key, CultureInfo.CurrentUICulture) ?? $"[{key}]";

    /// <summary>Gets a localized string and formats it with arguments.</summary>
    public static string Format(string key, params object[] args) =>
        string.Format(CultureInfo.CurrentCulture, Get(key), args);
}
