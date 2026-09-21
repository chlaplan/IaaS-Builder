using System.Text.Json;
using System.Text.Json.Serialization;

namespace IaaSBuilder.Core.Azure;

/// <summary>
/// Endpoints for a cloud the Azure SDK does not ship constants for.
/// </summary>
/// <remarks>
/// Sovereign and air-gapped clouds - notably the US Government Secret and Top Secret regions
/// used for IL6 workloads - have authority hosts and Resource Manager endpoints that are not
/// public, so they cannot be compiled in. Supplying them from a file next to the executable
/// keeps the same build usable in every enclave.
/// </remarks>
public sealed record CloudDefinition
{
    /// <summary>Label shown in the UI, e.g. "Azure Government Secret".</summary>
    [JsonPropertyName("name")]
    public string Name { get; init; } = "Custom cloud";

    /// <summary>Entra ID authority, e.g. https://login.microsoftonline.eaglex.ic.gov/.</summary>
    [JsonPropertyName("authorityHost")]
    public Uri? AuthorityHost { get; init; }

    /// <summary>Azure Resource Manager endpoint.</summary>
    [JsonPropertyName("resourceManagerEndpoint")]
    public Uri? ResourceManagerEndpoint { get; init; }

    /// <summary>
    /// Token audience for Resource Manager. Usually the same as the endpoint, but it is a
    /// separate value in several clouds, so it is not inferred.
    /// </summary>
    [JsonPropertyName("resourceManagerAudience")]
    public string? ResourceManagerAudience { get; init; }

    /// <summary>Blob storage DNS suffix, used to build the DSC artifact URI.</summary>
    [JsonPropertyName("blobSuffix")]
    public string? BlobSuffix { get; init; }

    /// <summary>
    /// Returns every reason this definition cannot be used. Empty means usable.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(Name))
        {
            errors.Add("'name' is required.");
        }

        Check(AuthorityHost, "authorityHost");
        Check(ResourceManagerEndpoint, "resourceManagerEndpoint");

        if (string.IsNullOrWhiteSpace(ResourceManagerAudience))
        {
            errors.Add("'resourceManagerAudience' is required.");
        }

        if (string.IsNullOrWhiteSpace(BlobSuffix))
        {
            errors.Add("'blobSuffix' is required (for example blob.core.usgovcloudapi.net).");
        }
        else if (BlobSuffix.Contains("://", StringComparison.Ordinal) || BlobSuffix.StartsWith('.'))
        {
            errors.Add("'blobSuffix' must be a bare DNS suffix, not a URL.");
        }

        return errors;

        void Check(Uri? value, string field)
        {
            if (value is null)
            {
                errors.Add($"'{field}' is required.");
            }
            else if (!value.IsAbsoluteUri)
            {
                errors.Add($"'{field}' must be an absolute URI.");
            }
            else if (value.Scheme != Uri.UriSchemeHttps)
            {
                errors.Add($"'{field}' must use https.");
            }
        }
    }
}

/// <summary>
/// Loads and holds the <see cref="Models.AzureCloud.Custom"/> definition.
/// </summary>
/// <remarks>
/// Process-wide because the cloud is a property of the machine the tool is running on, not of
/// an individual plan: an enclave jump box can only reach one Resource Manager.
/// </remarks>
public static class CustomCloud
{
    public const string FileName = "cloud.json";

    private static readonly Lock Gate = new();
    private static CloudDefinition? _definition;
    private static string? _loadedFrom;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static CloudDefinition? Definition
    {
        get { lock (Gate) { return _definition; } }
    }

    /// <summary>Path the active definition came from, for display in the UI.</summary>
    public static string? LoadedFrom
    {
        get { lock (Gate) { return _loadedFrom; } }
    }

    public static bool IsConfigured => Definition is not null;

    /// <summary>Sets the definition directly. Used by tests and by callers with their own config.</summary>
    public static void Set(CloudDefinition definition, string? source = null)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var errors = definition.Validate();
        if (errors.Count > 0)
        {
            throw new ArgumentException(string.Join(" ", errors), nameof(definition));
        }

        lock (Gate)
        {
            _definition = definition;
            _loadedFrom = source;
        }
    }

    public static void Clear()
    {
        lock (Gate)
        {
            _definition = null;
            _loadedFrom = null;
        }
    }

    /// <summary>
    /// Loads <c>cloud.json</c> from <paramref name="contentRoot"/> if it exists.
    /// </summary>
    /// <returns>
    /// True when a definition was loaded. False with a null <paramref name="error"/> simply means
    /// no file was present, which is the normal case in a connected commercial environment.
    /// </returns>
    public static bool TryLoad(string contentRoot, out string? error)
    {
        error = null;

        var path = Path.Combine(contentRoot, FileName);
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            var definition = JsonSerializer.Deserialize<CloudDefinition>(File.ReadAllText(path), Options);
            if (definition is null)
            {
                error = $"{path} is empty.";
                return false;
            }

            var errors = definition.Validate();
            if (errors.Count > 0)
            {
                error = string.Join(" ", errors);
                return false;
            }

            Set(definition, path);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Returns the definition, or throws with an actionable message if none is configured.</summary>
    public static CloudDefinition Require() =>
        Definition ?? throw new InvalidOperationException(
            $"The plan targets a custom cloud but no {FileName} was found. " +
            $"Place {FileName} beside the executable with the authority host, Resource Manager " +
            "endpoint and audience for your enclave.");
}
