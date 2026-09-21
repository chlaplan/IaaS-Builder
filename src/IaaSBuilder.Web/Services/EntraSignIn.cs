using System.Security.Claims;
using IaaSBuilder.Core.Models;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Identity.Web;

namespace IaaSBuilder.Web.Services;

/// <summary>
/// Picks up a visitor who already completed the website's Entra redirect.
/// </summary>
/// <remarks>
/// Resolved through <see cref="IServiceProvider"/> rather than injected, because the
/// authentication services only exist when <see cref="EntraOptions.IsConfigured"/> is true. The
/// offline executable has none of them registered, and a constructor-injected dependency would
/// turn that supported configuration into a startup crash.
/// </remarks>
public static class EntraSignIn
{
    public static async Task<bool> TryAdoptAsync(
        IServiceProvider services,
        AzureSession session,
        EntraOptions options,
        CancellationToken ct = default)
    {
        var stateProvider = services.GetService<AuthenticationStateProvider>();
        var tokenAcquisition = services.GetService<ITokenAcquisition>();

        if (stateProvider is null || tokenAcquisition is null)
        {
            return false;
        }

        var user = (await stateProvider.GetAuthenticationStateAsync()).User;
        if (user.Identity is not { IsAuthenticated: true })
        {
            return false;
        }

        var cloud = ParseCloud(options.Cloud);
        var credential = new EntraUserCredential(tokenAcquisition, user, cloud);

        await session.AdoptEntraUserAsync(credential, cloud, DisplayName(user), ct);
        return session.IsSignedIn;
    }

    public static AzureCloud ParseCloud(string? value) =>
        Enum.TryParse<AzureCloud>(value, ignoreCase: true, out var cloud) ? cloud : AzureCloud.Public;

    private static string? DisplayName(ClaimsPrincipal user) =>
        user.FindFirst("preferred_username")?.Value
        ?? user.FindFirst(ClaimTypes.Upn)?.Value
        ?? user.FindFirst(ClaimTypes.Email)?.Value
        ?? user.Identity?.Name;
}
