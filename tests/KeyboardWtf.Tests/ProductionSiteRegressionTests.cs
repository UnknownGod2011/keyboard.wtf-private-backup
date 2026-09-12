namespace KeyboardWtf.Tests;

using Xunit;

public sealed class ProductionSiteRegressionTests
{
    [Theory]
    [InlineData("src/Resources/web/settings.html")]
    [InlineData("production/site/support.html")]
    [InlineData("production/site/privacy.html")]
    [InlineData("production/site/refunds.html")]
    [InlineData("production/site/terms.html")]
    [InlineData("site/support.html")]
    [InlineData("site/privacy.html")]
    [InlineData("staging/site/support.html")]
    [InlineData("staging/site/privacy.html")]
    [InlineData("extensions/keyboard-wtf-browser-helper-v2/store/STORE_LISTING.md")]
    [InlineData("extensions/keyboard-wtf-browser-helper-v2/store/PRIVACY_POLICY_PUBLIC_DRAFT.md")]
    [InlineData("extensions/keyboard-wtf-browser-helper-v2/store/SUPPORT_AND_TROUBLESHOOTING.md")]
    public void Customer_facing_support_uses_only_the_canonical_support_address(string relativePath)
    {
        var content = File.ReadAllText(Path.Combine(FindRepositoryRoot(), relativePath));
        Assert.Contains("support@keyboardwtf.com", content, StringComparison.Ordinal);
        Assert.DoesNotContain("keyboard.wtf@gmail.com", content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tanushshah2006@gmail.com", content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Account_history_uses_bounded_scroll_regions_and_cursor_loading()
    {
        var root = FindRepositoryRoot();
        var html = File.ReadAllText(Path.Combine(root, "production", "site", "account.html"));
        var css = File.ReadAllText(Path.Combine(root, "production", "site", "styles.css"));
        var script = File.ReadAllText(Path.Combine(root, "production", "site", "account.js"));

        Assert.Contains("account-history-list", html, StringComparison.Ordinal);
        Assert.Contains("max-height: 430px", css, StringComparison.Ordinal);
        Assert.Contains("overflow-y: auto", css, StringComparison.Ordinal);
        Assert.Contains("loadUsagePage(usageCursor)", script, StringComparison.Ordinal);
        Assert.Contains("loadPurchasePage(purchaseCursor)", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Authoritative_usage_categories_have_clear_labels_in_settings_and_account()
    {
        var root = FindRepositoryRoot();
        var settings = File.ReadAllText(Path.Combine(root, "src", "Resources", "web", "settings.html"));
        var account = File.ReadAllText(Path.Combine(root, "production", "site", "account.js"));

        Assert.Contains("smart_writing: 'Smart Writing'", settings, StringComparison.Ordinal);
        Assert.Contains("selected_text: 'Selected text'", settings, StringComparison.Ordinal);
        Assert.Contains("smart_writing: \"Smart Writing\"", account, StringComparison.Ordinal);
        Assert.Contains("selected_text: \"Selected text\"", account, StringComparison.Ordinal);
    }

    [Fact]
    public void Backend_auth_rejection_clears_rendered_account_data()
    {
        var root = FindRepositoryRoot();
        var script = File.ReadAllText(Path.Combine(root, "production", "site", "account.js"));
        var rejection = script.IndexOf("error.code === \"backend-auth-rejected\"", StringComparison.Ordinal);
        var clear = script.IndexOf("clearAccountUi();", rejection, StringComparison.Ordinal);

        Assert.True(rejection >= 0);
        Assert.True(clear > rejection);
    }

    [Fact]
    public void Settings_displays_live_registered_hotkeys_and_marks_unavailable_bindings()
    {
        var root = FindRepositoryRoot();
        var html = File.ReadAllText(Path.Combine(root, "src", "Resources", "web", "settings.html"));

        Assert.Contains("Active in Windows:", html, StringComparison.Ordinal);
        Assert.Contains("Unavailable - choose another", html, StringComparison.Ordinal);
        Assert.Contains("input.value = activeValue", html, StringComparison.Ordinal);
        Assert.DoesNotContain("2[0-4]", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Pricing_waits_for_existing_auth_before_rendering_the_account_appropriate_cta()
    {
        var root = FindRepositoryRoot();
        var html = File.ReadAllText(Path.Combine(root, "production", "site", "pricing.html"));
        var script = File.ReadAllText(Path.Combine(root, "production", "site", "pricing.js"));

        Assert.Contains("Checking plans and account", html, StringComparison.Ordinal);
        Assert.Contains("waitForAuthInitialization()", script, StringComparison.Ordinal);
        Assert.Contains("const account = user ? await loadAccount() : null", script, StringComparison.Ordinal);
        Assert.Contains("Sign in to subscribe", script, StringComparison.Ordinal);
        Assert.Contains("Subscribe from Account", script, StringComparison.Ordinal);
        Assert.Contains("Manage current plan", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Pricing_card_notice_and_faq_share_one_compact_aligned_width()
    {
        var root = FindRepositoryRoot();
        var pricing = File.ReadAllText(Path.Combine(root, "production", "site", "pricing.html"));
        var reviews = File.ReadAllText(Path.Combine(root, "production", "site", "reviews.html"));
        var css = File.ReadAllText(Path.Combine(root, "production", "site", "styles.css"));

        Assert.Contains("class=\"shell pricing-shell\"", pricing, StringComparison.Ordinal);
        Assert.Contains("class=\"shell faq-shell\"", pricing, StringComparison.Ordinal);
        Assert.DoesNotContain("faq-list", reviews, StringComparison.Ordinal);
        Assert.Contains(".pricing-shell { max-width: 760px; }", css, StringComparison.Ordinal);
        Assert.Contains(".faq-shell { max-width: 760px; }", css, StringComparison.Ordinal);
        Assert.Contains(".pricing-status.status { width: 100%;", css, StringComparison.Ordinal);
        Assert.Contains(".pricing-status.status { padding-inline: 24px; }", css, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "KeyboardWtf.sln")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not find the keyboard.wtf repository root.");
    }
}
