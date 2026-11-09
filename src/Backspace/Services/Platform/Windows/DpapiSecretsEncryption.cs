using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace Backspace.Services.Platform.Windows;

/// <summary>
/// DPAPI-backed encryption (CurrentUser scope). Bound to the Windows
/// user + machine pair: copying a vault key copy to another account
/// or another machine produces a <see cref="CryptographicException"/>
/// from <see cref="ProtectedData.Unprotect"/>, which
/// <see cref="VaultKeyCache"/> treats as no key here: the vault is
/// locked and the password is asked once.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiSecretsEncryption : ISecretsEncryption
{
    public string DisplayName => "DPAPI (Windows user + machine)";

    public byte[] Protect(byte[] cleartext, byte[] entropy) =>
        ProtectedData.Protect(cleartext, entropy, DataProtectionScope.CurrentUser);

    public byte[] Unprotect(byte[] ciphertext, byte[] entropy) =>
        ProtectedData.Unprotect(ciphertext, entropy, DataProtectionScope.CurrentUser);
}
