using System.Diagnostics;
using IaaSBuilder.Core.Azure;
using IaaSBuilder.Core.Catalog;
using IaaSBuilder.Core.Models;
using IaaSBuilder.Core.Pricing;
using IaaSBuilder.Core.Templates;
using IaaSBuilder.Web.Components;
using IaaSBuilder.Web.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Identity.Web;
using Microsoft.Identity.Web.UI;

// --no-browser is ours, not the host's. The command-line configuration provider treats any
// bare --switch as a key whose value is the *next* token, so leaving it in makes
// `--no-browser --urls http://...` silently swallow --urls and bind the default port instead.
var launchBrowser = !args.Contains("--no-browser", StringComparer.OrdinalIgnoreCase);
var hostArgs = args.Where(a => !string.Equals(a, "--no-browser", StringComparison.OrdinalIgnoreCase)).ToArray();

// ASP.NET's content root anchors wwwroot, so it cannot simply be forced to the app folder:
// wwwroot is published beside the binary but is NOT copied to the build output, where static
// web assets are served from the project via the generated manifest. Forcing it either way
// breaks the other layout - every CSS/JS request returns 200 with an empty body and the UI
// renders unstyled and completely non-interactive.
//
// The layouts are distinguishable: a published app has wwwroot next to the executable, a build
// output does not. Anchor to the app folder only when this is a published layout, which is also
// the only case where the app can be launched from an unrelated working directory (a shortcut,
// a scheduled task, or an operator running it from anywhere in an enclave).
var appFolder = AppContext.BaseDirectory;
var isPublishedLayout = Directory.Exists(Path.Combine(appFolder, "wwwroot"));

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = hostArgs,
    ContentRootPath = isPublishedLayout ? appFolder : null
});

// In a build output, generated assets (the scoped-CSS bundle, blazor.web.js) are not under
// wwwroot at all - they live in obj/ and the NuGet cache, and the generated
// *.staticwebassets.runtime.json maps their routes onto those real paths. ASP.NET loads that
// manifest automatically ONLY in the Development environment, so running the built exe in any
// other environment - which is exactly what Visual Studio does when you F5 a configuration
// whose launch profile does not set ASPNETCORE_ENVIRONMENT=Development - returns HTTP 500 from
// StaticAssetDevelopmentRuntimeHandler for every generated asset, and the UI never becomes
// interactive. Load it explicitly. A published layout has the real files on disk beside the
// executable and needs no manifest.
if (!isPublishedLayout && !builder.Environment.IsDevelopment())
{
    builder.WebHost.UseStaticWebAssets();
}

// Loopback only. This is an operator tool that holds subscription credentials; it should not
// be reachable from the network unless somebody deliberately hosts it.
var url = builder.Configuration["urls"] ?? "http://127.0.0.1:5099";
builder.WebHost.UseUrls(url);

// Derived from the binding rather than a switch, because the interactive browser sign-in opens a
// browser on the *server*, and offering it on a hosted site is a real failure rather than a
// cosmetic one. See HostingMode.
var hosting = HostingMode.FromUrls([url]);
builder.Services.AddSingleton(hosting);

// Optional. Configure the Entra section and visitors are redirected to Entra to sign in, which is
// the only sensible flow for a multi-user website. Leave it out - as the offline executable does,
// since an air-gapped operator cannot create an app registration - and no authentication
// middleware is added at all and nothing about the tool changes.
var entra = builder.Configuration.GetSection(EntraOptions.SectionName).Get<EntraOptions>()
            ?? new EntraOptions();
builder.Services.AddSingleton(entra);

