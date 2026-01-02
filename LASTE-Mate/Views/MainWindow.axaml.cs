using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LASTE_Mate.ViewModels;

namespace LASTE_Mate.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Closing += MainWindow_Closing;
    }

    private void NumericUpDown_GotFocus(object? sender, GotFocusEventArgs e)
    {
        if (sender is NumericUpDown numericUpDown)
        {
            // Use dispatcher to select text after the control is fully focused
            Dispatcher.UIThread.Post(() =>
            {
                // Try to find TextBox through template children first
                var textBox = numericUpDown.GetTemplateChildren()
                    .OfType<TextBox>()
                    .FirstOrDefault();
                
                // Fallback: search through visual children
                if (textBox == null)
                {
                    textBox = FindTextBox(numericUpDown);
                }
                
                if (textBox != null)
                {
                    textBox.SelectAll();
                }
            }, DispatcherPriority.Input);
        }
    }

    private TextBox? FindTextBox(Control control)
    {
        // Recursively search for TextBox in the control's visual children
        if (control is TextBox tb)
        {
            return tb;
        }

        foreach (var child in control.GetVisualChildren().OfType<Control>())
        {
            var result = FindTextBox(child);
            if (result != null)
                return result;
        }

        return null;
    }

    private bool _isClosing = false;

    private void MainWindow_Closing(object? sender, WindowClosingEventArgs e)
    {
        // Prevent multiple disposal calls
        if (_isClosing)
        {
            System.Diagnostics.Debug.WriteLine("MainWindow: Already closing, skipping");
            return;
        }
        
        _isClosing = true;
        System.Diagnostics.Debug.WriteLine("MainWindow: Closing event fired");
        
        // Dispose resources first
        if (DataContext is IDisposable disposable)
        {
            System.Diagnostics.Debug.WriteLine("MainWindow: Disposing DataContext");
            try
            {
                disposable.Dispose();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"MainWindow: Error disposing DataContext: {ex.Message}");
            }
            System.Diagnostics.Debug.WriteLine("MainWindow: DataContext disposed");
        }

        // Give a moment for cleanup (especially for TCP server to release port)
        System.Threading.Thread.Sleep(200);

        // Ensure app process terminates (prevents zombie listener)
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            System.Diagnostics.Debug.WriteLine("MainWindow: Shutting down application");
            try
            {
                desktop.Shutdown();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"MainWindow: Error shutting down: {ex.Message}");
            }
        }
    }
}