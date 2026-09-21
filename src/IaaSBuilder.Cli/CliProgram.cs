using IaaSBuilder.Core;
using IaaSBuilder.Core.Azure;
using IaaSBuilder.Core.Catalog;
using IaaSBuilder.Core.Deployment;
using IaaSBuilder.Core.Dsc;
using IaaSBuilder.Core.Models;
using IaaSBuilder.Core.Serialization;
using IaaSBuilder.Core.Templates;
using IaaSBuilder.Core.Validation;

namespace IaaSBuilder.Cli;

/// <summary>
/// Headless front end over <c>IaaSBuilder.Core</c>.
/// </summary>
/// <remarks>
/// The legacy tool could only ever be driven by a human clicking a WPF window, so it could
/// not run in a pipeline, could not be smoke tested, and could not be used over a plain SSH
/// session on a hardened jump box. The CLI and the web UI share the same engine, so
/// whatever the UI can build, a pipeline can build identically.
/// </remarks>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintUsage();
            return args.Length == 0 ? 1 : 0;
        }

        using var cancellation = new CancellationTokenSource();        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            Console.WriteLine();
            Console.WriteLine("Cancellation requested; waiting for in-flight deployments to stop...");
            cancellation.Cancel();
        };

        try
        {
            if (UnknownOption(args) is { } unknown)
            {
                return Fail($"Unknown option '{unknown}' for command '{args[0].ToLowerInvariant()}'.");
            }

            return args[0].ToLowerInvariant() switch
            {
                "init" => await InitAsync(args, cancellation.Token),
                "validate" => await ValidateAsync(args),
                "plan" => await PlanAsync(args),
                "deploy" => await DeployAsync(args, whatIf: false, cancellation.Token),
                "whatif" => await DeployAsync(args, whatIf: true, cancellation.Token),
                "catalog" => await CatalogAsync(args, cancellation.Token),
                _ => Fail($"Unknown command '{args[0]}'.")
            };
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Cancelled.");
            return 130;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> InitAsync(string[] args, CancellationToken ct)
    {
        var path = GetOption(args, "--out") ?? "plan.json";
        var prefix = GetOption(args, "--prefix") ?? "lab";
        var domain = GetOption(args, "--domain") ?? "contoso.local";

        var plan = PlanFactory.CreateDefault(prefix, domain);
        await DeploymentPlanSerializer.SaveAsync(plan, path, ct);

        Console.WriteLine($"Wrote starter plan to {Path.GetFullPath(path)}");
        Console.WriteLine("Edit it, then run:  iaasbuilder validate --plan " + path);
        return 0;
    }

    private static async Task<int> ValidateAsync(string[] args)
    {
        var (plan, contentRoot) = await LoadPlanAsync(args);
        using var secrets = ReadSecrets(args, required: false);

        var tokens = DscPackageInspector.GetAvailableRoleTokens(
            Path.Combine(contentRoot, plan.Artifacts.DscPackagePath));

        var result = new DeploymentPlanValidator(tokens.Count > 0 ? tokens : null)
            .Validate(plan, secrets.HasAdminPassword ? secrets : null);

        // Same catalog checks the UI runs continuously. Offline and entirely optional: with no
        // snapshot on disk there is nothing to compare against and nothing is reported.
        var catalog = await new CatalogFileStore(Path.Combine(contentRoot, "catalog.json"))
            .TryLoadAsync();
        var preflight = CatalogPreflight.Check(plan, catalog);

        if (preflight.Count > 0)
        {
            result = new ValidationResult([.. result.Issues, .. preflight]);
        }

        foreach (var issue in result.Issues)
        {
            var colour = issue.Severity == ValidationSeverity.Error ? ConsoleColor.Red : ConsoleColor.Yellow;
            Write(colour, $"  {issue}");
        }

        if (result.IsValid)
        {
            Write(ConsoleColor.Green, $"Plan is valid ({result.Warnings.Count()} warning(s)).");
            return 0;
        }

        Write(ConsoleColor.Red, $"Plan has {result.Errors.Count()} error(s).");
        return 1;
    }

    private static async Task<int> PlanAsync(string[] args)
    {
        var (plan, contentRoot) = await LoadPlanAsync(args);

        // 'plan' only prints the graph through a NullDeploymentService - nothing is sent
        // anywhere and no password leaves the process - so it must never block on a prompt.
        using var secrets = ReadSecretsForPreview(plan);

        var orchestrator = new DeploymentOrchestrator(
            new NullDeploymentService(), new TemplateResolver(contentRoot));

        var graph = orchestrator.BuildGraph(plan, secrets);
        var waves = graph.GetExecutionWaves();
        var byId = graph.Steps.ToDictionary(s => s.Id, StringComparer.OrdinalIgnoreCase);

        Console.WriteLine($"{graph.Steps.Count} step(s) in {waves.Count} wave(s).");
        Console.WriteLine("Steps in the same wave run concurrently.");
        Console.WriteLine();

        for (var i = 0; i < waves.Count; i++)
        {
            Console.WriteLine($"Wave {i + 1}:");
            foreach (var id in waves[i])
            {
                Console.WriteLine($"  - {byId[id].DisplayName}");
            }
        }

        return 0;
    }

    private static async Task<int> DeployAsync(string[] args, bool whatIf, CancellationToken ct)
    {
        var (plan, contentRoot) = await LoadPlanAsync(args);
        using var secrets = ReadSecrets(args, required: true);

        var credential = AzureCloudEndpoints.CreateCredential(plan.Azure.Cloud, plan.Azure.TenantId);
        var templates = new TemplateResolver(contentRoot);
        var azure = new AzureDeploymentService(credential, plan.Azure.Cloud, templates);
        var orchestrator = new DeploymentOrchestrator(azure, templates);

        var graph = orchestrator.BuildGraph(plan, secrets, new OrchestrationOptions { WhatIf = whatIf });

        Console.WriteLine(whatIf
            ? $"Validating {graph.Steps.Count} deployment(s) against Azure; nothing will be created."
            : $"Deploying {graph.Steps.Count} step(s) to resource group '{plan.Azure.ResourceGroup}'.");
        Console.WriteLine();

        var progress = new Progress<DeploymentProgress>(p =>
        {
            var (colour, symbol) = p.Status switch
            {
                StepStatus.Running => (ConsoleColor.Cyan, ">"),
                StepStatus.Succeeded => (ConsoleColor.Green, "+"),
                StepStatus.Failed => (ConsoleColor.Red, "x"),
                StepStatus.Skipped => (ConsoleColor.Yellow, "-"),
                StepStatus.Cancelled => (ConsoleColor.Yellow, "!"),
                _ => (ConsoleColor.Gray, ".")
            };

            var duration = p.Duration is { } d ? $" [{d.TotalSeconds:F0}s]" : "";
            var message = p.Message is null ? "" : $" - {p.Message}";
            Write(colour, $"  {symbol} {p.DisplayName}{duration}{message}");
        });

        var result = await graph.RunAsync(progress, ct);

        Console.WriteLine();
        if (result.Succeeded)
        {
            Write(ConsoleColor.Green, whatIf ? "All deployments validated." : "All deployments succeeded.");
            return 0;
        }

        Write(ConsoleColor.Red, $"{result.Failed.Count()} step(s) failed:");
        foreach (var step in result.Failed)
        {
            Write(ConsoleColor.Red, $"  - {step.DisplayName}: {step.Error}");
        }

        return 1;
    }

    private static async Task<int> CatalogAsync(string[] args, CancellationToken ct)
    {
        var path = GetOption(args, "--out") ?? "catalog.json";
        var subscriptionId = GetOption(args, "--subscription")
                             ?? throw new ArgumentException("--subscription is required.");

        var cloud = Enum.Parse<AzureCloud>(GetOption(args, "--cloud") ?? "Public", ignoreCase: true);
        var store = new CatalogFileStore(path);
        var credential = AzureCloudEndpoints.CreateCredential(cloud, GetOption(args, "--tenant"));

        Console.WriteLine($"Refreshing catalog for subscription {subscriptionId} ({cloud})...");
        var catalog = await new AzureCatalogSource(credential, cloud, subscriptionId, store).GetAsync(ct);

        Console.WriteLine($"  Regions:           {catalog.Locations.Count}");
        Console.WriteLine($"  AVD regions:       {catalog.GetAvdLocations().Count()}");
        Console.WriteLine($"  Regions w/ sizes:  {catalog.VmSizesByLocation.Count}");
        Console.WriteLine($"  Image SKU sets:    {catalog.ImageSkus.Count}");
        Console.WriteLine();
        Write(ConsoleColor.Green, $"Snapshot written to {store.Path}");
        Console.WriteLine("Copy this file alongside the application to populate the UI on a disconnected machine.");
        return 0;
    }

    private static async Task<(DeploymentPlan Plan, string ContentRoot)> LoadPlanAsync(string[] args)
    {
        var path = GetOption(args, "--plan") ?? "plan.json";
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Plan file not found: {Path.GetFullPath(path)}");
        }

        var contentRoot = GetOption(args, "--content-root") ?? AppContext.BaseDirectory;

        // An enclave cloud's endpoints are not compiled into the SDK; they travel beside the
        // app. A malformed file is reported now rather than as a cryptic failure at sign-in.
        if (!CustomCloud.TryLoad(contentRoot, out var cloudError) && cloudError is not null)
        {
            Console.Error.WriteLine($"{CustomCloud.FileName}: {cloudError}");
        }

        return (await DeploymentPlanSerializer.LoadAsync(path), contentRoot);
    }

    /// <summary>
    /// Secrets for the read-only 'plan' preview.
    /// </summary>
    /// <remarks>
    /// Prompting here would make a read-only command hang waiting on input in scripts and CI,
    /// but <c>BuildGraph</c> validates the plan and a password is one of the things it checks.
    /// So an obviously-fake placeholder stands in when the caller supplied nothing. It is only
    /// ever bound into template parameters that are then discarded - 'plan' uses
    /// <c>NullDeploymentService</c>, so nothing reaches Azure and nothing is printed.
    /// </remarks>
    private static DeploymentSecrets ReadSecretsForPreview(DeploymentPlan plan)
    {
        var password = Environment.GetEnvironmentVariable("IAASBUILDER_ADMIN_PASSWORD");

        return string.IsNullOrEmpty(password) && plan.Identity.AdminPasswordSecret is null
            ? new DeploymentSecrets(PreviewPlaceholderPassword)
            : new DeploymentSecrets(password ?? "");
    }

    /// <summary>Never used for a real deployment; see <see cref="ReadSecretsForPreview"/>.</summary>
    private const string PreviewPlaceholderPassword = "PlanPreviewPlaceholder1!";

    private static DeploymentSecrets ReadSecrets(string[] args, bool required)
    {
        // Environment first so pipelines never put a password on a command line.
        var password = Environment.GetEnvironmentVariable("IAASBUILDER_ADMIN_PASSWORD");

        if (string.IsNullOrEmpty(password) && required)
        {
            Console.Write("Administrator password: ");
            password = ReadPasswordMasked();
            Console.WriteLine();
        }

        return new DeploymentSecrets(password ?? "");
    }

    private static string ReadPasswordMasked()
    {
        var buffer = new System.Text.StringBuilder();

        while (true)
        {
            var key = Console.ReadKey(intercept: true);

            switch (key.Key)
            {
                case ConsoleKey.Enter:
                    return buffer.ToString();
                case ConsoleKey.Backspace when buffer.Length > 0:
                    buffer.Length--;
                    Console.Write("\b \b");
                    break;
                default:
                    if (!char.IsControl(key.KeyChar))
                    {
                        buffer.Append(key.KeyChar);
                        Console.Write('*');
                    }

                    break;
            }
        }
    }

    private static string? GetOption(string[] args, string name)
    {
        var index = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    /// <summary>
    /// Options each command understands. Anything else is a typo and must be refused.
    /// </summary>
    /// <remarks>
    /// <see cref="GetOption"/> scans for a flag and returns null when it is absent, so an
    /// unrecognised option used to be discarded in silence and the default applied instead. That is
    /// dangerous rather than merely untidy: <c>--cluod UsGovernment</c> would have refreshed the
    /// catalog against the commercial cloud, and <c>--output plan.json</c> would have written the
    /// plan to the working directory while reporting success.
    /// </remarks>
    private static readonly Dictionary<string, string[]> KnownOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["init"] = ["--out", "--prefix", "--domain"],
        ["validate"] = ["--plan", "--content-root"],
        ["plan"] = ["--plan", "--content-root"],
        ["whatif"] = ["--plan", "--content-root"],
        ["deploy"] = ["--plan", "--content-root"],
        ["catalog"] = ["--out", "--subscription", "--cloud", "--tenant"]
    };

    /// <summary>The first option the command does not recognise, or null when all are valid.</summary>
    private static string? UnknownOption(string[] args)
    {
        if (!KnownOptions.TryGetValue(args[0], out var allowed))
        {
            return null;
        }

        for (var i = 1; i < args.Length; i++)
        {
            var token = args[i];
            if (!token.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            if (!allowed.Contains(token, StringComparer.OrdinalIgnoreCase))
            {
                return token;
            }

            // Skip the value so a value that happens to look like an option is not flagged.
            i++;
        }

        return null;
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        PrintUsage();
        return 1;
    }

    private static void Write(ConsoleColor colour, string message)
    {
        var previous = Console.ForegroundColor;
        Console.ForegroundColor = colour;
        Console.WriteLine(message);
        Console.ForegroundColor = previous;
    }

    private static void PrintUsage() => Console.WriteLine(
        """
        IaaS Builder - build Azure IaaS lab environments from a declarative plan.

        USAGE
          iaasbuilder <command> [options]

        COMMANDS
          init       Write a starter plan file.
          validate   Check a plan for errors without contacting Azure.
          plan       Show the deployment steps and the order they will run in.
          whatif     Validate every template against Azure without creating resources.
          deploy     Build the environment.
          catalog    Refresh the offline region/size/image snapshot.

        OPTIONS
          --plan <file>          Plan file. Default: plan.json
          --content-root <dir>   Folder holding Templates/ and DSC/. Default: the app folder.
          --out <file>           Output file for 'init' and 'catalog'.
          --prefix <name>        Naming prefix for 'init'. Default: lab
          --domain <fqdn>        AD domain for 'init'. Default: contoso.local
          --subscription <guid>  Subscription for 'catalog'.
          --cloud <name>         Public | UsGovernment | China | Custom. Default: Public
                                 Custom reads endpoints from cloud.json beside the app.
          --tenant <guid>        Tenant override.

        ENVIRONMENT
          IAASBUILDER_ADMIN_PASSWORD   Administrator password. Prompted for when unset.

        EXAMPLES
          iaasbuilder init --prefix contoso --out contoso.json
          iaasbuilder validate --plan contoso.json
          iaasbuilder plan --plan contoso.json
          iaasbuilder deploy --plan contoso.json
        """);
}

