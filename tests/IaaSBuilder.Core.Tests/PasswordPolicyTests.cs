using IaaSBuilder.Core.Validation;

namespace IaaSBuilder.Core.Tests;

public class PasswordPolicyTests
{
    [Theory]
    [InlineData("Sup3rSecret!Lab")]
    [InlineData("correct-horse-Battery9")]
    public void Accepts_compliant_passwords(string password) =>
        Assert.Empty(PasswordPolicy.Check(password));

    [Fact]
    public void Rejects_short_passwords() =>
        Assert.Contains(PasswordPolicy.Check("Ab1!xyz"), f => f.Contains("at least"));

    [Fact]
    public void Rejects_passwords_with_too_few_character_classes() =>
        Assert.Contains(PasswordPolicy.Check("alllowercaseletters"), f => f.Contains("3 of"));

    [Fact]
    public void Rejects_azure_disallowed_passwords() =>
        Assert.Contains(PasswordPolicy.Check("Password1"), f => f.Contains("disallowed"));

    [Fact]
    public void Rejects_passwords_containing_the_admin_username() =>
        Assert.Contains(
            PasswordPolicy.Check("LabAdmin-Str0ng!", "labadmin"),
            f => f.Contains("username"));

    /// <summary>
    /// The legacy check used <c>-match '!|@|#|%|^|&amp;|$'</c>. Because that is a regex,
    /// the trailing <c>$</c> alternative matched end-of-string, so the special-character
    /// requirement passed for literally every input. A password with no special character
    /// must now actually be evaluated on its merits.
    /// </summary>
    [Fact]
    public void Special_character_class_is_evaluated_literally()
    {
        // Three classes present (lower, upper, digit) - acceptable without a special char.
        Assert.Empty(PasswordPolicy.Check("NoSpecialsHere123"));

        // Only two classes present - must fail, which the legacy expression could not detect.
        Assert.Contains(PasswordPolicy.Check("nospecialshere123"), f => f.Contains("3 of"));
    }

    [Fact]
    public void Empty_password_is_rejected() =>
        Assert.NotEmpty(PasswordPolicy.Check(""));
}

public class AzureNamingTests
{
    [Theory]
    [InlineData("labdsc001", true)]
    [InlineData("ab", false)]                          // too short
    [InlineData("ThisHasUppercase", false)]
    [InlineData("has-hyphen", false)]
    [InlineData("waytoolongstorageaccountname", false)]
    public void Storage_account_names_follow_the_azure_rules(string name, bool expected) =>
        Assert.Equal(expected, AzureNaming.IsValidStorageAccountName(name));

    [Theory]
    [InlineData("labdc01", true)]
    [InlineData("lab-dc-01", true)]
    [InlineData("thisnameiswaytoolong", false)]        // > 15 characters
    [InlineData("-leadinghyphen", false)]
    [InlineData("trailinghyphen-", false)]
    [InlineData("12345", false)]                       // all digits
    [InlineData("", false)]
    public void Computer_names_respect_the_15_character_limit(string name, bool expected) =>
        Assert.Equal(expected, AzureNaming.IsValidComputerName(name));

    [Theory]
    [InlineData("contoso.local", true)]
    [InlineData("corp.contoso.com", true)]
    [InlineData("contoso", false)]                     // not fully qualified
    [InlineData("", false)]
    public void Domain_names_must_be_fully_qualified(string name, bool expected) =>
        Assert.Equal(expected, AzureNaming.IsValidDomainName(name));

    [Fact]
    public void NetBios_name_is_the_first_label_uppercased() =>
        Assert.Equal("CONTOSO", AzureNaming.ToNetBiosName("contoso.local"));

    [Fact]
    public void NetBios_name_is_truncated_to_15_characters() =>
        Assert.Equal("VERYLONGDOMAINN", AzureNaming.ToNetBiosName("verylongdomainname.local"));
}
