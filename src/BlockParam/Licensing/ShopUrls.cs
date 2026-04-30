namespace BlockParam.Licensing;

/// <summary>
/// Central configuration for all shop and licensing URLs.
/// </summary>
public static class ShopUrls
{
    /// <summary>LemonSqueezy checkout URL for purchasing a Pro license.</summary>
    public const string CheckoutUrl = "https://blockparam.lemonsqueezy.com/buy";

    /// <summary>
    /// LemonSqueezy customer portal for managing subscriptions, payment methods, and invoices.
    /// </summary>
    public const string CustomerPortalUrl = "https://app.lemonsqueezy.com/my-orders";

    /// <summary>
    /// Landing page with product info, Free vs Pro comparison, and purchase button.
    /// </summary>
    public const string LandingPageUrl = "https://lautimweb.de/blockparam";

    /// <summary>
    /// Success page shown after checkout with license key and activation instructions.
    /// </summary>
    public const string SuccessPageUrl = "https://lautimweb.de/blockparam/success";
}
