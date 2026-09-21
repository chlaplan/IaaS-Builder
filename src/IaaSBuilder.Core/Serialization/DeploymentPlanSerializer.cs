using System.Text.Json;
using System.Text.Json.Serialization;
using IaaSBuilder.Core.Models;

namespace IaaSBuilder.Core.Serialization;

/// <summary>
/// Saves and loads <see cref="DeploymentPlan"/> as JSON.
/// </summary>
/// <remarks>
/// Replaces the legacy Save/Load handlers: ~150 lines of hand-written
/// <c>$upv = $loadvars | Where Name -EQ '...'</c> mappings that silently dropped every
/// combo box, every checkbox and the entire SACA tab. One serializer covers the whole
/// model, including anything added to it later.
/// </remarks>
public static class DeploymentPlanSerializer
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static string Serialize(DeploymentPlan plan) =>
        JsonSerializer.Serialize(plan, Options);

    public static DeploymentPlan Deserialize(string json)
    {
        var plan = JsonSerializer.Deserialize<DeploymentPlan>(json, Options)
                   ?? throw new InvalidDataException("Plan file deserialized to null.");

        if (plan.SchemaVersion != DeploymentPlan.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Plan schema version '{plan.SchemaVersion}' is not supported by this build " +
                $"(expected '{DeploymentPlan.CurrentSchemaVersion}').");
        }

        return plan;
    }

    public static async Task SaveAsync(DeploymentPlan plan, string path, CancellationToken ct = default)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(path, Serialize(plan), ct);
    }

    public static async Task<DeploymentPlan> LoadAsync(string path, CancellationToken ct = default) =>
        Deserialize(await File.ReadAllTextAsync(path, ct));
}
