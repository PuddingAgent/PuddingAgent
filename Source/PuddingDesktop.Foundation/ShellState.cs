using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace PuddingDesktop.Foundation;

/// <summary>UI state only. Switching selection never executes, cancels or retargets a Run.</summary>
public sealed class ShellState : INotifyPropertyChanged
{
    private readonly Dictionary<WorkContext, string> _drafts = [];
    private readonly ObservableCollection<RoleSummary> _roles = [];
    private readonly ObservableCollection<WorkspaceDocument> _documents = [];
    private RoleSummary? _selectedRole;
    private WorkspaceDocument? _selectedDocument;
    private ShellPage _page;
    private long _selectionGeneration;

    public ShellState()
    {
        Roles = new(_roles);
        Documents = new(_documents);
    }

    public ReadOnlyObservableCollection<RoleSummary> Roles { get; }
    public ReadOnlyObservableCollection<WorkspaceDocument> Documents { get; }
    public RoleSummary? SelectedRole => _selectedRole;
    public WorkspaceDocument? SelectedDocument => _selectedDocument;
    public WorkContext? ActiveContext => _selectedRole is { } role
        ? new(role.Identity, role.MainSessionId) : null;
    public long SelectionGeneration => _selectionGeneration;
    public string Draft => ActiveContext is { } context ? _drafts.GetValueOrDefault(context, "") : "";
    public ShellPage Page => _page;
    public event PropertyChangedEventHandler? PropertyChanged;

    public void ReplaceRoles(IEnumerable<RoleSummary> roles)
    {
        var next = roles.ToArray();
        if (next.Select(role => role.Identity).Distinct().Count() != next.Length)
            throw new ArgumentException("Duplicate Agent identity.", nameof(roles));
        foreach (var role in next)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(role.Identity.WorkspaceId);
            ArgumentException.ThrowIfNullOrWhiteSpace(role.Identity.AgentId);
            ArgumentException.ThrowIfNullOrWhiteSpace(role.MainSessionId);
        }
        var previous = _selectedRole?.Identity;
        _roles.Clear();
        foreach (var role in next) _roles.Add(role);
        _selectedRole = previous is null ? null : next.FirstOrDefault(role => role.Identity == previous);
        SelectionChanged();
    }

    public void SelectRole(AgentIdentity identity)
    {
        var role = _roles.FirstOrDefault(role => role.Identity == identity)
            ?? throw new ArgumentException("Agent does not belong to this navigation snapshot.", nameof(identity));
        if (_selectedRole == role) return;
        _selectedRole = role;
        SelectionChanged();
        Navigate(ShellPage.Workbench);
    }

    /// <summary>Reject a delayed editor/async callback from a previous selection.</summary>
    public bool TrySetDraft(long generation, string text)
    {
        if (generation != _selectionGeneration || ActiveContext is not { } context) return false;
        _drafts[context] = text;
        OnChanged(nameof(Draft));
        return true;
    }

    public void OpenDocument(WorkspaceDocument document)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(document.Id);
        var existing = _documents.FirstOrDefault(item => item.Id == document.Id);
        if (existing is not null && (existing.Owner != document.Owner || existing.ResourceReference != document.ResourceReference))
            throw new InvalidOperationException("Document identity cannot be reassigned to a different owner or resource.");
        if (existing is null) _documents.Add(document);
        SelectDocument(document.Id);
    }

    public void SelectDocument(string id)
    {
        _selectedDocument = _documents.FirstOrDefault(document => document.Id == id)
            ?? throw new ArgumentException("Unknown document.", nameof(id));
        OnChanged(nameof(SelectedDocument));
    }

    public void CloseDocument(string id)
    {
        var index = _documents.ToList().FindIndex(document => document.Id == id);
        if (index < 0) return;
        var wasSelected = _selectedDocument?.Id == id;
        _documents.RemoveAt(index);
        if (wasSelected)
        {
            _selectedDocument = _documents.Count == 0 ? null : _documents[Math.Min(index, _documents.Count - 1)];
            OnChanged(nameof(SelectedDocument));
        }
    }

    public void Navigate(ShellPage page)
    {
        _page = page;
        OnChanged(nameof(Page));
    }

    private void SelectionChanged()
    {
        _selectionGeneration++;
        OnChanged(nameof(SelectedRole));
        OnChanged(nameof(ActiveContext));
        OnChanged(nameof(SelectionGeneration));
        OnChanged(nameof(Draft));
    }

    private void OnChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new(propertyName));
}
