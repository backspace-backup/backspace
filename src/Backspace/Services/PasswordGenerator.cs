using System.Security.Cryptography;
using System.Text;

namespace Backspace.Services;

/// <summary>
/// Strong passwords people can write down and type back (U250, owner 14
/// November 2026: "offer to generate a password ... and make sure user has
/// taken a note of it"). Five groups of five lowercase letters and digits,
/// without the look-alikes (0/o, 1/l/i): "k7mqd-x3vth-..." is about 124
/// bits, from the system's cryptographic random generator.
/// </summary>
public static class PasswordGenerator
{
    internal const string Alphabet = "abcdefghjkmnpqrstuvwxyz23456789";
    internal const int Groups = 5, GroupLength = 5;

    public static string Create()
    {
        var text = new StringBuilder(Groups * (GroupLength + 1));
        for (var g = 0; g < Groups; g++)
        {
            if (g > 0) text.Append('-');
            for (var i = 0; i < GroupLength; i++) text.Append(Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)]);
        }
        return text.ToString();
    }

    /// <summary>What you typed back is the password: surrounding spaces and
    /// letter case don't matter (the password has no capitals); everything
    /// else, hyphens included, must match.</summary>
    public static bool Matches(string password, string typed)
        => string.Equals(password, (typed ?? "").Trim().ToLowerInvariant(), System.StringComparison.Ordinal);
}
