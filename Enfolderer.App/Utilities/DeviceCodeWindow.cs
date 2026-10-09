using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Enfolderer.App.Utilities;

/// <summary>
/// Shows a device code and opens the sign-in page. Built in code rather than XAML because it is a
/// single transient dialog with no bindings, and deliberately modeless: the identity library starts
/// polling as soon as the prompt callback returns, so a modal dialog would stall the very sign-in
/// it is describing.
/// </summary>
internal sealed class DeviceCodeWindow : Window
{
    private DeviceCodeWindow(DeviceCodeDetails details)
    {
        Title = "Sign in";
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        // Selectable rather than a label: the whole point of this window is that the code and the
        // URL can be copied, which a MessageBox does not allow.
        var code = new TextBox
        {
            Text = details.UserCode,
            IsReadOnly = true,
            FontSize = 24,
            FontFamily = new FontFamily("Consolas"),
            TextAlignment = TextAlignment.Center,
            Padding = new Thickness(8),
            Margin = new Thickness(0, 4, 0, 12)
        };
        code.GotFocus += (_, _) => code.SelectAll();

        var url = new TextBox
        {
            Text = details.VerificationUri,
            IsReadOnly = true,
            Margin = new Thickness(0, 4, 0, 12)
        };

        var copyButton = new Button { Content = "Copy code", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 8, 0) };
        copyButton.Click += (_, _) => TrySetClipboard(details.UserCode, copyButton);

        var openButton = new Button { Content = "Open sign-in page", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 8, 0) };
        openButton.Click += (_, _) => TryOpenBrowser(details.VerificationUri);

        var closeButton = new Button { Content = "Close", Padding = new Thickness(12, 4, 12, 4), IsCancel = true };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(copyButton);
        buttons.Children.Add(openButton);
        buttons.Children.Add(closeButton);

        var panel = new StackPanel { Margin = new Thickness(16), MaxWidth = 420 };
        panel.Children.Add(new TextBlock
        {
            Text = "Your browser should have opened. Enter this code to sign in:",
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(code);
        panel.Children.Add(new TextBlock { Text = "Sign-in page:", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(url);
        panel.Children.Add(new TextBlock
        {
            Text = "This window closes by itself once you have signed in.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.7,
            Margin = new Thickness(0, 0, 0, 12)
        });
        panel.Children.Add(buttons);

        Content = panel;
        code.Focus();
    }

    /// <summary>
    /// Shows the prompt and opens the sign-in page, returning a handle that closes it again. Must
    /// be called from the UI thread.
    /// </summary>
    public static IDisposable Show(Window? owner, DeviceCodeDetails details)
    {
        var window = new DeviceCodeWindow(details);
        if (owner is not null && owner.IsLoaded) window.Owner = owner;
        window.Show();

        TryOpenBrowser(details.VerificationUri);
        TrySetClipboard(details.UserCode, null);

        return new Dismisser(window);
    }

    private static void TryOpenBrowser(string uri)
    {
        // A failure here is not worth interrupting sign-in for: the URL is on screen and copyable.
        try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); }
        catch (Exception ex) { Debug.WriteLine($"Could not open the sign-in page: {ex.Message}"); }
    }

    private static void TrySetClipboard(string text, Button? feedbackOn)
    {
        // The clipboard can be locked by another process, and that must not break sign-in.
        try
        {
            Clipboard.SetText(text);
            if (feedbackOn is not null) feedbackOn.Content = "Copied";
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Could not copy the device code: {ex.Message}");
            if (feedbackOn is not null) feedbackOn.Content = "Copy failed";
        }
    }

    private sealed class Dismisser : IDisposable
    {
        private readonly DeviceCodeWindow _window;
        private bool _disposed;

        public Dismisser(DeviceCodeWindow window) => _window = window;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            // Disposal happens on whichever thread finished the token request, not necessarily the
            // UI thread.
            _window.Dispatcher.Invoke(_window.Close);
        }
    }
}
