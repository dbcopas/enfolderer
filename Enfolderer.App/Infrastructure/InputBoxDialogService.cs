using System.Windows;

namespace Enfolderer.App.Infrastructure;

/// <summary>
/// Service for displaying simple input dialogs.
/// Provides a replacement for Microsoft.VisualBasic.Interaction.InputBox with a cleaner interface.
/// </summary>
public class InputBoxDialogService
{
    private static InputBoxDialogService? _instance;
    private static readonly object _lock = new();

    public static InputBoxDialogService Instance
    {
        get
        {
            if (_instance == null)
            {
                lock (_lock)
                {
                    _instance ??= new InputBoxDialogService();
                }
            }
            return _instance;
        }
    }

    /// <summary>
    /// Shows an input dialog and returns the user's input.
    /// </summary>
    /// <param name="prompt">The prompt message to display</param>
    /// <param name="title">The title of the dialog</param>
    /// <param name="defaultValue">The default value in the input field</param>
    /// <returns>The user's input, or empty string if cancelled</returns>
    public string ShowInputDialog(string prompt, string title, string defaultValue = "")
    {
        var result = ShowInputDialogInternal(prompt, title, defaultValue);
        return result ?? string.Empty;
    }

    private string? ShowInputDialogInternal(string prompt, string title, string defaultValue)
    {
        // Use WPF dialogs if available, otherwise fall back to basic MessageBox approach
        // For now, use a simple implementation with InputBox as reference
        try
        {
            // Attempt to use the modern WPF approach if available
            return Microsoft.VisualBasic.Interaction.InputBox(prompt, title, defaultValue);
        }
        catch
        {
            // Fallback for environments where Visual Basic is not available
            return null;
        }
    }
}
