using IaaSBuilder.Core.Models;

namespace IaaSBuilder.Core.Validation;

/// <summary>One step in the getting-started sequence.</summary>
/// <param name="Title">Short imperative label.</param>
/// <param name="Detail">What to do, and why it has to happen here.</param>
/// <param name="Done">Whether the plan already satisfies it.</param>
/// <param name="Page">Route the step is performed on.</param>
/// <param name="Anchor">
/// Identifies the block of controls this step refers to, so the page can highlight the actual
/// field rather than only the checklist row. Pages match on this rather than on the title, which
/// is prose and will be reworded.
/// </param>
public sealed record SetupStep(string Title, string Detail, bool Done, string Page, string Anchor);

/// <summary>
/// The ordered list of things an operator must do before a deployment can run.
/// </summary>
/// <remarks>
/// <para>
/// The pages are deliberately not a locked wizard - an experienced operator loading a saved plan
/// should not have to click through five screens. But a first-time operator was given eight
/// pages and no indication of where to start, and the order genuinely matters in one place: the
/// cloud has to be chosen before signing in, because signing in to the wrong cloud means
/// cancelling and starting over.
/// </para>
/// <para>
/// Every step reports <see cref="SetupStep.Done"/> from the real plan, so this cannot drift out
/// of sync with the form the way a hard-coded tutorial would. It is a checklist, not a wizard:
/// it describes state rather than controlling navigation.
/// </para>
/// </remarks>
public static class SetupChecklist
{
    /// <param name="plan">The plan being built.</param>
    /// <param name="signedIn">Whether an Azure sign-in has completed.</param>
    /// <param name="adminPassword">
    /// Held separately from the plan because it is never persisted, so it has to be passed in.
    /// </param>
    public static IReadOnlyList<SetupStep> For(
        DeploymentPlan plan,
        bool signedIn,
        string? adminPassword)
    {
        var azure = plan.Azure;

        return
        [
            new("Choose the cloud",
                "Azure commercial, US Government, or a custom endpoint. Do this first - it cannot "
                + "be changed once a sign-in has started, and it decides which regions and images "
                + "you are offered.",
                signedIn,
                "azure",
                "cloud"),

            new("Sign in to Azure",
                "Use the browser sign-in if it is configured, otherwise the device code. Nothing "
                + "is read from your subscription until you do.",
                signedIn,
                "azure",
                "signin"),

            new("Pick a subscription",
                "Everything is created here, and it decides which regions and resource groups the "
                + "rest of the form can offer.",
                !string.IsNullOrWhiteSpace(azure.SubscriptionId),
                "azure",
                "subscription"),

            new("Pick a region",
                "Chosen before the resource group, because it decides which VM sizes and images "
                + "exist.",
                !string.IsNullOrWhiteSpace(azure.Location),
                "azure",
                "region"),

            new("Pick or name a resource group",
                "Choose an existing group from the list, or type a new name - anything not already "
                + "in the list is created for you.",
                !string.IsNullOrWhiteSpace(azure.ResourceGroup),
                "azure",
                "resourcegroup"),

            new("Set the domain and administrator",
                "The domain the lab builds and the account the DSC configurations run as.",
                !string.IsNullOrWhiteSpace(plan.Identity.DomainName)
                    && !string.IsNullOrWhiteSpace(plan.Identity.AdminUsername),
                "identity",
                "identity"),

            new("Set an administrator password",
                "Generate one if you would rather not invent it. It is held in memory only and "
                + "never written to the plan file, so record it before you leave the page.",
                PasswordPolicy.IsValid(adminPassword, plan.Identity.AdminUsername),
                "identity",
                "password"),

            new("Add the servers you want",
                "Start with a Domain Controller - almost everything else depends on one.",
                plan.Servers.Any(s => s.Enabled),
                "servers",
                "addserver"),

            new("Review and deploy",
                "Validation runs continuously, and a prerequisite check runs before anything is "
                + "created.",
                false,
                "deploy",
                "deploy")
        ];
    }

    /// <summary>
    /// The first unfinished step, or null when everything before the final review is done.
    /// </summary>
    public static SetupStep? NextStep(IReadOnlyList<SetupStep> steps) =>
        steps.FirstOrDefault(s => !s.Done);
}
