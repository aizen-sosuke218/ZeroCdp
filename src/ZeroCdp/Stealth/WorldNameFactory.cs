using System.Security.Cryptography;

namespace ZeroCdp.Stealth;

/// <summary>
/// Generates isolated-world names that carry no library identity.
/// </summary>
/// <remarks>
/// The name is handed to <c>Page.createIsolatedWorld</c> and is not injected into the page, but a
/// name such as <c>__zerocdp_utility</c> would still be a fingerprint if it ever surfaced in a
/// diagnostic or a future protocol event. A random identifier has no such meaning and is unique
/// per page.
/// </remarks>
public static class WorldNameFactory
{
    /// <summary>Creates a 32 character lowercase hexadecimal name.</summary>
    public static string Create()
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexStringLower(bytes);
    }
}
