using System.Security.Cryptography;

namespace AccessibilityModManager.Tests.CatalogClaims;

/// <summary>
/// Two RSA-4096 keys shared by every claim test in the assembly.
///
/// The contract pins one key size, so tests cannot use a smaller one for speed. Generating 4096
/// bits is slow enough that doing it per test — xUnit builds a fresh fixture for each — turned a
/// two-second suite into a minute-long one. Nothing here mutates a key, so one pair for the whole
/// run is safe, and they live as long as the process rather than being disposed by whichever test
/// happened to finish last.
/// </summary>
internal static class ClaimTestKeys
{
    internal static readonly RSA Primary = RSA.Create(4096);
    internal static readonly RSA Secondary = RSA.Create(4096);
}
