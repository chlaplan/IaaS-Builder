using System.Text.RegularExpressions;

namespace IaaSBuilder.Core.Validation;

/// <summary>
/// Azure VM administrator password rules.
/// </summary>
/// <remarks>
/// Ported from the legacy <c>$WPFadminpassword1.Add_LostFocus</c> handler, which packed the
/// whole policy into one unreadable boolean expression and had two real defects:
/// the special-character class <c>'!|@|#|%|^|&amp;|$'</c> was an unescaped <c>-match</c> regex
/// (so <c>$</c> matched end-of-string and every password "passed" that check), and it used a
/// short hard-coded disallow list. This version reports *which* rule failed.
/// </remarks>
public static partial class PasswordPolicy
{
    public const int MinLength = 12;
    public const int MaxLength = 123;

    /// <summary>Documented by Azure as always rejected, regardless of complexity.</summary>
    private static readonly string[] DisallowedPasswords =
    [
        "abc@123", "iloveyou!", "P@$$w0rd", "P@ssw0rd", "P@ssword123",
        "Pa$$word", "pass@word1", "Password!", "Password1", "Password22"
    ];

    [GeneratedRegex(@"[a-z]")] private static partial Regex Lowercase();
    [GeneratedRegex(@"[A-Z]")] private static partial Regex Uppercase();
    [GeneratedRegex(@"[0-9]")] private static partial Regex Digit();
    [GeneratedRegex(@"[^a-zA-Z0-9]")] private static partial Regex Special();

    /// <summary>Returns the list of unmet requirements; empty means the password is acceptable.</summary>
    public static IReadOnlyList<string> Check(string? password, string? adminUsername = null)
    {
        var failures = new List<string>();

        if (string.IsNullOrEmpty(password))
        {
            failures.Add("A password is required.");
            return failures;
        }

        if (password.Length < MinLength)
            failures.Add($"Must be at least {MinLength} characters (Azure minimum is 12 for Windows).");

        if (password.Length > MaxLength)
            failures.Add($"Must be at most {MaxLength} characters.");

        // Azure requires 3 of the 4 character classes.
        var classes = 0;
        if (Lowercase().IsMatch(password)) classes++;
        if (Uppercase().IsMatch(password)) classes++;
        if (Digit().IsMatch(password)) classes++;
        if (Special().IsMatch(password)) classes++;

        if (classes < 3)
            failures.Add("Must contain at least 3 of: lowercase, uppercase, digit, special character.");

        if (DisallowedPasswords.Contains(password, StringComparer.Ordinal))
            failures.Add("This password is on Azure's disallowed list.");

        if (!string.IsNullOrWhiteSpace(adminUsername) &&
            password.Contains(adminUsername, StringComparison.OrdinalIgnoreCase))
        {
            failures.Add("Must not contain the administrator username.");
        }

        return failures;
    }

    public static bool IsValid(string? password, string? adminUsername = null) =>
        Check(password, adminUsername).Count == 0;
}
