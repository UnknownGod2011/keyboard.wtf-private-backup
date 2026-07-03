using System.Drawing.Imaging;
using System.Reflection;
using KeyboardWtf.Models;
using KeyboardWtf.Services;

namespace TaskPopupRenderer;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();

        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", ".."));
        var outputDir = Path.Combine(repoRoot, "output", "task-popup");
        Directory.CreateDirectory(outputDir);

        RenderRunning(outputDir);
        RenderClarification(outputDir);
        RenderCompleted(outputDir);
    }

    private static void WithFreshForm(string outputDir, Action<JarvisTaskManager, Form> render)
    {
        using var manager = new JarvisTaskManager();
        var settings = new SettingsService(Path.Combine(outputDir, "preview-settings.json"));
        var formType = typeof(SettingsService).Assembly.GetType("KeyboardWtf.TaskPanelForm", throwOnError: true)!;
        using var form = (Form)Activator.CreateInstance(formType, BindingFlags.Public | BindingFlags.Instance, binder: null, args: [manager, settings], culture: null)!;
        render(manager, form);
    }

    private static JarvisTask CreateTask(JarvisTaskManager manager, string title, string command)
    {
        return manager.Create(
            title,
            command,
            [
                "Capture requested context",
                "Open controlled browser",
                "Inspect the page",
                "Perform safe browser actions",
                "Verify the result",
            ],
            ["Controlled browser access"]);
    }

    private static void RenderRunning(string outputDir)
    {
        WithFreshForm(outputDir, (manager, form) =>
        {
            var task = CreateTask(manager, "Running task demo", "Open the local mock form and keep the task running for overlay verification.");
            manager.SetStepByIndex(task.TaskId, 0, JarvisTaskStepStatus.Completed, "No private context was requested.");
            manager.SetStepByIndex(task.TaskId, 1, JarvisTaskStepStatus.Completed, "keyboard.wtf Browser Agent Mock Form");
            manager.SetStepByIndex(task.TaskId, 2, JarvisTaskStepStatus.Completed, "keyboard.wtf Browser Agent Mock Form · 5 interactive elements");
            manager.SetStepByIndex(task.TaskId, 3, JarvisTaskStepStatus.Running, "Waiting for the next safe browser action.");
            manager.Start(task.TaskId);
            Capture(form, Path.Combine(outputDir, "popup-running-final.png"));
        });
    }

    private static void RenderClarification(string outputDir)
    {
        WithFreshForm(outputDir, (manager, form) =>
        {
            var task = CreateTask(manager, "Clarification state demo", "Open the local mock form and demonstrate an ambiguous project choice.");
            manager.SetStepByIndex(task.TaskId, 0, JarvisTaskStepStatus.Completed, "No private context was requested.");
            manager.SetStepByIndex(task.TaskId, 1, JarvisTaskStepStatus.Completed, "keyboard.wtf Browser Agent Mock Form");
            manager.SetStepByIndex(task.TaskId, 2, JarvisTaskStepStatus.Completed, "keyboard.wtf Browser Agent Mock Form · 5 interactive elements");
            var fresh = manager.Get(task.TaskId)!;
            manager.WaitForClarification(
                fresh.TaskId,
                fresh.Steps[3].Id,
                "2 page elements matched Choose project; clarification is required.",
                ["Choose project"]);
            Capture(form, Path.Combine(outputDir, "popup-clarification-final.png"));
        });
    }

    private static void RenderCompleted(string outputDir)
    {
        WithFreshForm(outputDir, (manager, form) =>
        {
            var task = CreateTask(manager, "Safe mock form demo", "Fill the local mock form with keyboard.wtf and preview it without submitting.");
            manager.SetStepByIndex(task.TaskId, 0, JarvisTaskStepStatus.Completed, "No private context was requested.");
            manager.SetStepByIndex(task.TaskId, 1, JarvisTaskStepStatus.Completed, "keyboard.wtf Browser Agent Mock Form");
            manager.SetStepByIndex(task.TaskId, 2, JarvisTaskStepStatus.Completed, "keyboard.wtf Browser Agent Mock Form · 5 interactive elements");
            manager.SetStepByIndex(task.TaskId, 3, JarvisTaskStepStatus.Completed, "Clicked Preview safely.");
            manager.Complete(task.TaskId, "I filled the form and stopped for review.");
            Capture(form, Path.Combine(outputDir, "popup-completed-final.png"));
        });
    }

    private static void Capture(Form form, string path)
    {
        form.Show();
        Application.DoEvents();
        Thread.Sleep(200);
        Application.DoEvents();
        using var bitmap = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
        bitmap.Save(path, ImageFormat.Png);
        form.Hide();
        Application.DoEvents();
    }
}
