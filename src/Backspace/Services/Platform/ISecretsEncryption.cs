namespace Backspace.Services.Platform;

/// <summary>
/// Protects this computer's copy of a vault key (<see cref="VaultKeyCache"/>,
/// U153). The vault itself is encrypted with the person's password and
/// travels; only the key copy is bound to this computer.
///
/// <para>
/// Per-platform implementations:
/// <list type="bullet">
///   <item><b>Windows</b> — DPAPI (CurrentUser) via
///         <c>System.Security.Cryptography.ProtectedData</c>. Bound
///         to the Windows user + machine pair. Access also depends on
///         the logon context; the account name alone does not establish
///         that an unattended session can decrypt.</item>
///   <item><b>macOS / Linux</b> — AES-GCM with a 256-bit key kept in
///         a chmod-600 file at <c>keys/key-file.bin</c> on this
///         computer. Not bound to the OS keychain (a follow-up can wire
///         macOS Keychain / libsecret) but a meaningful obstacle to
///         offline-only attackers — without the key the ciphertext
///         is opaque.</item>
/// </list>
/// </para>
///
/// <para>
/// <see cref="Unprotect"/> throws on tamper / wrong key / corrupt
/// blob. <see cref="VaultKeyCache"/> treats that as no usable key copy:
/// the vault counts as locked and the password is asked once.
/// </para>
/// </summary>
public interface ISecretsEncryption
{
    byte[] Protect(byte[] cleartext, byte[] entropy);
    byte[] Unprotect(byte[] ciphertext, byte[] entropy);

    /// <summary>
    /// Short label shown in the user-facing "couldn't decrypt" message
    /// so a power user reading it knows whether they're looking at
    /// DPAPI / Keychain / file-key trouble.
    /// </summary>
    string DisplayName { get; }
}
