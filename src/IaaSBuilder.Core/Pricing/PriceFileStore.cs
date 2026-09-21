using System.Text.Json;
using System.Text.Json.Serialization;

namespace IaaSBuilder.Core.Pricing;

/// <summary>
/// Caches fetched prices beside the catalog snapshot.
/// </summary>
/// <remarks>
/// Prices change rarely - a region's rate card is stable for months - and fetching one costs seven
/// throttled round trips to an endpoint that may not be reachable at all. Caching turns that into
/// a one-off. It also gives a disconnected enclave a way to have prices: copy the file in
/// alongside the catalog snapshot, exactly as the catalog itself is shipped.
/// </remarks>
public sealed class PriceFileStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public PriceFileStore(string path) => Path = System.IO.Path.GetFullPath(path);

    public string Path { get; }

    public bool Exists => File.Exists(Path);

    public async Task<PriceCatalog?> TryLoadAsync(CancellationToken ct = default)
    {
        if (!Exists) return null;

        try
        {
            await using var stream = File.OpenRead(Path);
            return await JsonSerializer.DeserializeAsync<PriceCatalog>(stream, Options, ct);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // A corrupt cache means no prices, never a failure to start.
            return null;
        }
    }

    public async Task SaveAsync(PriceCatalog catalog, CancellationToken ct = default)
    {
        try
        {
            var directory = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // Write-then-rename, so an interrupted save cannot leave a truncated file behind.
            var temporary = Path + ".tmp";
            await using (var stream = File.Create(temporary))
            {
                await JsonSerializer.SerializeAsync(stream, catalog, Options, ct);
            }

            File.Move(temporary, Path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A read-only install directory is a normal way to run this. Losing the cache costs a
            // re-fetch next time; failing here would cost the operator their prices entirely.
        }
    }
}
