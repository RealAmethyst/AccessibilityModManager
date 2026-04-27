namespace AccessibilityModManager.Core.Models;

/// <summary>
/// One filter tag the registry knows about. Authors pick from these in the AuthorTool's
/// Filters tab; the manager renders a checkbox per tag in the filter sidebar. Authors can
/// add custom tags too — those appear as additional checkboxes alongside the core set.
/// </summary>
public sealed record TagDefinition(string Id, string Label, string Category);

/// <summary>
/// The hardcoded core tag catalog, shared between the AuthorTool and Manager. A future
/// enhancement is to fetch this from <c>tag-catalog.json</c> in the registry repo so
/// labels can change without re-shipping the apps.
/// </summary>
public static class TagCatalog
{
    public static IReadOnlyList<TagDefinition> Core { get; } = new[]
    {
        new TagDefinition("screen-reader",       "Screen reader supported", "Accessibility"),
        new TagDefinition("controller-support",  "Controller support",      "Input"),
        new TagDefinition("audio-cues",          "Audio cues",              "Accessibility"),
        new TagDefinition("completable",         "Game is completable",     "Scope"),
        new TagDefinition("multiplayer",         "Multiplayer supported",   "Scope"),
    };

    public static TagDefinition? FindById(string id) =>
        Core.FirstOrDefault(t => t.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    public static IEnumerable<IGrouping<string, TagDefinition>> ByCategory() =>
        Core.GroupBy(t => t.Category);
}

/// <summary>
/// Curated ISO 639-1 language list. Not exhaustive — covers the languages most likely to
/// appear in accessibility mods. Authors can technically add others by editing index.json
/// directly; the AuthorTool only exposes this list in the picker.
/// </summary>
public static class LanguageCatalog
{
    public static IReadOnlyList<LanguageDefinition> All { get; } = new[]
    {
        new LanguageDefinition("en", "English"),
        new LanguageDefinition("es", "Spanish"),
        new LanguageDefinition("fr", "French"),
        new LanguageDefinition("de", "German"),
        new LanguageDefinition("it", "Italian"),
        new LanguageDefinition("pt", "Portuguese"),
        new LanguageDefinition("ja", "Japanese"),
        new LanguageDefinition("ko", "Korean"),
        new LanguageDefinition("zh", "Chinese"),
        new LanguageDefinition("ru", "Russian"),
        new LanguageDefinition("nl", "Dutch"),
        new LanguageDefinition("sv", "Swedish"),
        new LanguageDefinition("nb", "Norwegian"),
        new LanguageDefinition("da", "Danish"),
        new LanguageDefinition("fi", "Finnish"),
        new LanguageDefinition("pl", "Polish"),
        new LanguageDefinition("cs", "Czech"),
        new LanguageDefinition("tr", "Turkish"),
        new LanguageDefinition("ar", "Arabic"),
        new LanguageDefinition("he", "Hebrew"),
        new LanguageDefinition("hi", "Hindi"),
        new LanguageDefinition("th", "Thai"),
        new LanguageDefinition("vi", "Vietnamese"),
    };

    public static string LabelFor(string code) =>
        All.FirstOrDefault(l => l.Code.Equals(code, StringComparison.OrdinalIgnoreCase))?.Label
        ?? code.ToUpperInvariant();
}

public sealed record LanguageDefinition(string Code, string Label);
