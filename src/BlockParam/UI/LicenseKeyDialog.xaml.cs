using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using BlockParam.Licensing;
using BlockParam.Localization;
using Serilog;

namespace BlockParam.UI;

public partial class LicenseKeyDialog : Window
{
    private readonly ILicenseService _licenseService;

    public LicenseKeyDialog(ILicenseService licenseService)
    {
        InitializeComponent();
        WindowIconHelper.SetIcon(this);
        _licenseService = licenseService;
        UpdateDisplay();

        // #20: Keep the Activate button's tooltip in sync with its disabled reason,
        // so the user isn't staring at a greyed-out button with no feedback.
        KeyInput.TextChanged += (_, _) => UpdateActivateButtonState();
        UpdateActivateButtonState();
    }

    private void UpdateActivateButtonState()
    {
        if (_licenseService.IsProActive)
        {
            ActivateButton.ToolTip = Res.Get("License_ActivateTooltip_AlreadyPro");
            return;
        }
        ActivateButton.ToolTip = string.IsNullOrWhiteSpace(KeyInput.Text)
            ? Res.Get("License_ActivateTooltip_Empty")
            : null;
    }

    private void UpdateDisplay()
    {
        var info = _licenseService.GetLicenseInfo();
        var tierName = info.Tier == LicenseTier.Pro
            ? Res.Get("License_Tier_Pro")
            : Res.Get("License_Tier_Free");

        TierText.Text = Res.Format("License_CurrentTier", tierName);

        // Reset panels
        ExpiredHintPanel.Visibility = Visibility.Collapsed;
        BuyLicensePanel.Visibility = Visibility.Collapsed;

        if (info.Tier == LicenseTier.Pro)
        {
            TierText.Foreground = new SolidColorBrush(Color.FromRgb(0, 120, 215));
            KeyInput.Text = info.LicenseKey ?? "";
            KeyInput.IsEnabled = false;
            ActivateButton.IsEnabled = false;
            RemoveButton.Visibility = Visibility.Visible;
        }
        else
        {
            TierText.Foreground = new SolidColorBrush(Color.FromRgb(100, 100, 100));
            KeyInput.Text = "";
            KeyInput.IsEnabled = true;
            ActivateButton.IsEnabled = true;
            RemoveButton.Visibility = Visibility.Collapsed;

            // Show "Buy License" button for Free tier users (S-052)
            BuyLicensePanel.Visibility = Visibility.Visible;

            // Show expired hint if the user had a Pro key that expired (S-042, S-053)
            if (!string.IsNullOrEmpty(info.LicenseKey) && !_licenseService.IsProActive)
            {
                ExpiredHintPanel.Visibility = Visibility.Visible;
                ExpiredHintText.Text = Res.Get("License_ExpiredHint");
                ManageSubscriptionRun.Text = Res.Get("License_ManageSubscription");
            }
        }
    }

    private async void OnActivateClick(object sender, RoutedEventArgs e)
    {
        var key = KeyInput.Text.Trim();
        if (string.IsNullOrWhiteSpace(key))
            return;

        ActivateButton.IsEnabled = false;
        ActivateButton.ToolTip = Res.Get("License_ActivateTooltip_Activating");
        StatusText.Text = "...";
        StatusText.Foreground = new SolidColorBrush(Colors.Gray);

        // #20: Wrap in try/finally so the button is always re-enabled — previously an
        // unexpected exception left it greyed out with no way for the user to retry.
        // Only Pro activation success should leave it disabled (that's the terminal state).
        try
        {
            var result = await _licenseService.ActivateKeyAsync(key);

            if (result.IsSuccess)
            {
                StatusText.Text = Res.Get("License_Activated");
                StatusText.Foreground = new SolidColorBrush(Color.FromRgb(46, 125, 50));
                UpdateDisplay();
                return;
            }

            StatusText.Foreground = new SolidColorBrush(Color.FromRgb(198, 40, 40));
            StatusText.Text = result.Status switch
            {
                LicenseActivationStatus.InvalidKey => Res.Get("License_Invalid"),
                LicenseActivationStatus.TooManySessions => result.ErrorMessage ?? Res.Get("License_TooManySessions"),
                LicenseActivationStatus.ServerError => Res.Get("License_ServerError"),
                _ => result.ErrorMessage ?? "Unknown error"
            };
        }
        catch (Exception ex)
        {
            StatusText.Foreground = new SolidColorBrush(Color.FromRgb(198, 40, 40));
            StatusText.Text = Res.Get("License_ServerError");
            Log.Error(ex, "License activation threw unexpected exception");
        }
        finally
        {
            // Re-enable unless the service now reports Pro (success path handled above via return)
            if (!_licenseService.IsProActive)
                ActivateButton.IsEnabled = true;
            UpdateActivateButtonState();
        }
    }

    private void OnRemoveClick(object sender, RoutedEventArgs e)
    {
        _licenseService.DeactivateKey();
        StatusText.Text = "";
        UpdateDisplay();
        UpdateActivateButtonState();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void OnBuyLicenseClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(ShopUrls.CheckoutUrl) { UseShellExecute = true });
        }
        catch
        {
            // Silently ignore if browser cannot be opened
        }
    }

    private void OnManageSubscriptionClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(ShopUrls.CustomerPortalUrl) { UseShellExecute = true });
        }
        catch
        {
            // Silently ignore if browser cannot be opened
        }
    }
}
