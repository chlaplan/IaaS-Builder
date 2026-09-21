using Azure.Core;
using Azure.Identity;
using IaaSBuilder.Core.Azure;
using IaaSBuilder.Core.Models;

namespace IaaSBuilder.Web.Services;

public enum SignInState
{
    SignedOut,
    /// <summary>A device code has been issued and we are waiting for the user to redeem it.</summary>
    AwaitingDeviceCode,
    /// <summary>The system browser has been sent to the Entra sign-in page.</summary>
    AwaitingBrowser,
    /// <summary>Acquiring a token from an ambient credential; no user interaction expected.</summary>
    Connecting,
    SignedIn,
    Failed
}

/// <summary>How the credential behind the current session was obtained.</summary>
public enum SignInMethod
{
    None,
    /// <summary>Redirected to Entra by the website, authorization code exchanged server-side.</summary>
    EntraRedirect,
    /// <summary>System browser opened on this machine, authorization code + PKCE.</summary>
    InteractiveBrowser,
    DeviceCode,
    AmbientCredential
}

/// <summary>
/// Holds the Azure credential for the session.
/// </summary>
/// <remarks>
/// Deliberately optional. The legacy script called <c>Connect-AzAccount</c> before the window
/// was even created, so the tool could not be opened at all without internet. Here sign-in is
/// only required to deploy or to refresh the catalog: plans can be authored, validated and
/// saved entirely offline, which is the point of the air-gapped workflow.
/// </remarks>
public sealed class AzureSession
{
    private TokenCredential? _credential;

    /// <summary>
    /// Cancels whichever sign-in is in flight. Device code waits for up to fifteen minutes and the
    /// browser flow until the tab is dealt with, so without this a sign-in started against the
    /// wrong cloud can only be escaped by killing the application.
    /// </summary>
    private CancellationTokenSource? _signInCts;

    public SignInState State { get; private set; } = SignInState.SignedOut;

    /// <summary>
    /// Sign-in page for the *selected cloud* - commercial, US Government and China each have a
    /// different one, and pasting a code into the wrong one simply fails.
    /// </summary>
    public string? DeviceCodeVerificationUri { get; private set; }

    /// <summary>The code to paste. Short-lived; see <see cref="DeviceCodeExpiresOn"/>.</summary>
    public string? DeviceCodeUserCode { get; private set; }

    public DateTimeOffset? DeviceCodeExpiresOn { get; private set; }

    public string? Error { get; private set; }
    public AzureCloud Cloud { get; private set; } = AzureCloud.Public;

    /// <summary>How the current (or most recent) credential was obtained.</summary>
    public SignInMethod Method { get; private set; } = SignInMethod.None;

    /// <summary>Display name of the signed-in account, when the flow tells us one.</summary>
    public string? AccountName { get; private set; }

    public bool IsSignedIn => State == SignInState.SignedIn && _credential is not null;

    /// <summary>True while a sign-in attempt is in flight, whichever flow was chosen.</summary>
    public bool IsBusy => State is SignInState.AwaitingDeviceCode
        or SignInState.AwaitingBrowser
        or SignInState.Connecting;

    public event Action? Changed;

    public TokenCredential Credential =>
        _credential ?? throw new InvalidOperationException("Not signed in to Azure.");

    /// <summary>
    /// Adopts the credential of a visitor who already signed in through the website's Entra
    /// redirect. Nothing interactive happens here - the redirect completed before the circuit
    /// existed - so this only proves the delegated token can actually be acquired, which is where
    /// a missing admin consent for the ARM scope shows up.
    /// </summary>
    public async Task AdoptEntraUserAsync(
        TokenCredential credential,
        AzureCloud cloud,
        string? accountName,
        CancellationToken ct = default)
    {
        Cloud = cloud;
        Error = null;
        Method = SignInMethod.EntraRedirect;
        State = SignInState.Connecting;
        Changed?.Invoke();

        try
        {
            var scope = AzureCloudEndpoints.GetResourceManagerDefaultScope(cloud);
            await credential.GetTokenAsync(new TokenRequestContext([scope]), ct);

            _credential = credential;
            AccountName = accountName;
            State = SignInState.SignedIn;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _credential = null;
            Error = ex.Message;
            State = SignInState.Failed;
        }

        Changed?.Invoke();
    }

