using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using PuddingAssistantDesktop.Models;
using PuddingAssistantDesktop.ViewModels;

namespace PuddingAssistantDesktop.Views;

/// <summary>
/// Compact chat window that opens beside the pudding spirit.
/// Supports text input, send via Enter, clipboard paste, drag to move, close,
/// and auto-scrolls to follow new/streaming messages.
/// </summary>
public partial class ChatWindow : Window
{
    private ChatEntry? _trackedEntry;
    private bool _autoScroll = true;
    private bool _scrollPending;

    public ChatWindow()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnGlobalKeyDown, handledEventsToo: false);

        // Auto-scroll when messages are added
        DataContextChanged += (_, _) =>
        {
            if (DataContext is ChatWindowViewModel vm)
            {
                vm.Messages.CollectionChanged += OnMessagesChanged;
            }
        };

        // Scroll after layout has recalculated heights — this is the key:
        // TextWrapping="Wrap" causes height changes that aren't visible until
        // layout completes. LayoutUpdated fires *after* the measure/arrange pass.
        MessageScroll.LayoutUpdated += OnScrollLayoutUpdated;

        // Detect manual scroll: if user scrolls up, stop auto-scrolling;
        // if user scrolls back to bottom, re-enable.
        MessageScroll.ScrollChanged += OnScrollChanged;
    }

    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Stop tracking the previous streaming entry
        if (_trackedEntry is not null)
        {
            _trackedEntry.PropertyChanged -= OnTrackedEntryChanged;
            _trackedEntry = null;
        }

        // Track the newest entry so we scroll during streaming too
        if (e.Action == NotifyCollectionChangedAction.Add
            && e.NewItems?[^1] is ChatEntry newEntry)
        {
            newEntry.PropertyChanged += OnTrackedEntryChanged;
            _trackedEntry = newEntry;
        }

        // New message always re-enables auto-scroll
        _autoScroll = true;
        RequestScroll();
    }

    private void OnTrackedEntryChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ChatEntry.Content) or nameof(ChatEntry.ReasoningContent))
        {
            RequestScroll();
        }
    }

    /// <summary>
    /// Marks that a scroll-to-end is needed. The actual scroll happens in
    /// <see cref="OnScrollLayoutUpdated"/> after Avalonia finishes layout.
    /// </summary>
    private void RequestScroll()
    {
        if (_autoScroll)
            _scrollPending = true;
    }

    /// <summary>
    /// Fires after every layout pass. If a scroll was requested, now the
    /// ScrollViewer's Extent reflects the true content height, so ScrollToEnd works.
    /// </summary>
    private void OnScrollLayoutUpdated(object? sender, System.EventArgs e)
    {
        if (!_scrollPending) return;
        _scrollPending = false;

        MessageScroll.ScrollToEnd();
    }

    /// <summary>
    /// Detects whether the user manually scrolled away from the bottom.
    /// If they scroll back near the bottom, re-enable auto-scroll.
    /// </summary>
    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        var sv = MessageScroll;
        var distanceFromBottom = sv.Extent.Height - sv.Viewport.Height - sv.Offset.Y;

        // Tolerance: within 30px of bottom = "at bottom"
        _autoScroll = distanceFromBottom <= 30;
    }

    // ── Title bar drag ──

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    // ── Close button ──

    private void OnCloseClicked(object? sender, RoutedEventArgs e)
    {
        Hide();
    }

    // ── Enter to send, Shift+Enter for new line ──

    private void OnGlobalKeyDown(object? sender, KeyEventArgs e)
    {
        // Enter without Shift → send message (suppress the newline insertion)
        if (e.Key == Key.Enter
            && !e.KeyModifiers.HasFlag(KeyModifiers.Shift)
            && e.Source is TextBox
            && DataContext is ChatWindowViewModel vm
            && vm.SendCommand.CanExecute(null))
        {
            vm.SendCommand.Execute(null);
            e.Handled = true;
            return;
        }

        // Shift+Enter → let the TextBox handle it (inserts newline via AcceptsReturn)

        // Escape to hide
        if (e.Key == Key.Escape)
        {
            Hide();
            e.Handled = true;
        }
    }

    private async void OnPasteClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ChatWindowViewModel vm) return;

        var clipboard = GetTopLevel(this)?.Clipboard;
        if (clipboard is null) return;

        var text = await clipboard.TryGetTextAsync();
        if (!string.IsNullOrWhiteSpace(text))
        {
            // Paste text directly into input box
            vm.InputText += text;
            return;
        }

        var formats = await clipboard.GetDataFormatsAsync();
        if (formats.Any())
        {
            vm.AddClipboardContent($"[Clipboard: {string.Join(", ", formats)}]");
        }
    }

    // ── File attachment ──

    private async void OnAttachClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ChatWindowViewModel vm) return;

        var storage = GetTopLevel(this)?.StorageProvider;
        if (storage is null) return;

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Attach files",
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType("All files") { Patterns = ["*"] },
                new FilePickerFileType("Images") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.gif", "*.bmp", "*.webp"] },
                new FilePickerFileType("Documents") { Patterns = ["*.txt", "*.md", "*.pdf", "*.docx", "*.csv", "*.json", "*.xml"] },
                new FilePickerFileType("Code") { Patterns = ["*.cs", "*.js", "*.ts", "*.py", "*.java", "*.cpp", "*.h", "*.html", "*.css"] }
            ]
        });

        foreach (var file in files)
        {
            var path = file.TryGetLocalPath();
            if (path is not null)
            {
                vm.AddAttachment(path);
            }
        }
    }
}
