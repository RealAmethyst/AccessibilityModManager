using AccessibilityModManager.Core.Models;

namespace AccessibilityModManager.Core.Interfaces;

public interface IConfigService
{
    Task<AppConfig> LoadAsync();
    Task SaveAsync(AppConfig config);
}
