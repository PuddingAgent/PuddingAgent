using System;
using System.Linq;
using System.Net.Http;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using PuddingAssistant.Abstractions;
using PuddingAssistant.Core;
using PuddingAssistant.Models;
using PuddingAssistant.Skills;
using PuddingAssistant.Skills.BuiltIn;
using PuddingAssistantDesktop.Heartbeat;
using PuddingAssistantDesktop.Models;
using PuddingAssistantDesktop.Physics;
using PuddingAssistantDesktop.ViewModels;

namespace PuddingAssistantDesktop.Views;

/// <summary>
/// Transparent, borderless, topmost window hosting the pudding spirit.
/// Physics is driven by the <see cref="HeartbeatCoordinator"/>'s physics beat.
/// Supports multi-monitor virtual desktop bounds.
/// </summary>
public partial class SpiritWindow : Window
{
    private bool _isDragging;
    private Point _dragStart;
    private ChatWindow? _chatWindow;
    private MainWindow? _mainWindow;

    // ── Physics ──

    private PhysicsWorld? _physics;
    private PhysicsBody? _body;

    /// <summary>Tracks previous position during drag to compute release velocity.</summary>
    private PixelPoint _prevDragPosition;
    private DateTime _prevDragTime;

    public SpiritWindow()
    {
        InitializeComponent();

        // Position near bottom-right of primary screen
        Opened += OnOpened;

        // Double-tap triggers poke/startle reaction
        DoubleTapped += OnSpiritDoubleTapped;
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        var screen = Screens.Primary;
        if (screen is not null)
        {
            var workArea = screen.WorkingArea;
            Position = new PixelPoint(
                workArea.Right - (int)Width - 60,
                workArea.Bottom - (int)Height - 20);
        }

        InitializePhysics();
    }

    // ── Physics initialization ──

    /// <summary>
    /// Computes the virtual desktop bounding box across all monitors,
    /// initializes the physics world, and subscribes to the heartbeat's physics beat.
    /// </summary>
    private void InitializePhysics()
    {
        // Compute virtual desktop bounds spanning all screens
        var allScreens = Screens.All.ToArray();
        if (allScreens.Length == 0) return;

        var virtualLeft = double.MaxValue;
        var virtualTop = double.MaxValue;
        var virtualRight = double.MinValue;
        var virtualBottom = double.MinValue;

        foreach (var screen in allScreens)
        {
            var work = screen.WorkingArea;
            var bounds = screen.Bounds;

            virtualLeft = Math.Min(virtualLeft, bounds.X);
            virtualTop = Math.Min(virtualTop, bounds.Y);
            virtualRight = Math.Max(virtualRight, bounds.Right);
            virtualBottom = Math.Max(virtualBottom, work.Bottom);
        }

        _body = new PhysicsBody(Position.X, Position.Y);
        _physics = new PhysicsWorld(_body);

        var handle = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        _physics.Configure(virtualLeft, virtualTop, virtualRight, virtualBottom, handle);
        _physics.SetWindowSize(Width, Height);
        _physics.RefreshPlatforms();
        _physics.OnLanded += OnPhysicsLanded;

        // Subscribe physics tick to the heartbeat coordinator instead of a standalone timer
        if (DataContext is SpiritViewModel vm)
        {
            vm.Heartbeat.PhysicsBeat += OnPhysicsBeat;
            vm.Heartbeat.Start();
        }
    }

    private void OnPhysicsBeat(double dt)
    {
        if (_physics is null || _body is null) return;

        var vm = DataContext as SpiritViewModel;

        // Sync drag state
        _body.IsDragged = _isDragging;

        if (_isDragging)
        {
            _body.Teleport(Position.X, Position.Y);
            return;
        }

        // Enter falling state when not grounded and moving down
        if (vm is not null && !_body.IsGrounded && _body.VelocityY > 30)
        {
            vm.EnterFalling();
        }

        // Step physics
        _physics.Update(dt);

        // Sync physics squash to ViewModel
        if (vm is not null)
        {
            vm.SetPhysicsSquash(_body.SquashY, _body.StretchX);
        }

        // Update window position from physics body
        Position = new PixelPoint((int)_body.X, (int)_body.Y);
    }

