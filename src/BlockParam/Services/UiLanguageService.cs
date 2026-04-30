using System;
using System.Globalization;
using System.IO;
using System.Threading;
using BlockParam.Diagnostics;
using BlockParam.Localization;

namespace BlockParam.Services;

/// <summary>
/// Persists the user's UI-language choice (#50) and applies it to the current
/// thread's UI culture. The choice lives in a tiny standalone file
/// (<c>%APPDATA%\BlockParam\ui-language.txt</c>) — kept separate from
/// <see cref="UiZoomService"/>'s JSON so neither service needs to merge-and-rewrite
/// the other's keys to avoid clobbering on save.
///
/// File format: a single line containing "auto" / "en" / "de". Anything else is
/// treated as Auto (forward-compatible with future additions).
/// </summary>
public class UiLanguageService
{
    private const string CultureGerman = "de-DE";
    private const string CultureEnglish = "en-US";

    private readonly string _settingsPath;
    private UiLanguageOption _language = UiLanguageOption.Auto;
    private bool _loaded;

    private static UiLanguageService? _shared;
    public static UiLanguageService Shared => _shared ??= new UiLanguageService();

    public UiLanguageService() : this(DefaultSettingsPath()) { }

    public UiLanguageService(string settingsPath)
    {
        _settingsPath = settingsPath;
    }

    public static string DefaultSettingsPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "BlockParam", "ui-language.txt");

    public UiLanguageOption Language
    {
        get
        {
            EnsureLoaded();
            return _language;
        }
    }

    public void SetLanguage(UiLanguageOption value)
    {
        EnsureLoaded();
        if (_language == value) return;
        _language = value;
        Save();
    }

    /// <summary>
    /// Applies the persisted language choice to <see cref="Thread.CurrentUICulture"/>
    /// (and <see cref="Thread.CurrentCulture"/>, so number / date formatting matches).
    /// Auto is a no-op: leave whatever the OS / hosting process already set.
    /// Call once per dialog open, before any <c>Res.Get</c> runs.
    /// </summary>
    public void ApplyToCurrentThread()
    {
        var culture = Language switch
        {
            UiLanguageOption.German => new CultureInfo(CultureGerman),
            UiLanguageOption.English => new CultureInfo(CultureEnglish),
            _ => null,
        };
        if (culture == null) return;

        Thread.CurrentThread.CurrentUICulture = culture;
        Thread.CurrentThread.CurrentCulture = culture;
    }

    private void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;

        if (!File.Exists(_settingsPath)) return;

        try
        {
            var raw = File.ReadAllText(_settingsPath).Trim();
            _language = Parse(raw);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning(ex, "UiLanguageService: cannot read {Path} — defaulting to Auto", _settingsPath);
        }
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_settingsPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            File.WriteAllText(_settingsPath, Format(_language));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning(ex, "UiLanguageService: cannot save {Path}", _settingsPath);
        }
    }

    internal static UiLanguageOption Parse(string? raw) => raw?.Trim().ToLowerInvariant() switch
    {
        "en" or "english" => UiLanguageOption.English,
        "de" or "german"  => UiLanguageOption.German,
        _ => UiLanguageOption.Auto,
    };

    internal static string Format(UiLanguageOption lang) => lang switch
    {
        UiLanguageOption.English => "en",
        UiLanguageOption.German  => "de",
        _ => "auto",
    };
}
