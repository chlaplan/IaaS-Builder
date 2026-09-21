using System.Text.RegularExpressions;
using IaaSBuilder.Core.Models;
using IaaSBuilder.Core.Validation;

namespace IaaSBuilder.Core.Tests;

/// <summary>
/// The checklist highlights the block of controls the operator should use next, by matching
/// <see cref="SetupStep.Anchor"/> against an <c>Anchor</c> on a <c>StepBlock</c> in the markup.
///
/// Nothing connects those two halves at compile time. A renamed anchor on either side leaves a
/// page that renders perfectly and simply never highlights anything - the exact failure the
/// checklist itself had before (a computed list meant instance comparison never matched). So the
/// link is asserted here against the real .razor sources instead.
/// </summary>
public class StepAnchorTests
{
    private static readonly string WebComponents =
        Path.Combine(RepoRoot.Path, "src", "IaaSBuilder.Web", "Components");

    /// <summary>Every <c>Anchor="..."</c> written on a StepBlock in the shipped pages.</summary>
    private static HashSet<string> RenderedAnchors()
    {
        var anchors = new HashSet<string>(StringComparer.Ordinal);

        foreach (var file in Directory.EnumerateFiles(WebComponents, "*.razor", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);

            foreach (Match match in Regex.Matches(text, """<StepBlock\s+Anchor="([^"]+)"""))
            {
                anchors.Add(match.Groups[1].Value);
            }
        }

        return anchors;
    }

    private static IEnumerable<SetupStep> AllSteps()
    {
        // Both sign-in states, because the checklist's contents do not depend on the plan but
        // several steps flip Done with it, and a step is only ever reachable as "next" when it
        // is not done.
        foreach (var signedIn in new[] { false, true })
        {
            foreach (var step in SetupChecklist.For(new DeploymentPlan(), signedIn, null))
            {
                yield return step;
            }
        }
    }

    [Fact]
    public void Every_step_names_an_anchor()
    {
        foreach (var step in AllSteps())
        {
            Assert.False(
                string.IsNullOrWhiteSpace(step.Anchor),
                $"Step '{step.Title}' has no anchor, so nothing on '{step.Page}' can highlight it.");
        }
    }

    [Fact]
    public void Every_step_anchor_is_rendered_by_a_page()
    {
        var rendered = RenderedAnchors();

        // Guards the guard: if the regex or the path stopped finding anything, every assertion
        // below would fail for a reason that has nothing to do with the anchors.
        Assert.NotEmpty(rendered);

        foreach (var step in AllSteps())
        {
            Assert.True(
                rendered.Contains(step.Anchor),
                $"Step '{step.Title}' points at anchor '{step.Anchor}', which no StepBlock renders. "
                + $"Rendered anchors: {string.Join(", ", rendered.Order())}.");
        }
    }

    [Fact]
    public void Every_rendered_anchor_belongs_to_a_step()
    {
        var wanted = AllSteps().Select(s => s.Anchor).ToHashSet(StringComparer.Ordinal);

        foreach (var anchor in RenderedAnchors())
        {
            Assert.True(
                wanted.Contains(anchor),
                $"A StepBlock renders anchor '{anchor}', which no checklist step ever asks for, "
                + "so it can never highlight.");
        }
    }

    [Fact]
    public void Anchors_are_unique_so_one_step_highlights_one_block()
    {
        var steps = SetupChecklist.For(new DeploymentPlan(), signedIn: false, adminPassword: null);
        var duplicates = steps
            .GroupBy(s => s.Anchor, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        Assert.True(
            duplicates.Count == 0,
            $"Anchors shared by more than one step: {string.Join(", ", duplicates)}. "
            + "Two steps on one block means the highlight stays put while the checklist advances.");
    }
}
