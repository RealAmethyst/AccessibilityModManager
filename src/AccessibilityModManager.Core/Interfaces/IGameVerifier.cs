using AccessibilityModManager.Core.Models;

namespace AccessibilityModManager.Core.Interfaces;

public interface IGameVerifier
{
    bool VerifyInstallPath(GameDefinition game, string path);
}
