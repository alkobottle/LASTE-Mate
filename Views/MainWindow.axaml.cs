using System;
using System.Collections.Specialized;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LASTE_Mate.ViewModels;
using LASTE_Mate.Services;
using NLog;

namespace LASTE_Mate.Views;

public partial class MainWindow : Window
{
    private static readonly ILogger Logger = LoggingService.GetLogger<MainWindow>();
    private ScrollViewer? _debugLogScrollViewer;

    public MainWindow()
    {
        InitializeComponent();
        Closing += MainWindow_Closing;
        
        // Subscribe to debug log changes for autoscroll
        this.Loaded += MainWindow_Loaded;
    }

    private void MainWindow_Loaded(object? sender, EventArgs e)
    {
        _debugLogScrollViewer = this.FindControl<ScrollViewer>("DebugLogScrollViewer");
        
        if (DataContext is MainWindowViewModel viewModel)
        {
            viewModel.CduDebugLog.CollectionChanged += (s, args) =>
            {
                if (_debugLogScrollViewer != null && args.NewItems?.Count > 0)
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        _debugLogScrollViewer.ScrollToEnd();
                    }, DispatcherPriority.Background);
                }
            };
        }
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
            return;
        }
        
        _isClosing = true;
        Logger.Debug("Closing event fired");
        
        // Dispose resources - this will stop TCP listener and clean up
        if (DataContext is IDisposable disposable)
        {
            try
            {
                disposable.Dispose();
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Error disposing DataContext");
            }
        }
    }
}