using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace Enfolderer.App.Tests;

public static class MainMenuTests
{
    public static int RunAll()
    {
        var failures = 0;
        void Check(bool condition, string message)
        {
            if (condition) return;
            failures++;
            Console.Error.WriteLine("Main menu: " + message);
        }

        var application = Application.Current;
        var originalMainWindow = application.MainWindow;
        var originalShutdownMode = application.ShutdownMode;
        var originalWindowCount = application.Windows.Count;
        application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        MainWindow? window = null;
        try
        {
        window = new MainWindow();
        var panel = (DockPanel)window.Content;
        var bar = panel.Children.OfType<DockPanel>().Single();
        var menu = bar.Children.OfType<Menu>().Single();
        var toggle = (CheckBox)window.FindName("AdvancedToggle");
        var top = menu.Items.OfType<MenuItem>().ToArray();
        Check(top.Select(item => item.Header.ToString()).SequenceEqual(
            new[] { "_File", "_View", "_Scan", "_Tools", "_Help" }), "top-level menu structure");
        Check(top[0].Items.Count == 1 && ((MenuItem)top[0].Items[0]).Header.ToString() == "E_xit",
            "File contains only Exit");
        Check(top[1].Items.OfType<MenuItem>().Count() == 5, "View retains layouts and binder navigation");
        Check(top[2].Items.Count == 2 && ((MenuItem)top[2].Items[1]).Header.ToString()!.Contains("Multiple Images"),
            "Scan exposes single-image and folder scans");
        Check(top[4].Items.Count == 1, "Help exposes the project link");
        Check(window.InputBindings.OfType<KeyBinding>().Any(binding =>
            binding.Key == Key.F && binding.Modifiers == ModifierKeys.Control
            && binding.Command == ApplicationCommands.Find), "Ctrl+F remains available");

        var tools = top[3].Items.OfType<MenuItem>().ToArray();
        Check(tools.Length == 5, "Tools retains viewers and three advanced groups");
        Check(toggle.IsChecked == false, "Advanced starts off");
        foreach (var enabled in new[] { false, true, false })
        {
            toggle.IsChecked = enabled;
            window.Measure(new Size(1400, 900));
            window.Arrange(new Rect(0, 0, 1400, 900));
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Check(tools.Take(2).All(item => item.Visibility == Visibility.Visible),
                "viewers stay accessible regardless of Advanced");
            Check(tools.Skip(2).All(item => item.Visibility ==
                (enabled ? Visibility.Visible : Visibility.Collapsed)),
                $"advanced groups follow the toggle ({enabled})");
        }
        // Do not show the window or invoke actions that access files, services or the browser.
        }
        finally
        {
            window?.Close();
            application.MainWindow = originalMainWindow;
            application.ShutdownMode = originalShutdownMode;
        }
        Check(application.Windows.Count == originalWindowCount, "menu tests leave no hidden windows alive");
        Check(application.MainWindow == originalMainWindow && application.ShutdownMode == originalShutdownMode,
            "menu tests preserve application lifetime settings");
        return failures;
    }
}