if (entra.IsConfigured)
{
    var entraCloud = Enum.TryParse<AzureCloud>(entra.Cloud, ignoreCase: true, out var parsed)
        ? parsed
        : AzureCloud.Public;

    builder.Services.AddAuthentication(OpenIdConnectDefaults.AuthenticationScheme)
        .AddMicrosoftIdentityWebApp(options =>
        {
            options.Instance = AzureCloudEndpoints.GetAuthorityHost(entraCloud).ToString();
            options.TenantId = entra.TenantId;
            options.ClientId = entra.ClientId;
            options.ClientSecret = entra.ClientSecret;
            options.CallbackPath = entra.CallbackPath;
            options.SignedOutCallbackPath = entra.SignedOutCallbackPath;
        })
        // Delegated, not application, permissions: every ARM call is made as the visitor under
        // their own RBAC. A shared service principal would let anyone who reaches the site deploy
        // with the full rights of that principal.
        .EnableTokenAcquisitionToCallDownstreamApi([AzureCloudEndpoints.GetResourceManagerUserScope(entraCloud)])
        .AddInMemoryTokenCaches();

    builder.Services.AddCascadingAuthenticationState();
    builder.Services.AddAuthorization();

    // The session cookie carries the signed-in identity, so it must never travel in clear text.
    // Always, not SameAsRequest: a site fronted by a TLS-terminating proxy sees plain HTTP on the
    // inside, and SameAsRequest would quietly drop the protection exactly there.
    builder.Services.Configure<CookieAuthenticationOptions>(
        CookieAuthenticationDefaults.AuthenticationScheme,
        options =>
        {
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.None;
        });

    // Without this a TLS-terminating proxy makes the app build an http:// redirect_uri, which
    // Entra then rejects because the registration lists https://.
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
        options.ForwardedHeaders = ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost);

    // Brings in /MicrosoftIdentity/Account/SignIn and SignOut, which issue the redirect to Entra
    // and the sign-out round trip.
    builder.Services.AddControllersWithViews().AddMicrosoftIdentityUI();
}

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Deployment assets are resolved against the application folder, NOT the ASP.NET content root
// and NOT the working directory. Three different things that happen to coincide when you run
// from the project folder:
//
//   * ASP.NET's content root anchors wwwroot. It must stay as the host computed it - wwwroot is
//     not copied to the build output, so forcing it to the app folder makes every CSS and JS
//     request return 200 with an empty body, and the UI renders unstyled and non-interactive.
//   * Templates/, nested/, DSC/, STIG/, catalog.json and cloud.json are copied next to the
//     binary on both build and publish, so the app folder is correct for them in every layout.
//   * The working directory is whatever launched us, which for a single-file exe started from a
//     shortcut or scheduled task is not the app folder at all - the legacy script's bare
//     relative paths broke for exactly this reason.
var assetRoot = ExplicitAssetRoot(hostArgs) ?? appFolder;

// An enclave cloud (for example an air-gapped US Government Secret region) has endpoints that
// are not compiled into the Azure SDK, so they are supplied on disk next to the exe.
if (CustomCloud.TryLoad(assetRoot, out var cloudError) is false && cloudError is not null)
{
    Console.Error.WriteLine($"cloud.json: {cloudError}");
}

builder.Services.AddSingleton(new TemplateResolver(assetRoot));
builder.Services.AddSingleton(new CatalogFileStore(Path.Combine(assetRoot, "catalog.json")));

// Prices sit beside the catalog snapshot and are shipped the same way: an air-gapped enclave that
// cannot reach the retail feed can have prices by copying this file in.
builder.Services.AddSingleton(new PriceFileStore(Path.Combine(assetRoot, "prices.json")));

// A named client rather than the ambient one, because this talks to prices.azure.com and not to
// ARM - different host, no Azure credential, and a short timeout so a blocked egress path fails
// quickly instead of hanging a background task for the default 100 seconds.
builder.Services.AddHttpClient<RetailPriceSource>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
});

