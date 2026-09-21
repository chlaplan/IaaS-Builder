namespace IaaSBuilder.Core.Models;

/// <summary>
/// Administrative trust tier, following Microsoft's Enterprise Access Model (formerly the
/// Tier 0/1/2 model that the "Securing Privileged Access" guidance describes).
/// </summary>
/// <remarks>
/// <para>
/// This is documentation, not enforcement. Grouping the Servers page by tier makes the blast
/// radius of each role visible and explains the deployment order - a Tier 0 asset has to exist
/// before anything below it can join the domain. It does <b>not</b> place the tiers in separate
/// subnets, give them separate administrative accounts, or restrict logon between them, all of
/// which a real tiered implementation requires.
/// </para>
/// <para>
/// Configuration Manager is the uncomfortable one. It is listed as Tier 1 because that is where
/// it is normally deployed, but a ConfigMgr site that manages domain controllers can execute
/// code on them, which makes it a Tier 0 asset in practice. Labs are where that distinction is
/// worth learning, so the grouping says so rather than quietly filing it under Tier 1.
/// </para>
/// </remarks>
public enum TrustTier
{
    /// <summary>Identity control plane: domain controllers, federation, certificate services.</summary>
    Tier0,

    /// <summary>Servers and applications.</summary>
    Tier1,

    /// <summary>User workstations and devices.</summary>
    Tier2
}

/// <summary>Display metadata for <see cref="TrustTier"/>.</summary>
public static class TrustTiers
{
    /// <summary>Tiers in the order they should be presented: most privileged first.</summary>
    public static readonly IReadOnlyList<TrustTier> InOrder =
        [TrustTier.Tier0, TrustTier.Tier1, TrustTier.Tier2];

    public static string DisplayNameOf(TrustTier tier) => tier switch
    {
        TrustTier.Tier0 => "Tier 0 - Identity control plane",
        TrustTier.Tier1 => "Tier 1 - Servers and applications",
        _ => "Tier 2 - Workstations"
    };

    public static string DescriptionOf(TrustTier tier) => tier switch
    {
        TrustTier.Tier0 =>
            "Direct control of identity. Compromise here is compromise of everything below it, "
            + "so these are built first and everything else depends on them.",
        TrustTier.Tier1 =>
            "Application and data servers. They trust Tier 0 for identity and are administered "
            + "by accounts that should never sign in to Tier 0.",
        _ =>
            "User devices. The most exposed and the least trusted; nothing above them should "
            + "depend on them."
    };
}
