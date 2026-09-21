using System.Security.Cryptography;

namespace IaaSBuilder.Core.Validation;

/// <summary>
/// Generates an administrator password that satisfies <see cref="PasswordPolicy"/>.
/// </summary>
/// <remarks>
/// <para>
/// Typing a password by hand is where lab builds stall: Azure rejects it after the plan is
/// otherwise complete, and the operator ends up picking something weak and memorable to get
/// moving. Generating one removes both problems.
/// </para>
/// <para>
/// The character set is deliberately narrower than "any special character". This password is
/// handed to ARM as a secure string and then to DSC as a <c>PSCredential</c>, and it ends up
/// inside PowerShell configurations. Quotes, backticks, backslashes and shell metacharacters
/// (<c>&amp; | &lt; &gt; ; $</c>) are the ones that historically break that path, so they are
/// excluded. Visually ambiguous characters (<c>O 0 l 1 I</c>) are excluded too, because these
/// passwords get read off a screen and typed into a console.
/// </para>
/// </remarks>
public static class PasswordGenerator
{
    /// <summary>
    /// Comfortably above the 12-character Azure minimum, because the generated password is not
    /// meant to be memorised.
    /// </summary>
    public const int DefaultLength = 20;

    private const string Lowercase = "abcdefghijkmnopqrstuvwxyz";
    private const string Uppercase = "ABCDEFGHJKLMNPQRSTUVWXYZ";
    private const string Digits = "23456789";
    private const string Specials = "!@#%^*_-+=?";

    private static readonly string[] Classes = [Lowercase, Uppercase, Digits, Specials];
    private static readonly string All = string.Concat(Classes);

    /// <summary>
    /// Returns a password that passes <see cref="PasswordPolicy.Check"/> for this username.
    /// </summary>
    /// <param name="adminUsername">
    /// Excluded from the result, because Azure rejects a password containing the username.
    /// </param>
    /// <param name="length">Length, clamped to the policy's own bounds.</param>
    public static string Generate(string? adminUsername = null, int length = DefaultLength)
    {
        length = Math.Clamp(length, PasswordPolicy.MinLength, PasswordPolicy.MaxLength);

        // One character from every class guarantees the "3 of 4 classes" rule by construction.
        // The remaining checks (disallowed list, username containment) are properties of the
        // whole string, so they are verified rather than constructed - and a fresh draw is
        // overwhelmingly likely to satisfy them.
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var candidate = Draw(length);
            if (PasswordPolicy.IsValid(candidate, adminUsername))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException(
            "Could not generate a password that satisfies the policy for this username.");
    }

    private static string Draw(int length)
    {
        var chars = new char[length];

        for (var i = 0; i < Classes.Length && i < length; i++)
        {
            chars[i] = Pick(Classes[i]);
        }

        for (var i = Classes.Length; i < length; i++)
        {
            chars[i] = Pick(All);
        }

        // Without this the first four positions would always be lower, upper, digit, special.
        Shuffle(chars);
        return new string(chars);
    }

    private static char Pick(string pool) => pool[RandomNumberGenerator.GetInt32(pool.Length)];

    private static void Shuffle(char[] chars)
    {
        for (var i = chars.Length - 1; i > 0; i--)
        {
            var j = RandomNumberGenerator.GetInt32(i + 1);
            (chars[i], chars[j]) = (chars[j], chars[i]);
        }
    }
}
