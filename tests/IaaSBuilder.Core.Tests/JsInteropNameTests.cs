using System.Text.RegularExpressions;

namespace IaaSBuilder.Core.Tests;

/// <summary>
/// The Copy button next to the generated admin password did nothing at all. It called
/// <c>copyText</c>, but app.js hangs every function off a single <c>window.iaasBuilder</c> object,
/// so the name resolved to undefined, the interop threw, and the page's catch block swallowed it.
/// A typo in a string literal, invisible to the compiler, in the one place where failing silently
/// is worst: a password that is generated once, never persisted, and gone if it is not captured.
///
/// This walks every JS interop call in the web project and checks the function actually exists.
/// </summary>
public class JsInteropNameTests
{
    private static string WebRoot => Path.Combine(RepoRoot.Path, "src", "IaaSBuilder.Web");

    /// <summary>
    /// Functions the browser provides. Everything else has to be one this repo ships, because
    /// there is no CDN and no module fetch - the app has to run in a disconnected enclave.
    /// </summary>
    private static readonly string[] BrowserBuiltIns =
        ["localStorage.", "sessionStorage.", "console.", "navigator.", "window."];

    private static IReadOnlyList<(string File, string Function)> InteropCalls()
    {
        var calls = new List<(string, string)>();

        var pattern = new Regex(
            @"Invoke(?:Void)?Async(?:<[^>]+>)?\s*\(\s*""([^""]+)""",
            RegexOptions.Compiled);

        var files = Directory
            .EnumerateFiles(WebRoot, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".razor", StringComparison.OrdinalIgnoreCase)
                     || f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

        foreach (var file in files)
        {
            foreach (Match match in pattern.Matches(File.ReadAllText(file)))
            {
                calls.Add((Path.GetFileName(file), match.Groups[1].Value));
            }
        }

        return calls;
    }

    private static IReadOnlySet<string> FunctionsInAppJs()
    {
        var js = File.ReadAllText(Path.Combine(WebRoot, "wwwroot", "app.js"));

        // Matches the "name: function" and "name: async function" members of window.iaasBuilder.
        var names = Regex
            .Matches(js, @"(\w+)\s*:\s*(?:async\s+)?function")
            .Select(m => "iaasBuilder." + m.Groups[1].Value);

        return names.ToHashSet(StringComparer.Ordinal);
    }

    [Fact]
    public void The_script_defines_the_functions_it_is_expected_to()
    {
        var defined = FunctionsInAppJs();

        Assert.Contains("iaasBuilder.copyText", defined);
        Assert.Contains("iaasBuilder.downloadText", defined);
    }

    [Fact]
    public void There_is_at_least_one_interop_call_to_check()
    {
        // Without this the test below passes vacuously the moment the regex stops matching.
        Assert.NotEmpty(InteropCalls());
    }

    [Fact]
    public void Every_js_function_called_from_dotnet_exists()
    {
        var defined = FunctionsInAppJs();

        var missing = InteropCalls()
            .Where(c => !BrowserBuiltIns.Any(b => c.Function.StartsWith(b, StringComparison.Ordinal)))
            .Where(c => !defined.Contains(c.Function))
            .Select(c => $"{c.File} calls '{c.Function}'")
            .Distinct()
            .ToList();

        Assert.True(
            missing.Count == 0,
            "These JS functions are invoked from .NET but are not defined in app.js. A missing " +
            "function throws a JSException at runtime, which callers usually catch and ignore, so " +
            "the feature just silently does nothing:" +
            Environment.NewLine + string.Join(Environment.NewLine, missing));
    }

    /// <summary>
    /// The password copy in particular, because it is the one that broke and because its failure
    /// mode is silent by design - the catch block is correct, the function name was not.
    /// </summary>
    [Fact]
    public void The_password_copy_calls_the_namespaced_function()
    {
        var page = File.ReadAllText(
            Path.Combine(WebRoot, "Components", "Pages", "IdentityPage.razor"));

        Assert.Contains("\"iaasBuilder.copyText\"", page);
        Assert.DoesNotContain("InvokeVoidAsync(\"copyText\"", page);
    }

    /// <summary>
    /// copyText returns false when the browser refuses the clipboard - a non-loopback http origin
    /// is not a secure context. Telling the operator "Copied" when it is not on the clipboard is
    /// worse than telling them nothing, because they close the page believing they have it.
    /// </summary>
    [Fact]
    public void The_password_copy_reads_the_result_rather_than_assuming_success()
    {
        var page = File.ReadAllText(
            Path.Combine(WebRoot, "Components", "Pages", "IdentityPage.razor"));

        Assert.Contains("_passwordCopied = await JS.InvokeAsync<bool>", page);
    }
}
