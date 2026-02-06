using System;
using System.Linq;
using System.Diagnostics.CodeAnalysis;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core;
using Avalonia.Data.Core.Plugins;
using Avalonia.Markup.Xaml;
using LASTE_Mate.ViewModels;
using LASTE_Mate.Views;
using LASTE_Mate.Services;
using Microsoft.Extensions.DependencyInjection;

namespace LASTE_Mate;

public partial class App : Application
{
    private static ServiceProvider? _serviceProvider;

    public static ServiceProvider ServiceProvider => _serviceProvider ?? throw new InvalidOperationException("Service provider not initialized");

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Avoid duplicate validations from both Avalonia and the CommunityToolkit. 
            // More info: https://docs.avaloniaui.net/docs/guides/development-guides/data-validation#manage-validationplugins
            DisableAvaloniaDataAnnotationValidation();

            // Configure dependency injection
            var services = new ServiceCollection();
            ConfigureServices(services);
            _serviceProvider = services.BuildServiceProvider();

            // Resolve MainWindowViewModel from DI
            var viewModel = _serviceProvider.GetRequiredService<MainWindowViewModel>();

            desktop.MainWindow = new MainWindow
            {
                DataContext = viewModel,
            };

            // Dispose service provider when app exits
            desktop.Exit += (_, _) => _serviceProvider?.Dispose();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void ConfigureServices(IServiceCollection services)
    {
        // Register services as singletons to ensure single instance
        services.AddSingleton<DcsSocketService>();
        services.AddSingleton<DcsBiosService>();
        services.AddSingleton<AppConfigService>();

        // Register ViewModels (transient, created fresh each time but with singleton services)
        services.AddTransient<MainWindowViewModel>();
    }

    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Only enumerates Avalonia validation plugins to remove them; reflection-based access is not used elsewhere.")]
    private void DisableAvaloniaDataAnnotationValidation()
    {
        // Get an array of plugins to remove
        var dataValidationPluginsToRemove =
            BindingPlugins.DataValidators.OfType<DataAnnotationsValidationPlugin>().ToArray();

        // remove each entry found
        foreach (var plugin in dataValidationPluginsToRemove)
        {
            BindingPlugins.DataValidators.Remove(plugin);
        }
    }
}
