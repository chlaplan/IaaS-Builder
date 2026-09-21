using IaaSBuilder.Core.Models;
using IaaSBuilder.Core.Roles;

namespace IaaSBuilder.Core.Tests;

/// <summary>
/// Guards the default marketplace images.
/// </summary>
/// <remarks>
/// A delisted or misspelled SKU is not caught by ARM validation or What-if: the image is only
/// resolved when the VM is created, so the deployment fails several minutes in, after the
/// network and usually a domain controller already exist. These images also expire on a
/// published schedule, which is how the legacy script ended up defaulting to "19h2-ent" and
/// "20h1-evd-o365pp" long after both were removed from the Marketplace.
/// </remarks>
public class ImageDefaultsTests
{
    private static ImageReferenceSpec WorkstationImage =>
        RoleCatalog.Get(ServerRole.Workstation).DefaultImage;

    private static ImageReferenceSpec AvdImage => new AvdSpec().Image;

    /// <summary>
    /// The two client defaults live in different files (the role table and AvdSpec). Before they
    /// shared a constant it was possible to bump one and leave the other on a retired build.
    /// </summary>
    [Fact]
    public void The_workstation_and_avd_clients_are_the_same_windows_version()
    {
        Assert.Contains(ImageDefaults.WindowsClientVersion, WorkstationImage.Sku);
        Assert.Contains(ImageDefaults.WindowsClientVersion, AvdImage.Sku);
    }

    [Theory]
    [InlineData("19h2")]
    [InlineData("20h1")]
    [InlineData("21h2")]
    [InlineData("22h2")]
    [InlineData("23h2")]
    [InlineData("win10")]
    public void No_default_image_uses_a_retired_windows_client(string retired)
    {
        foreach (var image in AllDefaultImages())
        {
            Assert.DoesNotContain(retired, image.Sku, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Azure image SKUs are lowercase and hyphenated. A wrong-cased or underscored string is
    /// accepted by the template and only rejected by the platform at VM-creation time.
    /// </summary>
    [Fact]
    public void Client_skus_are_lowercase_and_hyphenated()
    {
        foreach (var image in new[] { WorkstationImage, AvdImage })
        {
            Assert.Equal(image.Sku.ToLowerInvariant(), image.Sku);
            Assert.DoesNotContain('_', image.Sku);
            Assert.False(string.IsNullOrWhiteSpace(image.Publisher));
            Assert.False(string.IsNullOrWhiteSpace(image.Offer));
        }
    }

    [Fact]
    public void The_workstation_uses_a_single_session_client_and_avd_uses_multi_session()
    {
        // Under the Windows-11 offer, "-ent" is single session; multi-session lives in the
        // office-365 offer as "-avd-m365". Swapping them silently licenses the lab wrongly.
        Assert.Equal("Windows-11", WorkstationImage.Offer);
        Assert.EndsWith("-ent", WorkstationImage.Sku);

        Assert.Equal("office-365", AvdImage.Offer);
        Assert.EndsWith("-avd-m365", AvdImage.Sku);
    }

    /// <summary>
    /// Deliberately pinned. Exchange 2019 and SharePoint 2019 are shipped as roles by this tool
    /// and neither is supported on Windows Server 2025, so moving the server default forward
    /// would break those DSC configurations.
    /// </summary>
    [Fact]
    public void The_server_default_stays_on_a_release_the_shipped_roles_support()
    {
        Assert.Equal("2022-datacenter-azure-edition", ImageDefaults.WindowsServerSku);

        var serverRoles = new[]
        {
            ServerRole.DomainController, ServerRole.AdditionalDomainController, ServerRole.Adfs,
            ServerRole.Exchange, ServerRole.SccmPrimarySite, ServerRole.SccmDistributionPoint,
            ServerRole.MemberServer
        };

        // Asserted on the underlying Windows Server release rather than the publisher, because
        // the Configuration Manager primary site runs a marketplace SQL image - it needs SQL on
        // its own VM. Those images encode the OS in the offer ("sql2019-ws2022"), so the 2022
        // requirement is still checked, just through a different field.
        foreach (var role in serverRoles)
        {
            var image = RoleCatalog.Get(role).DefaultImage;

            if (string.Equals(image.Publisher, ImageDefaults.SqlPublisher, StringComparison.Ordinal))
            {
                Assert.EndsWith("-ws2022", image.Offer);
            }
            else
            {
                Assert.Equal("MicrosoftWindowsServer", image.Publisher);
                Assert.StartsWith("2022-", image.Sku);
            }
        }
    }

    /// <summary>
    /// Fails once the default client build is out of support, which is the point: the failure
    /// mode otherwise is a deployment that dies at VM creation inside a disconnected enclave,
    /// where diagnosing it is expensive. Bump <see cref="ImageDefaults.WindowsClientVersion"/>
    /// and the matching end-of-servicing date together.
    /// </summary>
    [Fact]
    public void The_default_client_build_is_still_in_support()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        Assert.True(today < ImageDefaults.WindowsClientEndOfServicing,
            $"Windows 11 {ImageDefaults.WindowsClientVersion} went out of support on " +
            $"{ImageDefaults.WindowsClientEndOfServicing:yyyy-MM-dd} and is likely to be removed " +
            "from the Azure Marketplace. Update ImageDefaults.WindowsClientVersion and " +
            "WindowsClientEndOfServicing, then re-run a catalog refresh to confirm the new SKU.");
    }

    private static IEnumerable<ImageReferenceSpec> AllDefaultImages()
    {
        foreach (var definition in RoleCatalog.All)
        {
            yield return definition.DefaultImage;
        }

        yield return AvdImage;
    }
}
