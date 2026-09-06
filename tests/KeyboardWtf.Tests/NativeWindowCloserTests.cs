using KeyboardWtf.Services;
using Xunit;
using Window = KeyboardWtf.Services.NativeWindowCloser.Window;

namespace KeyboardWtf.Tests;

public sealed class NativeWindowCloserTests
{
    [Theory]
    [InlineData("File Explorer")]
    [InlineData("explorer")]
    [InlineData("Windows Explorer")]
    [InlineData("explorer.exe")]
    public void Explorer_identity_excludes_shell_dialogs_and_browser_titles(string target)
    {
        Window[] windows = [Explorer(1), Explorer(2), Make(3, "explorer", "Program Manager", "Progman"),
            Make(4, "explorer", "Desktop", "WorkerW"), Make(5, "explorer", "Copying", "#32770"),
            Make(6, "chrome", "File Explorer - Search"), Make(7, "msedge", "File Explorer")];
        Assert.Equal(new long[] { 1, 2 }, NativeWindowCloser.Select(windows, target, true, null, (IntPtr)6)
            .Select(w => w.Handle.ToInt64()));
    }

    [Fact]
    public void Named_native_app_never_resolves_a_browser_tab_title()
    {
        Window[] windows = [Make(1, "chrome", "Editor"), Make(2, "editor", "notes")];
        Assert.Equal((IntPtr)2, Assert.Single(NativeWindowCloser.Select(windows, "editor", false, null, (IntPtr)1)).Handle);
        Assert.DoesNotContain(NativeWindowCloser.Select(windows, "Editor", false, null, (IntPtr)1), w => w.IsBrowser);
        Assert.Empty(NativeWindowCloser.Select([windows[0]], "Editor", false, null, (IntPtr)1));
    }

    [Fact]
    public void A_mismatched_window_id_cannot_override_the_explicit_native_app_identity()
    {
        Window[] windows = [Explorer(1), Make(2, "chrome", "Untouched")];
        Assert.Empty(NativeWindowCloser.Select(windows, "File Explorer", true, "2", (IntPtr)2));
    }

    [Fact]
    public void All_requires_a_named_target_and_one_does_not_silently_choose_among_matches()
    {
        Window[] windows = [Explorer(1), Explorer(2)];
        Assert.Empty(NativeWindowCloser.Select(windows, "", true, null, (IntPtr)1));
        Assert.Equal(2, NativeWindowCloser.Select(windows, "File Explorer", false, null, (IntPtr)1).Length);
        Assert.Equal((IntPtr)2, Assert.Single(NativeWindowCloser.Select(windows, "File Explorer", false, "2", (IntPtr)1)).Handle);
    }

    [Fact]
    public async Task Close_targets_only_snapshot_handles_even_when_user_switches_or_opens_windows()
    {
        var windows = new[] { Explorer(1), Explorer(2), Make(3, "chrome", "Untouched"), Make(4, "msedge", "Untouched") }
            .ToDictionary(w => w.Handle);
        var targets = NativeWindowCloser.Select(windows.Values.ToArray(), "File Explorer", true, null, (IntPtr)3);
        var sent = new List<IntPtr>();
        var result = await NativeWindowCloser.CloseAsync(targets, CancellationToken.None,
            id => windows.GetValueOrDefault(id), id =>
            {
                sent.Add(id);
                windows.Remove(id);
                windows[(IntPtr)5] = Explorer(5); // New window is not owned by this request.
                return true;
            });
        Assert.True(result.Succeeded);
        Assert.Equal(new IntPtr[] { (IntPtr)1, (IntPtr)2 }, sent);
        Assert.Equal(new long[] { 3, 4, 5 }, windows.Keys.Select(id => id.ToInt64()).Order());
    }

    [Fact]
    public async Task Manual_close_and_recycled_handle_never_target_the_replacement()
    {
        var windows = new[] { Explorer(1), Explorer(2), Explorer(3) };
        var sent = new List<IntPtr>();
        var result = await NativeWindowCloser.CloseAsync(windows, CancellationToken.None,
            id => id == (IntPtr)2 ? Make(2, "chrome", "Replacement") with { ProcessId = 999 } : null,
            id => { sent.Add(id); return true; });
        Assert.True(result.Succeeded);
        Assert.Empty(sent);
        Assert.Equal(0, result.Requested);
    }

    [Fact]
    public async Task A_posted_close_is_not_reported_as_success_until_the_window_disappears()
    {
        var target = Explorer(1);
        var result = await NativeWindowCloser.CloseAsync([target], CancellationToken.None,
            _ => target, _ => true, TimeSpan.Zero);
        Assert.False(result.Succeeded);
        Assert.Equal(target, Assert.Single(result.Remaining));
    }

    [Fact]
    public async Task Cancellation_stops_the_rest_of_the_snapshot_without_queued_closes()
    {
        using var cancellation = new CancellationTokenSource();
        var sent = new List<IntPtr>();
        var targets = new[] { Explorer(1), Explorer(2) };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NativeWindowCloser.CloseAsync(targets, cancellation.Token,
            id => targets.Single(w => w.Handle == id), id => { sent.Add(id); cancellation.Cancel(); return true; }));
        Assert.Equal((IntPtr)1, Assert.Single(sent));
    }

    private static Window Explorer(int id) => Make(id, "explorer", $"Folder {id}", "CabinetWClass");
    private static Window Make(int id, string process, string title, string className = "AppWindow") =>
        new((IntPtr)id, (uint)id, (uint)id, className, process, title);
}
