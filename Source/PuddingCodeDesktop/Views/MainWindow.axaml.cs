using Avalonia.Controls;
using Avalonia.Input;
using PuddingCodeDesktop.ViewModels;

namespace PuddingCodeDesktop.Views
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            // Subscribe to KeyDown to handle Enter in chat input
            AddHandler(KeyDownEvent, OnGlobalKeyDown, handledEventsToo: false);
        }

        private void OnGlobalKeyDown(object? sender, KeyEventArgs e)
        {
            // Enter in a TextBox with Watermark containing "PuddingCode" → Send
            if (e.Key == Key.Enter
                && e.Source is TextBox tb
                && tb.Watermark?.Contains("PuddingCode") == true
                && DataContext is MainWindowViewModel vm
                && vm.SendMessageCommand.CanExecute(null))
            {
                vm.SendMessageCommand.Execute(null);
                e.Handled = true;
            }
        }
    }
}
