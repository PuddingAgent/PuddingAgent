using System.Text.Json;

namespace PuddingDesktop.Foundation;

/// <summary>Presentation inventory only. No service calls, credentials or persisted Core state.</summary>
public sealed record SettingsCard(string Id, string Title, string Description,
    IReadOnlyList<string> Fields, string Source, string Task, string Status)
{
    public string FieldSummary => string.Join(" · ", Fields);
}

public sealed record SettingsTab(string Id, string Title, IReadOnlyList<SettingsCard> Cards);
public sealed record SettingsCategory(string Id, string Title, string Group, string Glyph,
    string Description, IReadOnlyList<SettingsTab> Tabs);

public static class SettingsCatalog
{
    public static IReadOnlyList<SettingsCategory> Categories { get; } = Load();

    private static IReadOnlyList<SettingsCategory> Load()
    {
        using var stream = typeof(SettingsCatalog).Assembly.GetManifestResourceStream(
            "PuddingDesktop.Foundation.SettingsCatalog.json")
            ?? throw new InvalidOperationException("Settings catalog is missing.");
        return JsonSerializer.Deserialize<SettingsCategory[]>(stream,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("Settings catalog is empty.");
    }

    /// <summary>Search labels, field names and handoff IDs without touching the kernel.</summary>
    public static IReadOnlyList<SettingsCategory> Search(string? query)
    {
        var term = query?.Trim();
        if (string.IsNullOrEmpty(term)) return Categories;
        bool Contains(string text) => text.Contains(term, StringComparison.OrdinalIgnoreCase);
        return Categories.Select(category => Contains(category.Title)
                ? category
                : category with
                {
                    Tabs = category.Tabs.Select(tab => Contains(tab.Title)
                            ? tab
                            : tab with
                            {
                                Cards = tab.Cards.Where(card => Contains(card.Title) || Contains(card.Description)
                                    || Contains(card.FieldSummary) || Contains(card.Id) || Contains(card.Task)).ToArray()
                            })
                        .Where(tab => tab.Cards.Count > 0).ToArray()
                })
            .Where(category => category.Tabs.Count > 0).ToArray();
    }
}
