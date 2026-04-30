using System;
using System.Globalization;
using System.IO;
using System.Threading;
using BlockParam.Localization;
using BlockParam.Services;
using FluentAssertions;
using Xunit;

namespace BlockParam.Tests;

public class UiLanguageServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _settingsPath;
    private readonly CultureInfo _originalUiCulture;
    private readonly CultureInfo _originalCulture;

    public UiLanguageServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "BlockParamTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _settingsPath = Path.Combine(_tempDir, "ui-language.txt");

        // Apply mutates the calling thread's culture; capture so tests can restore.
        _originalUiCulture = Thread.CurrentThread.CurrentUICulture;
        _originalCulture = Thread.CurrentThread.CurrentCulture;
    }

    public void Dispose()
    {
        Thread.CurrentThread.CurrentUICulture = _originalUiCulture;
        Thread.CurrentThread.CurrentCulture = _originalCulture;
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Language_WhenFileMissing_DefaultsToAuto()
    {
        var svc = new UiLanguageService(_settingsPath);
        svc.Language.Should().Be(UiLanguageOption.Auto);
    }

    [Theory]
    [InlineData(UiLanguageOption.English)]
    [InlineData(UiLanguageOption.German)]
    [InlineData(UiLanguageOption.Auto)]
    public void SetLanguage_ThenReloadFromDisk_RoundTrips(UiLanguageOption option)
    {
        new UiLanguageService(_settingsPath).SetLanguage(option);

        var reloaded = new UiLanguageService(_settingsPath);
        reloaded.Language.Should().Be(option);
    }

    [Fact]
    public void ApplyToCurrentThread_OnEnglish_SetsEnUsCulture()
    {
        var svc = new UiLanguageService(_settingsPath);
        svc.SetLanguage(UiLanguageOption.English);

        svc.ApplyToCurrentThread();

        Thread.CurrentThread.CurrentUICulture.Name.Should().Be("en-US");
        Thread.CurrentThread.CurrentCulture.Name.Should().Be("en-US");
    }

    [Fact]
    public void ApplyToCurrentThread_OnGerman_SetsDeDeCulture()
    {
        var svc = new UiLanguageService(_settingsPath);
        svc.SetLanguage(UiLanguageOption.German);

        svc.ApplyToCurrentThread();

        Thread.CurrentThread.CurrentUICulture.Name.Should().Be("de-DE");
        Thread.CurrentThread.CurrentCulture.Name.Should().Be("de-DE");
    }

    [Fact]
    public void ApplyToCurrentThread_OnAuto_LeavesCultureUntouched()
    {
        // Pre-set a known starting culture so we can assert it survives Apply.
        var initial = new CultureInfo("fr-FR");
        Thread.CurrentThread.CurrentUICulture = initial;
        Thread.CurrentThread.CurrentCulture = initial;

        var svc = new UiLanguageService(_settingsPath);
        svc.ApplyToCurrentThread();

        Thread.CurrentThread.CurrentUICulture.Name.Should().Be("fr-FR");
        Thread.CurrentThread.CurrentCulture.Name.Should().Be("fr-FR");
    }

    [Fact]
    public void Language_OnUnknownFileContents_FallsBackToAuto()
    {
        // Forward-compat: if a future build wrote a token this version doesn't
        // recognise, treat it as Auto rather than crashing the dialog open.
        File.WriteAllText(_settingsPath, "klingon");

        new UiLanguageService(_settingsPath).Language.Should().Be(UiLanguageOption.Auto);
    }

    [Theory]
    [InlineData("en", UiLanguageOption.English)]
    [InlineData("EN", UiLanguageOption.English)]
    [InlineData(" en ", UiLanguageOption.English)]
    [InlineData("english", UiLanguageOption.English)]
    [InlineData("de", UiLanguageOption.German)]
    [InlineData("DE", UiLanguageOption.German)]
    [InlineData("german", UiLanguageOption.German)]
    [InlineData("auto", UiLanguageOption.Auto)]
    [InlineData("", UiLanguageOption.Auto)]
    [InlineData(null, UiLanguageOption.Auto)]
    public void Parse_AcceptsKnownTokens_CaseAndWhitespaceInsensitive(string? input, UiLanguageOption expected)
    {
        UiLanguageService.Parse(input).Should().Be(expected);
    }
}
