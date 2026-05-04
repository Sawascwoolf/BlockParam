using System.Diagnostics;
using System.Windows;
using BlockParam.Diagnostics;
using BlockParam.Localization;
using BlockParam.Updates;

namespace BlockParam.UI;

public partial class UpdateAvailableDialog : Window
{
    private readonly UpdateInfo _info;

    public UpdateAvailableDialog(UpdateInfo info)
    {
        InitializeComponent();
        WindowIconHelper.SetIcon(this);
        ZoomHost.Attach(this);
        _info = info;
        Populate();
    }

    private void Populate()
    {
        ReleaseTitleText.Text = string.IsNullOrWhiteSpace(_info.Name) ? _info.TagName : _info.Name;

        var current = typeof(UpdateAvailableDialog).Assembly.GetName().Version;
        var currentText = current != null
            ? $"v{current.Major}.{System.Math.Max(0, current.Minor)}.{System.Math.Max(0, current.Build)}"
            : "v?";
        var latest = _info.TagName;
        if (latest.Length > 0 && latest[0] != 'v' && latest[0] != 'V') latest = "v" + latest;
        VersionDeltaText.Text = Res.Format("Update_VersionDelta", currentText, latest);

        if (_info.PublishedAt.HasValue)
        {
            var local = _info.PublishedAt.Value.ToLocalTime();
            PublishedText.Text = _info.PreRelease
                ? Res.Format("Update_PublishedAtPrerelease", local.ToString("yyyy-MM-dd"))
                : Res.Format("Update_PublishedAt", local.ToString("yyyy-MM-dd"));
        }
        else
        {
            PublishedText.Visibility = Visibility.Collapsed;
        }

        ChangelogText.Text = string.IsNullOrWhiteSpace(_info.Body)
            ? Res.Get("Update_NoChangelog")
            : _info.Body.Trim();

        // Disable Open if we somehow ended up without a URL — defensive
        // against a malformed cache entry.
        OpenDownloadButton.IsEnabled = !string.IsNullOrWhiteSpace(_info.HtmlUrl);
    }

    private void OnOpenDownloadClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(_info.HtmlUrl))
                Process.Start(new ProcessStartInfo(_info.HtmlUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "UpdateAvailableDialog: cannot open {Url}", _info.HtmlUrl);
        }
        Close();
    }

    private void OnRemindLaterClick(object sender, RoutedEventArgs e) => Close();
}
