using IaaSBuilder.Core;
using IaaSBuilder.Core.Azure;
using IaaSBuilder.Core.Models;
using IaaSBuilder.Core.Validation;

namespace IaaSBuilder.Core.Tests;

/// <summary>
/// The air-gapped cloud path. These matter because the target environment (Azure Government
/// Secret / IL6) has endpoints that are not public, so they cannot be compiled in and there is
/// no way to smoke-test the real values from here.
/// </summary>
[Collection(nameof(CustomCloudTests))]
[CollectionDefinition(nameof(CustomCloudTests), DisableParallelization = true)]
public sealed class CustomCloudTests : IDisposable
{
    public CustomCloudTests() => CustomCloud.Clear();

    public void Dispose() => CustomCloud.Clear();

    private static DeploymentPlan Plan()
    {
        var plan = PlanFactory.CreateDefault("contoso", "contoso.local");
        plan.Azure.SubscriptionId = Guid.NewGuid().ToString();
        return plan;
    }

    private static CloudDefinition Valid() => new()
    {
        Name = "Azure Government Secret",
        AuthorityHost = new Uri("https://login.example.gov/"),
        ResourceManagerEndpoint = new Uri("https://management.example.gov/"),
        ResourceManagerAudience = "https://management.example.gov/",
        BlobSuffix = "blob.core.example.gov"
    };

    [Fact]
    public void Valid_definition_passes_validation()
    {
        Assert.Empty(Valid().Validate());
    }

    [Fact]
    public void Missing_endpoints_are_reported_individually()
    {
        var errors = new CloudDefinition { Name = "x" }.Validate();

        Assert.Contains(errors, e => e.Contains("authorityHost"));
        Assert.Contains(errors, e => e.Contains("resourceManagerEndpoint"));
        Assert.Contains(errors, e => e.Contains("resourceManagerAudience"));
        Assert.Contains(errors, e => e.Contains("blobSuffix"));
    }

    [Fact]
    public void Http_endpoints_are_rejected()
    {
        var definition = Valid() with { ResourceManagerEndpoint = new Uri("http://management.example.gov/") };

        Assert.Contains(definition.Validate(), e => e.Contains("https"));
    }

    [Fact]
    public void Blob_suffix_must_not_be_a_url()
    {
        var definition = Valid() with { BlobSuffix = "https://blob.core.example.gov" };

        Assert.Contains(definition.Validate(), e => e.Contains("bare DNS suffix"));
    }

    [Fact]
    public void Endpoints_resolve_from_the_loaded_definition()
    {
        CustomCloud.Set(Valid());

        Assert.Equal(new Uri("https://login.example.gov/"),
            AzureCloudEndpoints.GetAuthorityHost(AzureCloud.Custom));
        Assert.Equal(new Uri("https://management.example.gov/"),
            AzureCloudEndpoints.GetResourceManagerEndpoint(AzureCloud.Custom));
        Assert.Equal("blob.core.example.gov", AzureCloudEndpoints.GetBlobSuffix(AzureCloud.Custom));
        Assert.Equal("Azure Government Secret", AzureCloudEndpoints.GetDisplayName(AzureCloud.Custom));
    }

    [Fact]
    public void Arm_environment_uses_the_supplied_audience()
    {
        CustomCloud.Set(Valid() with { ResourceManagerAudience = "https://audience.example.gov" });

        var environment = AzureCloudEndpoints.GetArmEnvironment(AzureCloud.Custom);

        Assert.Equal("https://audience.example.gov", environment.DefaultScope[..^"/.default".Length]);
    }

    [Fact]
    public void Unconfigured_custom_cloud_throws_an_actionable_message()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => AzureCloudEndpoints.GetAuthorityHost(AzureCloud.Custom));

        Assert.Contains("cloud.json", ex.Message);
    }

    [Fact]
    public void The_built_in_clouds_never_need_a_definition()
    {
        foreach (var cloud in new[] { AzureCloud.Public, AzureCloud.UsGovernment, AzureCloud.China })
        {
            Assert.NotNull(AzureCloudEndpoints.GetAuthorityHost(cloud));
            Assert.False(string.IsNullOrWhiteSpace(AzureCloudEndpoints.GetArmEnvironment(cloud).DefaultScope));
            Assert.False(string.IsNullOrWhiteSpace(AzureCloudEndpoints.GetBlobSuffix(cloud)));
        }
    }

    [Fact]
    public void A_plan_carried_into_an_enclave_without_cloud_json_fails_validation_rather_than_crashing()
    {
        var plan = Plan();
        plan.Azure.Cloud = AzureCloud.Custom;

        using var secrets = new DeploymentSecrets("Sup3rSecret!Lab");
        var result = new DeploymentPlanValidator().Validate(plan, secrets);

        Assert.Contains(result.Issues,
            i => i.Severity == ValidationSeverity.Error && i.Path == "azure.cloud");
    }

    [Fact]
    public void A_configured_enclave_validates_the_same_plan_cleanly()
    {
        CustomCloud.Set(Valid());

        var plan = Plan();
        plan.Azure.Cloud = AzureCloud.Custom;

        using var secrets = new DeploymentSecrets("Sup3rSecret!Lab");
        var result = new DeploymentPlanValidator().Validate(plan, secrets);

        Assert.DoesNotContain(result.Issues, i => i.Path == "azure.cloud");
    }

    [Fact]
    public void TryLoad_reads_a_file_with_comments_and_reports_a_bad_one()
    {
        var directory = Directory.CreateTempSubdirectory("iaasbuilder-cloud");
        try
        {
            // Absent file is not an error - that is the normal commercial case.
            Assert.False(CustomCloud.TryLoad(directory.FullName, out var noError));
            Assert.Null(noError);

            var path = Path.Combine(directory.FullName, CustomCloud.FileName);
            File.WriteAllText(path, """
                {
                  // Operators will comment these files; the sample ships with comments.
                  "name": "Enclave",
                  "authorityHost": "https://login.example.gov/",
                  "resourceManagerEndpoint": "https://management.example.gov/",
                  "resourceManagerAudience": "https://management.example.gov/",
                  "blobSuffix": "blob.core.example.gov"
                }
                """);

            Assert.True(CustomCloud.TryLoad(directory.FullName, out var loadError));
            Assert.Null(loadError);
            Assert.Equal("Enclave", CustomCloud.Definition!.Name);
            Assert.Equal(path, CustomCloud.LoadedFrom);

            CustomCloud.Clear();
            File.WriteAllText(path, """{ "name": "Enclave" }""");

            Assert.False(CustomCloud.TryLoad(directory.FullName, out var badError));
            Assert.NotNull(badError);
            Assert.Contains("authorityHost", badError);
            Assert.False(CustomCloud.IsConfigured);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void The_shipped_sample_is_a_complete_and_valid_shape()
    {
        var directory = Directory.CreateTempSubdirectory("iaasbuilder-cloud-sample");
        try
        {
            var sample = Path.Combine(RepoRoot.Path, "cloud.sample.json");
            Assert.True(File.Exists(sample), $"{sample} should ship so operators have a template.");

            File.Copy(sample, Path.Combine(directory.FullName, CustomCloud.FileName));

            Assert.True(CustomCloud.TryLoad(directory.FullName, out var error), error);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
