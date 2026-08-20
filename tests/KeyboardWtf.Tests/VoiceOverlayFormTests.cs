namespace KeyboardWtf.Tests;

using KeyboardWtf.Models;
using KeyboardWtf.Services;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Xunit;

public sealed class VoiceOverlayFormTests
{
    [Fact]
    public void BackgroundStatusUpdatesReuseTheUiThreadWindowHandle()
    {
        Exception failure = null;
        var completed = new ManualResetEventSlim();
        var uiThread = new Thread(() =>
        {
            try
            {
                using var recorder = new AudioRecorderService();
                using var overlay = new VoiceOverlayForm(recorder);
                overlay.InitializeUiThreadHandle();
                var originalHandle = overlay.Handle;
                var uiThreadId = GetCurrentThreadId();
                Assert.Equal(uiThreadId, GetWindowThreadProcessId(originalHandle, IntPtr.Zero));

                var published = new ManualResetEventSlim();
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    for (var index = 0; index < 250; index++)
                    {
                        KeyboardWtfState.SetUi(
                            index % 2 == 0 ? VoiceUiPhase.Listening : VoiceUiPhase.Thinking,
                            "Jarvis",
                            $"background update {index}");
                    }
                    published.Set();
                });

                var deadline = DateTime.UtcNow.AddSeconds(5);
                while ((!published.IsSet || !overlay.Visible) && DateTime.UtcNow < deadline)
                {
                    Application.DoEvents();
                    Thread.Sleep(5);
                }

                Application.DoEvents();
                Assert.True(published.IsSet);
                Assert.True(overlay.Visible);
                Assert.True(overlay.IsHandleCreated);
                Assert.Equal(originalHandle, overlay.Handle);
                Assert.Equal(uiThreadId, GetWindowThreadProcessId(overlay.Handle, IntPtr.Zero));
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                KeyboardWtfState.SetUi(VoiceUiPhase.Idle, "keyboard.wtf", "Ready");
                completed.Set();
            }
        });

        uiThread.SetApartmentState(ApartmentState.STA);
        uiThread.Start();
        Assert.True(completed.Wait(TimeSpan.FromSeconds(10)));
        uiThread.Join();
        Assert.Null(failure);
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, IntPtr processId);
}
