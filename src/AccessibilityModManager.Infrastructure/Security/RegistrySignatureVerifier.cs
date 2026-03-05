using System.Security.Cryptography;
using System.Text;
using Serilog;

namespace AccessibilityModManager.Infrastructure.Security;

/// <summary>
/// Verifies the RSA-PSS signature of the plugin registry JSON.
/// The app ships with the public key baked in; the registry maintainer signs with the private key.
/// </summary>
public sealed class RegistrySignatureVerifier
{
    private readonly RSA _publicKey;
    private readonly ILogger _logger;

    /// <summary>
    /// Creates a verifier with the given RSA public key (PEM-encoded).
    /// </summary>
    public RegistrySignatureVerifier(string publicKeyPem, ILogger logger)
    {
        _publicKey = RSA.Create();
        _publicKey.ImportFromPem(publicKeyPem);
        _logger = logger;
    }

    /// <summary>
    /// Verifies that the JSON content matches the provided signature.
    /// </summary>
    /// <param name="jsonContent">The registry JSON content.</param>
    /// <param name="signatureBase64">The base64-encoded RSA-PSS/SHA256 signature.</param>
    /// <returns>True if the signature is valid.</returns>
    public bool Verify(string jsonContent, string signatureBase64)
    {
        try
        {
            var signature = Convert.FromBase64String(signatureBase64);
            var data = Encoding.UTF8.GetBytes(jsonContent);

            var isValid = _publicKey.VerifyData(
                data,
                signature,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pss);

            if (!isValid)
                _logger.Warning("Registry signature verification failed — signature does not match content");

            return isValid;
        }
        catch (FormatException ex)
        {
            _logger.Error(ex, "Invalid base64 signature format");
            return false;
        }
        catch (CryptographicException ex)
        {
            _logger.Error(ex, "Cryptographic error during registry signature verification");
            return false;
        }
    }

    /// <summary>
    /// Utility: Signs registry JSON with a private key. Used by the registry maintainer's tooling.
    /// </summary>
    public static string Sign(string jsonContent, string privateKeyPem)
    {
        using var rsa = RSA.Create();
        rsa.ImportFromPem(privateKeyPem);

        var data = Encoding.UTF8.GetBytes(jsonContent);
        var signature = rsa.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);

        return Convert.ToBase64String(signature);
    }
}
