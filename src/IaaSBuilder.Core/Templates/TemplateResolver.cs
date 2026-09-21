using IaaSBuilder.Core.Models;

namespace IaaSBuilder.Core.Templates;

/// <summary>
/// Locates ARM templates on disk and caches their parsed parameter contracts.
/// </summary>
/// <remarks>
/// Paths are resolved against an explicit content root rather than the process working
/// directory. The legacy script used bare relative paths like <c>.\Templates\Networking.json</c>,
/// so it only worked when launched from its own folder.
/// </remarks>
public sealed class TemplateResolver
{
    private readonly string _contentRoot;
    private readonly Dictionary<string, ArmTemplate> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();

    public TemplateResolver(string contentRoot) => _contentRoot = Path.GetFullPath(contentRoot);

    public string ContentRoot => _contentRoot;

    public string Resolve(string relativePath) =>
        Path.GetFullPath(Path.Combine(_contentRoot, relativePath.Replace('\\', Path.DirectorySeparatorChar)));

    public ArmTemplate Load(string relativePath)
    {
        var fullPath = Resolve(relativePath);

        lock (_gate)
        {
            if (_cache.TryGetValue(fullPath, out var cached))
            {
                return cached;
            }

            var template = ArmTemplate.Load(fullPath);
            _cache[fullPath] = template;
            return template;
        }
    }

    public ArmTemplate Networking() => Load(TemplatePaths.Networking);

    public ArmTemplate Bastion() => Load(TemplatePaths.Bastion);

    public ArmTemplate Avd() => Load(TemplatePaths.Avd);

    /// <summary>Picks the VM template variant that matches the server and plan options.</summary>
    public ArmTemplate ForServer(DeploymentPlan plan, ServerSpec server)
    {
        if (server.UseSpotInstance) return Load(TemplatePaths.VirtualMachineSpot);
        if (plan.Saca is { Enabled: true }) return Load(TemplatePaths.VirtualMachineSaca);
        return Load(TemplatePaths.VirtualMachine);
    }

    public ArmTemplate SacaNetwork(int tier) =>
        Load(tier == 3 ? TemplatePaths.Saca3TierNetwork : TemplatePaths.Saca1TierNetwork);

    public ArmTemplate SacaF5(int tier) =>
        Load(tier == 3 ? TemplatePaths.Saca3TierF5 : TemplatePaths.Saca1TierF5);

    public ArmTemplate SacaIps() => Load(TemplatePaths.Saca3TierIps);

    /// <summary>Microsoft Mission Landing Zone. Subscription-scoped, unlike every other template here.</summary>
    public ArmTemplate Mlz() => Load(TemplatePaths.Mlz);
}

public static class TemplatePaths
{
    public const string VirtualMachine = "Templates/AzureTemplate.json";
    public const string VirtualMachineSpot = "Templates/AzureTemplateSpot.json";
    public const string VirtualMachineSaca = "Templates/AzureTemplateSACA.json";
    public const string Networking = "Templates/Networking.json";
    public const string Bastion = "Templates/Bastion.json";
    public const string Avd = "Templates/AzureWVD.json";
    public const string Saca1TierNetwork = "Templates/SACA/1T_SACA_NetworkBuild.json";
    public const string Saca3TierNetwork = "Templates/SACA/3T_SACA_NetworkBuild.json";
    public const string Saca1TierF5 = "Templates/SACA/1T_SACA_F5_Deploy.json";
    public const string Saca3TierF5 = "Templates/SACA/3T_SACA_F5_Deploy.json";
    public const string Saca3TierIps = "Templates/SACA/3T_SACA_IPSDeploy.json";
    public const string Mlz = "Templates/MLZ/mlz.json";
}
