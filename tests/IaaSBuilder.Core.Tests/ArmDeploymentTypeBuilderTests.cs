using System.ClientModel.Primitives;
using System.Reflection;
using Azure.ResourceManager.Resources;
using Azure.ResourceManager.Resources.Models;

namespace IaaSBuilder.Core.Tests;

/// <summary>
/// Azure SDK models are serialised through a source-generated <see cref="ModelReaderWriterContext"/>
/// per assembly. If a type the SDK still uses internally is missing from that context, nothing
/// fails at compile time - it throws at runtime, at the moment a response is deserialised, which
/// for a deployment is after Azure has already started creating resources.
///
/// That is exactly what Azure.ResourceManager.Resources 1.12.0 did to the ArmDeployment* family.
/// These tests assert the registration directly, so the diagnosis is one line of test output
/// rather than another failed deployment.
/// </summary>
public class ArmDeploymentTypeBuilderTests
{
    public static TheoryData<Type> DeploymentModels =>
    [
#pragma warning disable CS0618 // deliberately asserting on the obsolete types we still deploy through
        typeof(ArmDeploymentData),
        typeof(ArmDeploymentPropertiesExtended),
        typeof(ArmDeploymentContent),
        typeof(ArmDeploymentValidateResult),
#pragma warning restore CS0618
    ];

    /// <summary>Types read back when a deployment or validation long-running operation completes.</summary>
    [Theory]
    [MemberData(nameof(DeploymentModels))]
    public void Deployment_models_are_registered_with_the_generated_context(Type model)
    {
        var context = ContextFor(model);

        var exception = Record.Exception(() => context.GetTypeBuilder(model));

        Assert.True(
            exception is null,
            $"""
             {model.Name} is not registered with {context.GetType().Name}
             (Azure.ResourceManager.Resources {model.Assembly.GetName().Version}).

             The SDK still deserialises this type when a deployment completes, so every deploy and
             every what-if will fail with "No ModelReaderWriterTypeBuilder found for {model.Name}"
             roughly twenty seconds in, after ARM has begun creating resources.

             This is what 1.12.0 does. The package is pinned to 1.11.2 for this reason - see the
             comment in IaaSBuilder.Core.csproj before changing it.
             """);
    }

    /// <summary>
    /// A control: if this ever fails too, the problem is the context plumbing generally rather
    /// than the deprecated deployment types, and the pin is not the answer.
    /// </summary>
    [Fact]
    public void Non_deprecated_models_are_registered()
    {
        var context = ContextFor(typeof(ResourceGroupData));

        Assert.Null(Record.Exception(() => context.GetTypeBuilder(typeof(ResourceGroupData))));
    }

    private static ModelReaderWriterContext ContextFor(Type model)
    {
        var contextType = model.Assembly.GetTypes()
            .FirstOrDefault(t => typeof(ModelReaderWriterContext).IsAssignableFrom(t) && !t.IsAbstract);

        Assert.True(contextType is not null,
            $"No ModelReaderWriterContext was generated into {model.Assembly.GetName().Name}.");

        var instance = contextType!
            .GetProperty("Default", BindingFlags.Public | BindingFlags.Static)?
            .GetValue(null) as ModelReaderWriterContext;

        Assert.True(instance is not null, $"{contextType.Name} has no static Default instance.");

        return instance!;
    }
}
