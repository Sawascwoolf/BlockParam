namespace BlockParam.UI;

/// <summary>
/// State for the pre-dialog loading splash (#125). Deliberately tiny and
/// independent of <see cref="BulkChangeViewModel"/> (#80) — a pre-dialog
/// lifecycle that dies before the main dialog opens. Holds only display
/// strings; the splash thread renders, it never computes or localizes
/// anything (strings are pre-localized on the TIA thread and pushed in via
/// <see cref="LoadingSplashController"/>).
/// </summary>
public sealed class LoadingSplashViewModel : ViewModelBase
{
    private string _title = string.Empty;
    private string _statusText = string.Empty;
    private string _counterText = string.Empty;
    private string _humorLine = string.Empty;

    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value ?? string.Empty);
    }

    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    /// <summary>Secondary "(N of M)" line; empty string when not shown.</summary>
    public string CounterText
    {
        get => _counterText;
        set => SetProperty(ref _counterText, value ?? string.Empty);
    }

    /// <summary>
    /// Light rotating quip (#127), rendered dim/italic under the status line.
    /// Empty until elapsed prep time crosses the splash threshold, then set
    /// once and held for the rest of the session. Never replaces the
    /// load-bearing <see cref="StatusText"/>.
    /// </summary>
    public string HumorLine
    {
        get => _humorLine;
        set => SetProperty(ref _humorLine, value ?? string.Empty);
    }
}
