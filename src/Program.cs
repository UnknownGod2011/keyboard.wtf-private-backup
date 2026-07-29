namespace KeyboardWtf;

using System.Drawing;
using System.Windows.Forms;
using KeyboardWtf.Destinations;
using KeyboardWtf.Services;
using KeyboardWtf.Services.BrowserAutomationV2;

internal static class Program
{
    private static FileStream _instanceLock;

    [STAThread]
    private static void Main(string[] args)
    {
        if (BrowserNativeMessagingHost.IsNativeHostInvocation(args))
        {
            Environment.ExitCode = BrowserNativeMessagingHost.Run();
            return;
        }

        WindowsAppLifecycleService.WaitForRestartParent(args);

        if (!TryAcquireInstanceLock())
            return;

        try
        {
            ApplicationConfiguration.Initialize();
            using var notifyIcon = new NotifyIcon
            {
                Icon = SystemIcons.Application,
                Text = "keyboard.wtf - Stop typing. Say it.",
                Visible = true,
            };

            using var app = new KeyboardWtfApp(notifyIcon);
            app.Start();
            notifyIcon.ContextMenuStrip = BuildMenu(app);
            notifyIcon.DoubleClick += (_, _) => app.OpenSettings();

            Application.Run();
        }
        finally
        {
            _instanceLock?.Dispose();
            _instanceLock = null;
        }
    }

    private static bool TryAcquireInstanceLock()
    {
        try
        {
            Directory.CreateDirectory(SettingsService.AppDataDir);
            _instanceLock = new FileStream(
                Path.Combine(SettingsService.AppDataDir, "keyboard.wtf.lock"),
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static ContextMenuStrip BuildMenu(KeyboardWtfApp app)
    {
        var menu = new ContextMenuStrip();

        menu.Items.Add("Smart writing", null, (_, _) => app.Commands.ToggleSmartMode());
        menu.Items.Add("Dictation only", null, (_, _) => app.Commands.ToggleDictation());
        menu.Items.Add("Jarvis mode", null, (_, _) => app.Commands.ToggleJarvisMode());
        menu.Items.Add("Cancel current operation", null, (_, _) => app.Commands.CancelCurrent());
        menu.Items.Add(new ToolStripSeparator());

        var destinations = new ToolStripMenuItem("Default destination");
        foreach (var destination in DestinationRegistry.All)
        {
            destinations.DropDownItems.Add(destination.Name, null, (_, _) =>
            {
                app.SetDefaultDestination(destination.Name);
                RefreshDestinationChecks(destinations, app.Settings.Current.DefaultDestination);
            });
        }
        RefreshDestinationChecks(destinations, app.Settings.Current.DefaultDestination);
        menu.Items.Add(destinations);

        menu.Items.Add("Toggle AI formatting", null, (_, _) => app.ToggleAi());
        menu.Items.Add("Open voice notes", null, (_, _) => app.Commands.OpenVoiceNotes());
        menu.Items.Add("Open settings", null, (_, _) => app.OpenSettings());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Application.Exit());

        return menu;
    }

    private static void RefreshDestinationChecks(ToolStripMenuItem menu, string selected)
    {
        foreach (ToolStripItem item in menu.DropDownItems)
        {
            if (item is ToolStripMenuItem child)
                child.Checked = string.Equals(child.Text, selected, StringComparison.OrdinalIgnoreCase);
        }
    }
}
