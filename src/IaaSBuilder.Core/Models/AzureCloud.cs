namespace IaaSBuilder.Core.Models;

/// <summary>
/// Which Azure cloud the plan targets. Replaces the hard-coded
/// Connect-AzAccount / Connect-AzAccount -Environment AzureUSGovernment menu branches.
/// </summary>
public enum AzureCloud
{
    Public,
    UsGovernment,
    China,

    /// <summary>
    /// A cloud whose endpoints the SDK does not ship - notably the air-gapped US Government
    /// Secret / Top Secret regions used for IL6. Endpoints come from <c>cloud.json</c> beside
    /// the executable; see <c>IaaSBuilder.Core.Azure.CustomCloud</c>.
    /// </summary>
    Custom
}
