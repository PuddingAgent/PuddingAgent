using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System.Runtime.InteropServices.WindowsRuntime;
using PuddingDesktop.Foundation;

namespace PuddingDesktop;

public sealed partial class MainWindow
{
    private bool _buildingSettings;
    private string _settingsCategoryId = "general";
    private readonly Dictionary<string, string> _settingsTabIds = [];
    private Control? _settingsReturnFocus;
    private bool _settingsOpen;

    private void InitializeSettingsNavigation() => FilterSettings();

    private void OnSettingsSearchChanged(object sender, TextChangedEventArgs args)
    {
        if (_loaded) FilterSettings();
    }

    private void FilterSettings()
    {
        _buildingSettings = true;
        try
        {
            var categories = SettingsCatalog.Search(SettingsSearch.Text);
            SettingsNavigation.MenuItems.Clear();
            foreach (var group in categories.GroupBy(category => category.Group))
            {
                SettingsNavigation.MenuItems.Add(new NavigationViewItemHeader { Content = group.Key });
                foreach (var category in group)
                {
                    var item = new NavigationViewItem
                    {
                        Content = category.Title, Tag = category,
                        Icon = new FontIcon { Glyph = char.ConvertFromUtf32(Convert.ToInt32(category.Glyph, 16)) }
                    };
                    AutomationProperties.SetName(item, category.Title);
                    SettingsNavigation.MenuItems.Add(item);
                }
            }
            var items = SettingsNavigation.MenuItems.OfType<NavigationViewItem>().ToArray();
            var selected = items.FirstOrDefault(item => ((SettingsCategory)item.Tag).Id == _settingsCategoryId)
                ?? items.FirstOrDefault();
            SettingsNavigation.SelectedItem = selected;
            ShowSettingsCategory(selected?.Tag as SettingsCategory);
        }
        finally { _buildingSettings = false; }
    }

    private void OnSettingsCategoryChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (!_buildingSettings && ReferenceEquals(sender.SelectedItem, args.SelectedItem)
            && args.SelectedItem is NavigationViewItem { Tag: SettingsCategory category }
            && sender.MenuItems.Contains(args.SelectedItem))
            ShowSettingsCategory(category);
    }

    private void ShowSettingsCategory(SettingsCategory? category)
    {
        SettingsTabs.TabItems.Clear();
        AppearanceSettings.Visibility = Visibility.Collapsed;
        SettingsEmpty.Visibility = category is null ? Visibility.Visible : Visibility.Collapsed;
        SettingsCategoryTitle.Text = category?.Title ?? "搜索设置";
        SettingsCategoryDescription.Text = category?.Description ?? "";
        if (category is null) return;
        _settingsCategoryId = category.Id;
        var previous = _settingsTabIds.GetValueOrDefault(category.Id);
        foreach (var tab in category.Tabs)
        {
            var cards = new StackPanel { Spacing = 14, Margin = new Thickness(0, 16, 8, 16) };
            if (tab.Id != "appearance" || category.Id != "general")
                foreach (var card in tab.Cards) cards.Children.Add(CreateSettingsCard(card));
            SettingsTabs.TabItems.Add(new TabViewItem
            {
                Header = tab.Title, Tag = tab.Id, IsClosable = false,
                Content = new ScrollViewer { Content = cards, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }
            });
        }
        SettingsTabs.SelectedItem = SettingsTabs.TabItems.OfType<TabViewItem>().FirstOrDefault(tab => (string)tab.Tag == previous)
            ?? SettingsTabs.TabItems[0];
        RefreshSettingsTab();
    }

    private FrameworkElement CreateSettingsCard(SettingsCard card)
    {
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = card.Title, FontSize = 17, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = card.Description, TextWrapping = TextWrapping.Wrap, Opacity = .75 });
        panel.Children.Add(new TextBlock { Text = card.Status == "部分已有" ? "基础功能已在工作台提供 · 此卡片待接入" : card.Status,
            FontSize = 12, TextWrapping = TextWrapping.Wrap });
        var action = new Button
        {
            Content = card.Id == "local-kernel" ? "打开运行中心" : "功能迁移中",
            IsEnabled = card.Id == "local-kernel", HorizontalAlignment = HorizontalAlignment.Left
        };
        AutomationProperties.SetName(action, card.Title + " · " + action.Content);
        if (card.Id == "local-kernel") action.Click += OnRuntime;
        panel.Children.Add(action);
        // XAML owns the theme brushes so existing cards also update when the theme changes.
        var border = (Border)Microsoft.UI.Xaml.Markup.XamlReader.Load(
            "<Border xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' " +
            "Background='{ThemeResource CardBackground}' BorderBrush='{ThemeResource DividerBrush}' " +
            "BorderThickness='1' CornerRadius='12' Padding='22' />");
        border.Child = panel;
        return border;
    }

    private void OnSettingsTabChanged(object sender, SelectionChangedEventArgs args) => RefreshSettingsTab();

    private void RefreshSettingsTab()
    {
        if (SettingsTabs.SelectedItem is not TabViewItem { Tag: string tab }) return;
        _settingsTabIds[_settingsCategoryId] = tab;
        AppearanceSettings.Visibility = _settingsCategoryId == "general" && tab == "appearance"
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateSettingsOverlay()
    {
        var open = _state.Page == ShellPage.Settings;
        if (open == _settingsOpen) return;
        _settingsOpen = open;
        if (open)
            _settingsReturnFocus = FocusManager.GetFocusedElement(Root.XamlRoot) as Control;
        ShellInteractionHost.IsEnabled = !open;
        if (open) SettingsSearch.Focus(FocusState.Programmatic);
        else if (_settingsReturnFocus is { IsEnabled: true } control) control.Focus(FocusState.Programmatic);
    }

    private void OnSettingsKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key != Windows.System.VirtualKey.Escape) return;
        _state.Navigate(ShellPage.Workbench);
        args.Handled = true;
    }

    private void OpenSettingsCategory(string category, string? tab = null)
    {
        _settingsCategoryId = category;
        if (tab is not null) _settingsTabIds[category] = tab;
        SettingsSearch.Text = "";
        FilterSettings();
        _state.Navigate(ShellPage.Settings);
    }

    private void OnAppearanceSettings(object sender, RoutedEventArgs args) => OpenSettingsCategory("general", "appearance");
    private void OnMemorySettings(object sender, RoutedEventArgs args) => OpenSettingsCategory("memory");

    private async Task SaveSettingsSmokeImageAsync(string path)
    {
        Root.UpdateLayout();
        await Task.Delay(350); // Allow native selection/entrance animations to finish before capture.
        var bitmap = new Microsoft.UI.Xaml.Media.Imaging.RenderTargetBitmap();
        await bitmap.RenderAsync(Root);
        var pixels = await bitmap.GetPixelsAsync();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var file = File.Create(path);
        var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(
            Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, file.AsRandomAccessStream());
        encoder.SetPixelData(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
            Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
            (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, pixels.ToArray());
        await encoder.FlushAsync();
    }

    private static async Task WaitForSettingsUiAsync(Func<bool> ready)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!ready()) await Task.Delay(20, timeout.Token);
    }
}