/// <summary>No-op service used by the 'plan' command, which must not contact Azure.</summary>
internal sealed class NullDeploymentService : IAzureDeploymentService
{
    public Task EnsureResourceGroupAsync(DeploymentPlan plan, CancellationToken ct) => Task.CompletedTask;

    public Task<ArtifactLocation> PublishArtifactsAsync(DeploymentPlan plan, CancellationToken ct) =>
        Task.FromResult(new ArtifactLocation("https://example.invalid/", "?preview-only"));

    public Task<string?> ResolveDedicatedHostIdAsync(DeploymentPlan plan, CancellationToken ct) =>
        Task.FromResult<string?>(null);

    public Task<ArmDeploymentOutcome> DeployTemplateAsync(
        DeploymentPlan plan, string deploymentName, ArmTemplate template,
        IReadOnlyDictionary<string, object?> parameters, CancellationToken ct) =>
        Task.FromResult(new ArmDeploymentOutcome(deploymentName, "Succeeded", new Dictionary<string, object?>()));

    public Task<ArmDeploymentOutcome> ValidateTemplateAsync(
        DeploymentPlan plan, string deploymentName, ArmTemplate template,
        IReadOnlyDictionary<string, object?> parameters, CancellationToken ct) =>
        Task.FromResult(new ArmDeploymentOutcome(deploymentName, "Succeeded", new Dictionary<string, object?>()));
}
