using System.Security.Claims;
using Azure.Core;
using IaaSBuilder.Core.Azure;
using IaaSBuilder.Core.Models;
using Microsoft.Identity.Web;

namespace IaaSBuilder.Web.Services;

/// <summary>
/// Presents the signed-in visitor's delegated Entra token to the Azure SDK as a
/// <see cref="TokenCredential"/>.
/// </summary>
/// <remarks>
/// <para>
/// The redirect sign-in is an ASP.NET concern and the rest of the tool speaks
/// <see cref="TokenCredential"/>, so this is the adapter between them. Every ARM call then runs as
/// the visitor, under their own RBAC - there is no shared service principal that would let one
/// person's session deploy with another person's rights.
/// </para>
/// <para>
/// The user is captured once and passed explicitly on every acquisition. In a Blazor Server
/// circuit <c>HttpContext</c> is null after the first render, so the ambient-user overload of
/// <see cref="ITokenAcquisition"/> silently has nobody to work with and the call fails partway
/// through a deployment rather than at sign-in.
/// </para>
/// </remarks>
public sealed class EntraUserCredential : TokenCredential
{
    private readonly ITokenAcquisition _tokenAcquisition;
    private readonly ClaimsPrincipal _user;
    private readonly AzureCloud _cloud;

    public EntraUserCredential(ITokenAcquisition tokenAcquisition, ClaimsPrincipal user, AzureCloud cloud)
    {
        _tokenAcquisition = tokenAcquisition;
        _user = user;
        _cloud = cloud;
    }

    public override async ValueTask<AccessToken> GetTokenAsync(
        TokenRequestContext requestContext,
        CancellationToken cancellationToken)
    {
        var result = await _tokenAcquisition.GetAuthenticationResultForUserAsync(
            ScopesFor(requestContext),
            user: _user);

        return new AccessToken(result.AccessToken, result.ExpiresOn);
    }

    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
        GetTokenAsync(requestContext, cancellationToken).AsTask().GetAwaiter().GetResult();

    /// <summary>
    /// Translates the SDK's <c>.default</c> scope into the delegated scope the app registration
    /// actually holds. Asking Entra for <c>.default</c> on a delegated flow returns only what has
    /// already been consented, which for a freshly created registration is nothing.
    /// </summary>
    private string[] ScopesFor(TokenRequestContext context)
    {
        var scopes = context.Scopes;
        if (scopes is { Length: > 0 } && !scopes[0].EndsWith("/.default", StringComparison.OrdinalIgnoreCase))
        {
            return scopes;
        }

        return [AzureCloudEndpoints.GetResourceManagerUserScope(_cloud)];
    }
}
