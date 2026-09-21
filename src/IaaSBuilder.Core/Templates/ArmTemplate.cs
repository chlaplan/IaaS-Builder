using System.Text;
using System.Text.Json;

namespace IaaSBuilder.Core.Templates;

/// <summary>The scope an ARM template must be submitted at.</summary>
public enum ArmDeploymentScope
{
    ResourceGroup,
    Subscription
}

/// <summary>Metadata about a single ARM template parameter.</summary>
public sealed record ArmParameterInfo(string Name, string Type, bool HasDefault)
{
    public bool IsSecure =>
        Type.Equals("securestring", StringComparison.OrdinalIgnoreCase) ||
        Type.Equals("secureObject", StringComparison.OrdinalIgnoreCase);

    public bool IsRequired => !HasDefault;
}

/// <summary>
/// An ARM template on disk, together with its declared parameter contract.
/// </summary>
/// <remarks>
/// Knowing the contract lets the deployer pass only parameters the template actually
/// declares, and fail fast on required parameters that were never supplied. The legacy
/// script splatted a fixed <c>$commonVariables</c> hashtable into every template and
/// relied on all of them happening to accept the same 20 parameters.
/// </remarks>
public sealed class ArmTemplate
{
    /// <summary>
    /// ARM templates are authored as JSONC: the shipped templates carry <c>//</c> comments
    /// and trailing commas, both of which are invalid in strict JSON.
    /// </summary>
    internal static readonly JsonDocumentOptions ArmJsonOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private ArmTemplate(
        string path,
        IReadOnlyDictionary<string, ArmParameterInfo> parameters,
        ArmDeploymentScope scope)
    {
        Path = path;
        Parameters = parameters;
        Scope = scope;
    }

    public string Path { get; }
    public IReadOnlyDictionary<string, ArmParameterInfo> Parameters { get; }

    /// <summary>
    /// Where this template has to be submitted. Read from <c>$schema</c> rather than configured,
    /// because the template itself is the only authority on it and getting it wrong produces a
    /// confusing ARM error rather than an obvious one.
    /// </summary>
    public ArmDeploymentScope Scope { get; }

    public static ArmTemplate Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"ARM template not found: {path}", path);
        }

        using var document = JsonDocument.Parse(File.ReadAllText(path), ArmJsonOptions);

        var parameters = new Dictionary<string, ArmParameterInfo>(StringComparer.OrdinalIgnoreCase);
        if (document.RootElement.TryGetProperty("parameters", out var parametersElement) &&
            parametersElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in parametersElement.EnumerateObject())
            {
                var type = property.Value.TryGetProperty("type", out var typeElement)
                    ? typeElement.GetString() ?? "string"
                    : "string";

                var hasDefault = property.Value.TryGetProperty("defaultValue", out _);
                parameters[property.Name] = new ArmParameterInfo(property.Name, type, hasDefault);
            }
        }

        var schema = document.RootElement.TryGetProperty("$schema", out var schemaElement)
            ? schemaElement.GetString() ?? ""
            : "";

        // Mission Landing Zone is subscription-scoped: it creates its own resource groups, one per
        // tier, so there is no resource group to deploy it into.
        var scope = schema.Contains("subscriptionDeploymentTemplate", StringComparison.OrdinalIgnoreCase)
            ? ArmDeploymentScope.Subscription
            : ArmDeploymentScope.ResourceGroup;

        return new ArmTemplate(path, parameters, scope);
    }

    /// <summary>
    /// Drops values the template does not declare and reports required parameters that
    /// were not supplied.
    /// </summary>
    public BoundParameters Bind(IReadOnlyDictionary<string, object?> values)
    {
        var bound = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var ignored = new List<string>();

        foreach (var (key, value) in values)
        {
            // Normalise to the casing the template declares, so callers can be relaxed
            // about names like BigIP_VM1_Name vs BIGIP_VM1_Name.
            if (Parameters.TryGetValue(key, out var info))
            {
                bound[info.Name] = value;
            }
            else
            {
                ignored.Add(key);
            }
        }

        var missing = Parameters.Values
            .Where(p => p.IsRequired && !bound.ContainsKey(p.Name))
            .Select(p => p.Name)
            .ToList();

        return new BoundParameters(bound, ignored, missing);
    }

    /// <summary>
    /// The template JSON, normalised for the ARM deployment API.
    /// </summary>
    /// <remarks>
    /// The comments and trailing commas the templates are authored with are legal JSONC but
    /// would make the deployment request body invalid JSON, so they are stripped here.
    /// </remarks>
    public string ReadContent()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path), ArmJsonOptions);

        // GetRawText() would hand back the original span, comments and all, so re-write it.
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            document.RootElement.WriteTo(writer);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}

/// <param name="Values">Parameters accepted by the template.</param>
/// <param name="Ignored">Supplied values the template does not declare.</param>
/// <param name="Missing">Required parameters with no supplied value and no default.</param>
public sealed record BoundParameters(
    IReadOnlyDictionary<string, object?> Values,
    IReadOnlyList<string> Ignored,
    IReadOnlyList<string> Missing);
