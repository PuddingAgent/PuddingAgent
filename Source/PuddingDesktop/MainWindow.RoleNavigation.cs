using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PuddingChat;
using PuddingChat.WinUI;
using PuddingDesktop.Foundation;

namespace PuddingDesktop;

/// <summary>
/// The host owns role navigation: the shell sidebar lists the agents Core reports for the current
/// data root and drives the chat's role selection. The native chat is told to stop drawing its own
/// navigation column (width 0) and keeps only its narrow-layout Flyout fallback.
/// <para>
/// Every call goes through Composition into Core application services — there is no chat HTTP,
/// JWT or WebView on this path.
/// </para>
/// </summary>
public sealed partial class MainWindow
{
    private readonly List<RoleAvatarCard> _roleCards = [];
    private readonly Dictionary<RoleAvatarCard, RoleNavigationItem> _roleCardItems = [];
    private readonly Dictionary<RoleKey, RoleAvatarCard> _roleCardsByRole = [];
    private IChatClient? _roleSidebarClient;
    private string? _roleSidebarDataRoot;
    private bool _loadingRoleSidebar;
    private bool _renderingRoleSidebar;

    /// <summary>
    /// Loads the sidebar from the live in-process Core. Failures surface as a real message with the
    /// underlying exception logged — an empty sidebar must never be presented as "no roles exist".
    /// </summary>
    private async Task LoadRoleSidebarAsync(string dataRoot)
    {
        if (_loadingRoleSidebar || _exiting || _kernel.Snapshot.State != DesktopKernelState.Ready) return;
        _loadingRoleSidebar = true;
        RefreshRolesButton.IsEnabled = false;
        try
        {
            if (_roleSidebarClient is null
                || !string.Equals(_roleSidebarDataRoot, dataRoot, StringComparison.OrdinalIgnoreCase))
            {
                _roleSidebarClient?.Dispose();
                _roleSidebarClient = _createChatClient();
                _roleSidebarDataRoot = dataRoot;
            }

            var client = _roleSidebarClient;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var workspaces = await client.GetWorkspacesAsync(timeout.Token);
            var agentsByWorkspace = new Dictionary<string, IReadOnlyList<Agent>>(StringComparer.Ordinal);
            var statusesByWorkspace = new Dictionary<string, IReadOnlyList<AgentStatus>>(StringComparer.Ordinal);
            foreach (var workspace in workspaces)
            {
                agentsByWorkspace[workspace.WorkspaceId] =
                    await client.GetAgentsAsync(workspace.WorkspaceId, timeout.Token);
                statusesByWorkspace[workspace.WorkspaceId] =
                    await client.GetStatusesAsync(workspace.WorkspaceId, timeout.Token);
            }

            // A Core restart or data-root switch owns a new client; a late reply must not overwrite it.
            if (_exiting || !ReferenceEquals(_roleSidebarClient, client)) return;
            RenderRoleSidebar(RoleNavigation.Build(workspaces, agentsByWorkspace, statusesByWorkspace));
        }
        catch (Exception exception)
        {
            App.WriteDiagnostic(exception);
            ShowRoleSidebarUnavailable(exception);
        }
        finally
        {
            _loadingRoleSidebar = false;
            RefreshRolesButton.IsEnabled = true;
        }
    }

    private void RenderRoleSidebar(RoleNavigationSnapshot snapshot)
    {
        var previous = RoleList.SelectedItem is RoleAvatarCard selected
                       && _roleCardItems.TryGetValue(selected, out var selectedItem)
            ? selectedItem.Role
            : (RoleKey?)null;

        _renderingRoleSidebar = true;
        try
        {
            RoleList.SelectedItem = null;
            RoleList.Items.Clear();
            _roleCards.Clear();
            _roleCardItems.Clear();
            _roleCardsByRole.Clear();

            foreach (var item in snapshot.Items)
            {
                var card = new RoleAvatarCard(item.Agent);
                card.SetStatus(item.Status);
                _roleCards.Add(card);
                _roleCardItems[card] = item;
                _roleCardsByRole[item.Role] = card;
                RoleList.Items.Add(card);
            }

            RoleCount.Text = snapshot.Items.Count.ToString();
            RoleCountTooltip(snapshot);
            EmptyRoles.Text = snapshot.Items.Count == 0
                ? "这个数据目录还没有角色。可在「设置 → 角色与模板」中创建，或用「＋ 新工作」建立第一个角色。"
                : "";
            EmptyRoles.Visibility = snapshot.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            if (previous is { } role && _roleCardsByRole.TryGetValue(role, out var restored))
                RoleList.SelectedItem = restored;
        }
        finally
        {
            _renderingRoleSidebar = false;
        }
    }

    /// <summary>Duplicate rows are reported instead of hidden: two rows selecting one role would be ambiguous.</summary>
    private void RoleCountTooltip(RoleNavigationSnapshot snapshot)
    {
        var text = snapshot.DuplicateAgentsDropped == 0
            ? "当前数据目录中的角色数量。"
            : $"当前数据目录中的角色数量。Core 返回了 {snapshot.DuplicateAgentsDropped} 个重复角色标识，已折叠为一行。";
        ToolTipService.SetToolTip(RoleCount, text);
    }

    private void ShowRoleSidebarUnavailable(Exception exception)
    {
        _renderingRoleSidebar = true;
        try
        {
            RoleList.Items.Clear();
            _roleCards.Clear();
            _roleCardItems.Clear();
            _roleCardsByRole.Clear();
            RoleCount.Text = "—";
            ToolTipService.SetToolTip(RoleCount, "角色列表读取失败，见下方原因。");
            EmptyRoles.Text = exception is OperationCanceledException
                ? "读取角色超时。Core 可能仍在启动，请稍后点「刷新」。"
                : $"读取角色失败：{exception.Message}\n可打开「运行中心」检查内核状态，或点「刷新」重试。";
            EmptyRoles.Visibility = Visibility.Visible;
        }
        finally
        {
            _renderingRoleSidebar = false;
        }
    }

    private async void OnRefreshRoles(object sender, RoutedEventArgs args)
    {
        var root = _chatDataRoot ?? DataRootEditor.Text.Trim();
        if (string.IsNullOrWhiteSpace(root)) return;
        await LoadRoleSidebarAsync(root);
    }

    /// <summary>Selecting a role here is the only role-switch entry point; the chat owns execution state.</summary>
    private async Task SelectSidebarRoleAsync(RoleNavigationItem item)
    {
        if (_nativeChat is null) return;
        if (!item.CanSelect)
        {
            EmptyRoles.Text = $"{item.Label} 已冻结或停用，Core 会拒绝执行；请先在角色设置中启用它。";
            EmptyRoles.Visibility = Visibility.Visible;
            return;
        }

        EmptyRoles.Visibility = Visibility.Collapsed;
        _state.Navigate(ShellPage.Workbench);
        try
        {
            _nativeChat.SetActive(true);
            await _nativeChat.InitializeAsync();
            await _nativeChat.SelectRoleAsync(item.Workspace.WorkspaceId, item.Agent);
        }
        catch (Exception exception)
        {
            App.WriteDiagnostic(exception);
            EmptyRoles.Text = $"切换角色失败：{exception.Message}";
            EmptyRoles.Visibility = Visibility.Visible;
        }
    }

    private void ReleaseRoleSidebarClient()
    {
        _roleSidebarClient?.Dispose();
        _roleSidebarClient = null;
        _roleSidebarDataRoot = null;
    }
}
