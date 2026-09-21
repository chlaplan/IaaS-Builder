namespace IaaSBuilder.Web.Services;

/// <summary>
/// App registration used to sign visitors in when this is hosted as a website.
/// </summary>
/// <remarks>
/// <para>
/// This is the most secure sign-in the tool offers, and the only one that makes sense for a
/// multi-user site: the visitor is redirected to Entra, authenticates there under whatever
/// Conditional Access, MFA and device-compliance policy the tenant enforces, and comes back with
/// an authorization code. The code is exchanged server-side for a token the browser never sees.
/// Nothing is phishable the way a device code is, and every action is taken with that person's
/// own Azure RBAC rather than a shared service principal.
/// </para>
/// <para>
/// Entirely optional. When this is not configured the tool behaves exactly as it did before, with
/// no authentication middleware in the pipeline at all - which is what the offline executable
/// needs, since an air-gapped operator cannot create an app registration on a website they
/// cannot reach.
/// </para>
/// </remarks>
public sealed class EntraOptions
{
    public const string SectionName = "Entra";

    /// <summary>Application (client) id of the app registration.</summary>
    public string ClientId { get; set; } = "";

    /// <summary>Directory (tenant) id, or "organizations" / "common" for multi-tenant.</summary>
    public string TenantId { get; set; } = "";

    /// <summary>
    /// Client secret. Prefer a certificate or a managed identity in production; a secret is
    /// supported because it is what most people start with.
    /// </summary>
    public string ClientSecret { get; set; } = "";

    /// <summary>
    /// Which cloud the app registration lives in. A tenant exists in exactly one cloud, so when
    /// redirect sign-in is configured this also fixes the cloud the tool can reach.
    /// </summary>
    public string Cloud { get; set; } = "Public";

    /// <summary>Redirect path registered against the application. Must match Entra exactly.</summary>
    public string CallbackPath { get; set; } = "/signin-oidc";

    public string SignedOutCallbackPath { get; set; } = "/signout-callback-oidc";

    /// <summary>
    /// True once there is enough to attempt the flow. Deliberately strict: a half-filled section
    /// would add authentication middleware that then fails on every request, which on a hosted
    /// site means the tool is simply down.
    /// </summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ClientId) &&
        !string.IsNullOrWhiteSpace(TenantId) &&
        !string.IsNullOrWhiteSpace(ClientSecret);
}
