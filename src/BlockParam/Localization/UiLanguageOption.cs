namespace BlockParam.Localization;

/// <summary>
/// User-selectable UI language for the bulk-change dialog (#50). Decoupled from
/// the OS culture so an English Windows install can run the German UI and vice
/// versa. <see cref="Auto"/> = follow the OS culture (current 0.x behaviour).
/// </summary>
public enum UiLanguageOption
{
    Auto,
    English,
    German,
}
