using Avalonia.Controls;
using Avalonia.Input;
using PuddingAssistantDesktop.ViewModels;

namespace PuddingAssistantDesktop.Views
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            // Subscribe to KeyDown to handle Enter in chat input
            AddHandler(KeyDownEvent, OnGlobalKeyDown, handledEventsToo: false);

            // Wire the platform StorageProvider to the ViewModel so it can open folder pickers
            Loaded += (_, _) =>
            {
                if (DataContext is MainWindowViewModel vm)
                    vm.StorageProvider = StorageProvider;
            };
        }

        private void OnGlobalKeyDown(object? sender, KeyEventArgs e)
        {
            // Enter in a TextBox with Watermark containing "PuddingAssistant" → Send
            if (e.Key == Key.Enter
                && e.Source is TextBox tb
                && tb.Watermark?.Contains("PuddingAssistant") == true
                && DataContext is MainWindowViewModel vm
                && vm.SendMessageCommand.CanExecute(null))
            {
                vm.SendMessageCommand.Execute(null);
                e.Handled = true;
            }
        }
    }
}