    private void OnPhysicsLanded()
    {
        if (DataContext is SpiritViewModel vm)
        {
            vm.ExitFalling();
        }
    }

    // ── Drag to move (with throw velocity) ──

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        var props = e.GetCurrentPoint(this).Properties;

        if (props.IsRightButtonPressed)
        {
            ShowSpiritContextMenu(e);
            e.Handled = true;
            return;
        }

        if (props.IsLeftButtonPressed)
        {
            _isDragging = true;
            _dragStart = e.GetPosition(this);
            _prevDragPosition = Position;
            _prevDragTime = DateTime.UtcNow;
            e.Pointer.Capture(this);

            if (DataContext is SpiritViewModel vm)
            {
                vm.IsDragging = true;
                vm.TouchInteraction();
            }
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        if (_isDragging)
        {
            // Track velocity for throw
            _prevDragPosition = Position;
            _prevDragTime = DateTime.UtcNow;

            var currentPos = e.GetPosition(this);
            var delta = currentPos - _dragStart;
            Position = new PixelPoint(
                Position.X + (int)delta.X,
                Position.Y + (int)delta.Y);
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        if (_isDragging)
        {
            _isDragging = false;
            e.Pointer.Capture(null);

            // Compute release velocity for throw effect
            if (_body is not null)
            {
                var elapsed = (DateTime.UtcNow - _prevDragTime).TotalSeconds;
                if (elapsed > 0.001 && elapsed < 0.2)
                {
                    var vx = (Position.X - _prevDragPosition.X) / elapsed;
                    var vy = (Position.Y - _prevDragPosition.Y) / elapsed;

                    // Clamp throw speed
                    var maxThrow = 600.0;
                    vx = Math.Clamp(vx, -maxThrow, maxThrow);
                    vy = Math.Clamp(vy, -maxThrow, maxThrow);

                    _body.Teleport(Position.X, Position.Y);
                    _body.SetVelocity(vx / 60.0, vy / 60.0);
                    _body.Detach();
                }
                else
                {
                    // Gentle drop if released without momentum
                    _body.Teleport(Position.X, Position.Y);
                    _body.SetVelocity(0, 0);
                    _body.Detach();
                }
            }

            if (DataContext is SpiritViewModel vm)
                vm.IsDragging = false;
        }
    }

    // ── Hover detection ──

    protected override void OnPointerEntered(PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        if (DataContext is SpiritViewModel vm)
            vm.IsPointerOver = true;
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (DataContext is SpiritViewModel vm)
            vm.IsPointerOver = false;
    }

    // ── Double-click: poke reaction (startle) ──

    private void OnSpiritDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is SpiritViewModel vm)
        {
            vm.Startle();

            // Give a small upward bounce on poke
            if (_body is not null)
            {
                _body.SetVelocity(_body.VelocityX, -5.0);
                _body.Detach();
            }
        }
    }

    // ── Right-click context menu ──

    private void ShowSpiritContextMenu(PointerPressedEventArgs e)
    {
        var vm = DataContext as SpiritViewModel;

        var menu = new ContextMenu();

        // Show Main Window
        var showMainItem = new MenuItem { Header = "Show Main Window" };
        showMainItem.Click += (_, _) => ShowMainWindow();
        menu.Items.Add(showMainItem);

        // Chat
        var chatItem = new MenuItem { Header = "Chat" };
        chatItem.Click += (_, _) => OpenChatWindow();
        menu.Items.Add(chatItem);

        menu.Items.Add(new Separator());

        // Sleep / Wake toggle
        var isSleeping = vm?.State == SpiritState.Sleeping;
        var sleepItem = new MenuItem { Header = isSleeping ? "Wake Up" : "Sleep" };
        sleepItem.Click += (_, _) =>
        {
            if (vm is null) return;
            if (vm.State == SpiritState.Sleeping)
            {
                vm.TouchInteraction();
                vm.ShowBubble("I'm awake!");
            }
            else
            {
                vm.State = SpiritState.Sleeping;
                vm.ShowBubble("Zzz...");
            }
        };
        menu.Items.Add(sleepItem);

        // Focus Mode
        var focusItem = new MenuItem { Header = "Focus Mode" };
        focusItem.Click += (_, _) =>
        {
            if (vm is null) return;
            vm.State = SpiritState.Thinking;
            vm.ShowBubble("Focus mode on!");
        };
        menu.Items.Add(focusItem);

        menu.Items.Add(new Separator());

        // Exit
        var exitItem = new MenuItem { Header = "Exit" };
        exitItem.Click += (_, _) =>
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lifetime)
                lifetime.Shutdown();
        };
        menu.Items.Add(exitItem);

        // Attach to the spirit canvas and open
        var canvas = this.FindControl<Control>("SpiritCanvas");
        if (canvas is not null)
        {
            canvas.ContextMenu = menu;
            menu.Open(canvas);
        }
    }

    /// <summary>Lazily creates and shows the main application window.</summary>
    private void ShowMainWindow()
    {
        if (_mainWindow is not null && _mainWindow.IsVisible)
        {
            _mainWindow.Activate();
            if (_mainWindow.WindowState == WindowState.Minimized)
                _mainWindow.WindowState = WindowState.Normal;
            return;
        }

        if (_mainWindow is null)
        {
            _mainWindow = new MainWindow
            {
                DataContext = new MainWindowViewModel(),
            };

            // Hide instead of closing so it can be re-shown
            _mainWindow.Closing += (_, e) =>
            {
                e.Cancel = true;
                _mainWindow.Hide();
            };
        }

        _mainWindow.Show();
        _mainWindow.Activate();
    }

    /// <summary>Opens the chat window positioned at screen center.</summary>
    private void OpenChatWindow()
    {
        if (_chatWindow is not null && _chatWindow.IsVisible)
        {
            _chatWindow.Activate();
            return;
        }

        // Load LLM config and create gateway
        var llm = CreateLlmGateway();

        // Build skill registry with built-in skills for the Spirit role
        var skillRegistry = CreateSpiritSkillRegistry();

        var chatVm = new ChatWindowViewModel(llm, skillRegistry);
        _chatWindow = new ChatWindow
        {
            DataContext = chatVm
        };

        // WindowStartupLocation=CenterScreen handles positioning

        // Wire chat messages to spirit bubble
        chatVm.MessageSent += msg =>
        {
            if (DataContext is SpiritViewModel vm)
            {
                vm.TouchInteraction();
                vm.ShowBubble("Thinking...", TimeSpan.FromSeconds(2));
            }
        };

        _chatWindow.Show();

        if (DataContext is SpiritViewModel spiritVm)
            spiritVm.ShowBubble("Let's chat!");
    }

    /// <summary>
    /// Loads ~/.pudding/config.json and creates an LLM gateway for the active provider.
    /// Returns null if no valid config is found (chat falls back to echo mode).
    /// </summary>
    private static ILlmGateway? CreateLlmGateway()
    {
        try
        {
            var config = DesktopConfigLoader.Load();
            if (config.Providers.Count == 0) return null;

            var provider = config.Providers.Find(p => p.Id == config.ActiveProvider)
                           ?? config.Providers[0];

            if (string.IsNullOrWhiteSpace(provider.ApiKey)) return null;

            var options = new LlmOptions(
                Endpoint: provider.Endpoint,
                ApiKey: provider.ApiKey,
                Model: provider.Model,
                Temperature: provider.Temperature,
                MaxTokens: provider.MaxTokens);

            var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
            return new OpenAiLlmGateway(httpClient, options);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Creates a SkillRegistry populated with built-in skills for the desktop spirit.
    /// </summary>
    private static ISkillRegistry CreateSpiritSkillRegistry()
    {
        var registry = new SkillRegistry();

        // Environment skills (file/shell operations)
        registry.Register(new EnvironmentSkills());

        // File management skills (smart probe, eco-recycle, rename, shortcuts)
        registry.Register(new FileManagementSkills());

        // App launcher skills (list installed apps, launch apps)
        registry.Register(new AppLauncherSkills());

        // Introspection skills (self-inspection, must be registered last so it sees all skills)
        registry.Register(new IntrospectionSkills(registry));

        return registry;
    }
}
