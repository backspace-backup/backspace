namespace Backspace.Services;

/// <summary>A single credential authority. A remote implementation must fail
/// explicitly when unavailable; it must never fall back to an older local vault.</summary>
internal interface ISecretsBackend
{
    string? LoadErrorMessage => null;
    void ValidateReadable();
    bool TryGet(string key, out string value);
    void Set(string key, string value);
    void Remove(string key);
}
