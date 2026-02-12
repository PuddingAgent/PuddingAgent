using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core;
using Avalonia.Data.Core.Plugins;
using Avalonia.Markup.Xaml;
using PuddingAssistantDesktop.Heartbeat;
using PuddingAssistantDesktop.ViewModels;
using PuddingAssistantDesktop.Views;
using System.Linq;

namespace PuddingAssistantDesktop
{
    public partial class App : Application
    {
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

                // Don't create MainWindow at startup — only the spirit is visible.
                // MainWindow is lazily created when the user requests it via right-click menu.
                desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnExplicitShutdown;

                // Launch the desktop pudding spirit with a shared heartbeat coordinator
                var heartbeat = new HeartbeatCoordinator();
                var spiritVm = new SpiritViewModel(heartbeat);
                var spiritWindow = new SpiritWindow
                {
                    DataContext = spiritVm,
                };
                spiritWindow.Show();
            }

            base.OnFrameworkInitializationCompleted();
        }

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
}