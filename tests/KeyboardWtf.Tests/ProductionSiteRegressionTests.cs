namespace KeyboardWtf.Tests;

using Xunit;

public sealed class ProductionSiteRegressionTests
{
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