// Scoped, not singleton. In Blazor Server a scope is one circuit - one browser session - so this
// is what keeps the hosted website safe: a singleton would share one AzureSession (and therefore
// one person's Azure token and subscriptions), one DeploymentPlan and one in-memory admin password
// across every visitor at once. Running as the offline single-operator exe there is only ever one
// circuit, so the same registration is correct for both ways this ships.
//
// TemplateResolver and CatalogFileStore stay singletons: they are read-only and genuinely shared.
builder.Services.AddScoped<AzureSession>();
builder.Services.AddScoped<AzureDirectoryState>();
builder.Services.AddScoped<CatalogState>();
builder.Services.AddScoped<PriceState>();
builder.Services.AddScoped<PlanState>();
builder.Services.AddScoped<ValidationState>();
builder.Services.AddScoped<OperatorSettingsStore>();
builder.Services.AddScoped<ChecklistState>();
builder.Services.AddScoped<DeploymentRunner>();

var app = builder.Build();

// Static-asset failures are silent from the server's point of view: a missing wwwroot returns 200
// with an empty body, and an unloaded asset manifest returns 500 for generated files only. Either
// way the page loads unstyled and, with no blazor.web.js, completely inert - almost impossible to
// diagnose on a hardened machine with no browser dev tools.
//
// Probing the web root *directory* is too weak to catch this, because in a build layout that
// directory exists and serves hand-written files like app.css perfectly well while every
// generated asset fails. Ask the file provider for a specific generated asset instead: that is
// the one question whose answer is correct in all three layouts.
if (!app.Environment.WebRootFileProvider.GetFileInfo("_framework/blazor.web.js").Exists)
{
    Console.Error.WriteLine(
        $"WARNING: blazor.web.js cannot be resolved (content root '{app.Environment.ContentRootPath}', " +
        $"environment '{app.Environment.EnvironmentName}'). The UI will load without styling and " +
        "without interactivity. Run the published output, or 'dotnet run --project src/IaaSBuilder.Web'.");
}

foreach (var required in (string[])["Templates", "DSC"])
{
    if (!Directory.Exists(Path.Combine(assetRoot, required)))
    {
        Console.Error.WriteLine(
            $"WARNING: '{required}' not found under '{assetRoot}'. Deployments will fail when " +
            "they try to load a template or the DSC package. Pass --asset-root <dir> to point " +
            "at the folder holding Templates/ and DSC/.");
    }
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseAntiforgery();

if (entra.IsConfigured)
{
    app.UseForwardedHeaders();
    app.UseAuthentication();
    app.UseAuthorization();

    // Microsoft.Identity.Web's own controller, which owns /MicrosoftIdentity/Account/SignIn and
    // SignOut. Those are plain redirects rather than Blazor interactions on purpose: the
    // challenge has to be issued on a real HTTP request, and a SignalR circuit cannot issue one.
    app.MapControllers();
}
else if (!hosting.IsLocalOperator)
{
    // Reachable from the network with no redirect sign-in configured means the only interactive
    // option left is the device code, which is the weakest one. Say so at startup rather than
    // leaving it to be noticed on the sign-in page.
    Console.Error.WriteLine(
        "WARNING: this instance is reachable from the network but the Entra section is not " +
        "configured, so visitors can only sign in with a device code. Register an application " +
        "and fill in Entra:ClientId, Entra:TenantId and Entra:ClientSecret.");
}

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// The offline catalog is warmed per circuit instead (see MainLayout): CatalogState is scoped now,
// so resolving it from the root provider here would throw.

if (launchBrowser)
{
    app.Lifetime.ApplicationStarted.Register(() =>
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // Headless or no registered browser: the console already prints the URL.
        }
    });
}

app.Run();

// Folder holding Templates/, DSC/, catalog.json and cloud.json. Matches the CLI's
// --content-root switch, which has always meant the asset folder rather than ASP.NET's
// content root.
static string? ExplicitAssetRoot(string[] args)
{
    for (var i = 0; i < args.Length; i++)
    {
        foreach (var name in (string[])["--asset-root", "--content-root", "--contentRoot"])
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                return args[i + 1];
            }

            if (args[i].StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
            {
                return args[i][(name.Length + 1)..];
            }
        }
    }

    return null;
}