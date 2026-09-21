namespace IaaSBuilder.Core.Models;

/// <summary>
/// The workload a VM is built for. The legacy script encoded this as a free-text
/// "role" parameter plus a dedicated copy-pasted deployment block per server.
/// </summary>
public enum ServerRole
{
    DomainController,
    AdditionalDomainController,
    CertificateAuthority,
    Adfs,
    Exchange,
    SharePoint,
    Sql,
    SccmPrimarySite,
    SccmDistributionPoint,
    Workstation,
    MemberServer
}