    /// <summary>
    /// Opens the real Entra sign-in page in the system browser and completes over a loopback
    /// redirect (authorization code + PKCE). Nothing to read out or paste, and the tenant's
    /// Conditional Access, MFA and device-compliance rules all apply.
    /// </summary>
    /// <remarks>
    /// The browser opens on the machine running the server. Callers must only offer this when
    /// that machine is the user's own - see <see cref="HostingMode"/>.
    /// </remarks>
    public async Task SignInWithBrowserAsync(
        AzureCloud cloud,
        string? tenantId,
        CancellationToken ct = default)
    {
        var token = BeginSignIn(cloud, SignInMethod.InteractiveBrowser, SignInState.AwaitingBrowser, ct);
        var cts = _signInCts;
        ClearDeviceCode();
        var cancelled = false;

        try
        {
            var credential = AzureCloudEndpoints.CreateInteractiveBrowserCredential(cloud, tenantId);
            var scope = AzureCloudEndpoints.GetResourceManagerDefaultScope(cloud);
            await credential.GetTokenAsync(new TokenRequestContext([scope]), token);

            _credential = credential;
            State = SignInState.SignedIn;
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        catch (Exception ex)
        {
            _credential = null;
            Error = ex.Message;
            State = SignInState.Failed;
        }

        EndSignIn(cts, cancelled);
        Changed?.Invoke();
    }

    /// <summary>
    /// Device code flow: no browser redirect and no MSAL broker, so it works from a jump box
    /// and from a console with no default browser.
    /// </summary>
    /// <remarks>
    /// Kept as a fallback rather than the default. The code is phishable - an attacker starts the
    /// flow against their own session and talks someone into entering their code - which is why
    /// Microsoft's guidance is to block it with Conditional Access wherever it is not needed.
    /// </remarks>
    public async Task SignInAsync(AzureCloud cloud, string? tenantId, CancellationToken ct = default)
    {
        var token = BeginSignIn(cloud, SignInMethod.DeviceCode, SignInState.AwaitingDeviceCode, ct);
        var cts = _signInCts;
        ClearDeviceCode();
        var cancelled = false;

        try
        {
            var credential = AzureCloudEndpoints.CreateDeviceCodeCredential(
                cloud,
                (info, _) =>
                {
                    DeviceCodeVerificationUri = info.VerificationUri.ToString();
                    DeviceCodeUserCode = info.UserCode;
                    DeviceCodeExpiresOn = info.ExpiresOn;
                    Changed?.Invoke();
                    return Task.CompletedTask;
                },
                tenantId);

            // Force the interactive flow now rather than on the first deployment call.
            var scope = AzureCloudEndpoints.GetResourceManagerDefaultScope(cloud);
            await credential.GetTokenAsync(new TokenRequestContext([scope]), token);

            _credential = credential;
            State = SignInState.SignedIn;
            ClearDeviceCode();
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        catch (Exception ex)
        {
            _credential = null;
            Error = ex.Message;
            State = SignInState.Failed;
            ClearDeviceCode();
        }

        EndSignIn(cts, cancelled);
        Changed?.Invoke();
    }

    /// <summary>
    /// Uses whatever ambient credential is available (Azure CLI, managed identity, environment
    /// variables) which is how the tool authenticates when driven from CI.
    /// </summary>
    public async Task SignInWithDefaultCredentialAsync(
        AzureCloud cloud,
        string? tenantId,
        CancellationToken ct = default)
    {
        var token = BeginSignIn(cloud, SignInMethod.AmbientCredential, SignInState.Connecting, ct);
        var cts = _signInCts;
        var cancelled = false;

        try
        {
            var credential = AzureCloudEndpoints.CreateCredential(cloud, tenantId);
            var scope = AzureCloudEndpoints.GetResourceManagerDefaultScope(cloud);
            await credential.GetTokenAsync(new TokenRequestContext([scope]), token);

            _credential = credential;
            State = SignInState.SignedIn;
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        catch (Exception ex)
        {
            _credential = null;
            Error = ex.Message;
            State = SignInState.Failed;
        }

        EndSignIn(cts, cancelled);
        Changed?.Invoke();
    }

    public void SignOut()
    {
        CancelSignIn();
        _credential = null;
        State = SignInState.SignedOut;
        Error = null;
        Method = SignInMethod.None;
        AccountName = null;
        ClearDeviceCode();
        Changed?.Invoke();
    }

    /// <summary>True when there is something to cancel.</summary>
    public bool CanCancel => IsBusy;

    /// <summary>
    /// Abandons a sign-in in flight and returns to signed out. The underlying credential call is
    /// cancelled rather than merely ignored, so a device code stops being redeemable.
    /// </summary>
    public void CancelSignIn()
    {
        var cts = _signInCts;
        _signInCts = null;

        if (cts is null)
        {
            return;
        }

        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already finished; nothing to abandon.
        }
    }

    /// <summary>
    /// Starts a flow, replacing any previous one. Returns the token the credential call should
    /// observe, linked to the caller's own so a disconnecting circuit also tears the flow down.
    /// </summary>
    private CancellationToken BeginSignIn(AzureCloud cloud, SignInMethod method, SignInState state, CancellationToken ct)
    {
        CancelSignIn();

        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _signInCts = cts;

        Cloud = cloud;
        Error = null;
        Method = method;
        State = state;
        Changed?.Invoke();

        return cts.Token;
    }

    /// <summary>
    /// Ends a flow. A cancelled attempt is reported as such rather than as a failure, because
    /// "the user pressed Cancel" is not an error worth a red banner.
    /// </summary>
    private void EndSignIn(CancellationTokenSource? cts, bool cancelled)
    {
        if (ReferenceEquals(_signInCts, cts))
        {
            _signInCts = null;
        }

        cts?.Dispose();

        if (cancelled)
        {
            _credential = null;
            State = SignInState.SignedOut;
            Error = null;
            Method = SignInMethod.None;
            ClearDeviceCode();
        }
    }

    /// <summary>Short label for the flow that produced the current credential.</summary>
    public static string Describe(SignInMethod method) => method switch
    {
        SignInMethod.EntraRedirect => "Microsoft Entra redirect",
        SignInMethod.InteractiveBrowser => "Microsoft Entra (browser)",
        SignInMethod.DeviceCode => "device code",
        SignInMethod.AmbientCredential => "ambient credential",
        _ => "not signed in"
    };

    private void ClearDeviceCode()
    {
        DeviceCodeVerificationUri = null;
        DeviceCodeUserCode = null;
        DeviceCodeExpiresOn = null;
    }
}
