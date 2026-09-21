using System.Text.Json;
using System.Text.Json.Serialization;

namespace IaaSBuilder.Core.Pricing;

/// <summary>
/// Pay-as-you-go prices for one VM size in one region, in the currency the feed returned.
/// </summary>
/// <param name="WindowsPerHour">
/// The Windows rate, which includes the Windows Server licence. That is the one this tool shows,
/// because every image it deploys is Windows. The Linux rate is kept because the difference is
/// the licence cost and is worth being able to explain.
/// </param>
public sealed record VmPrice(
    string SizeName,
    decimal? LinuxPerHour = null,
    decimal? WindowsPerHour = null,
    decimal? WindowsSpotPerHour = null,
    decimal? LinuxSpotPerHour = null)
{
    /// <summary>
    /// Hours used to turn an hourly rate into "per month". 730 is the Azure pricing calculator's
    /// own figure - 365 * 24 / 12 - so the numbers here line up with what an operator sees there.
    /// </summary>
    public const decimal HoursPerMonth = 730m;

    public decimal? PerHour(bool spot) =>
        spot ? WindowsSpotPerHour ?? LinuxSpotPerHour : WindowsPerHour ?? LinuxPerHour;

    public decimal? PerMonth(bool spot) => PerHour(spot) * HoursPerMonth;
}

/// <summary>
/// Prices for every VM size in one region.
/// </summary>
public sealed class RegionPrices
{
    public string Region { get; set; } = "";
    public string Currency { get; set; } = "USD";
    public DateTimeOffset CapturedUtc { get; set; } = DateTimeOffset.UtcNow;
    public Dictionary<string, VmPrice> Sizes { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public TimeSpan Age => DateTimeOffset.UtcNow - CapturedUtc;

    public VmPrice? Find(string? sizeName) =>
        sizeName is not null && Sizes.TryGetValue(sizeName, out var price) ? price : null;
}

/// <summary>
/// Everything the app has learned about prices, keyed by region.
/// </summary>
public sealed class PriceCatalog
{
    public Dictionary<string, RegionPrices> Regions { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public RegionPrices? For(string? region) =>
        region is not null && Regions.TryGetValue(region, out var prices) ? prices : null;
}

/// <summary>
/// Reads the Azure retail price list.
/// </summary>
/// <remarks>
/// <para>
/// <c>prices.azure.com</c> is anonymous and unauthenticated - it is the same feed behind the public
/// pricing calculator - and it serves the sovereign clouds too: filtering on
/// <c>armRegionName eq 'usgovvirginia'</c> returns Azure Government rates. That matters, because
/// the alternative (the Consumption APIs) needs a billing-scope role most lab operators do not
/// have on the subscription they are building in.
/// </para>
/// <para>
/// It is, however, <b>a different endpoint from ARM</b>. A restricted enclave may allow the
/// management plane and not this, so every failure here is swallowed: prices are a nicety and must
/// never block, slow or fail a deployment. <see cref="OnDisk"/> caching exists for the same reason.
/// </para>
/// <para>
/// One region is roughly 6,300 rows across 7 pages, and the service throttles aggressively - a
/// burst of requests reliably draws HTTP 429. So it is fetched once per region, whole, with
/// backoff, and then cached.
/// </para>
/// </remarks>
public sealed class RetailPriceSource
{
    /// <summary>The public retail price feed. Not a sovereign-cloud-specific host.</summary>
    public const string DefaultEndpoint = "https://prices.azure.com/api/retail/prices";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _http;
    private readonly string _endpoint;

    public RetailPriceSource(HttpClient http, string endpoint = DefaultEndpoint)
    {
        _http = http;
        _endpoint = endpoint;
    }

    /// <summary>
    /// Fetches every pay-as-you-go VM rate for a region, or <see langword="null"/> if the feed
    /// cannot be reached. Never throws for a network reason.
    /// </summary>
    public async Task<RegionPrices?> TryGetAsync(string region, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(region))
        {
            return null;
        }

        var filter = $"serviceName eq 'Virtual Machines' and armRegionName eq '{region}' " +
                     "and priceType eq 'Consumption'";

        // The 2023-01-01-preview page size is 1000 rather than 100, which turns ~63 round trips
        // into 7. Against a service this eager to throttle, that is the difference between
        // working and not.
        var url = $"{_endpoint}?api-version=2023-01-01-preview&$filter={Uri.EscapeDataString(filter)}";

        var result = new RegionPrices { Region = region };
        var pages = 0;

        try
        {
            while (url is not null && pages < 40)
            {
                var page = await GetPageAsync(url, ct);
                if (page is null)
                {
                    // A failure part way through leaves a partial list, which would show some
                    // sizes priced and others blank for no visible reason. Better to have none.
                    return null;
                }

                foreach (var item in page.Items ?? [])
                {
                    Accumulate(result, item);
                }

                if (page.Items is { Length: > 0 })
                {
                    result.Currency = page.Items[0].CurrencyCode ?? result.Currency;
                }

                url = page.NextPageLink;
                pages++;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }

        return result.Sizes.Count > 0 ? result : null;
    }

    private async Task<PricePage?> GetPageAsync(string url, CancellationToken ct)
    {
        // Three tries with a widening gap. The throttle clears in seconds, and an operator who
        // opened the servers page is not waiting on this - it fills in when it arrives.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            using var response = await _http.GetAsync(url, ct);

            if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                await Task.Delay(TimeSpan.FromSeconds(2 + (attempt * 4)), ct);
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            return await JsonSerializer.DeserializeAsync<PricePage>(stream, Json, ct);
        }

        return null;
    }

    /// <summary>
    /// Folds one price row into the size's entry.
    /// </summary>
    /// <remarks>
    /// A single size has several rows, distinguished only by prose in <c>productName</c> and
    /// <c>meterName</c>: Linux and Windows, each in pay-as-you-go, Spot and Low Priority forms.
    /// "Low Priority" is the old Batch-only tier and is deliberately ignored - showing it as a VM
    /// price would understate what a lab actually costs.
    /// </remarks>
    private static void Accumulate(RegionPrices prices, PriceItem item)
    {
        if (string.IsNullOrWhiteSpace(item.ArmSkuName) || item.RetailPrice is not { } rate)
        {
            return;
        }

        var meter = item.MeterName ?? "";

        if (meter.Contains("Low Priority", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var isWindows = (item.ProductName ?? "").Contains("Windows", StringComparison.OrdinalIgnoreCase);
        var isSpot = meter.Contains("Spot", StringComparison.OrdinalIgnoreCase);

        var existing = prices.Sizes.TryGetValue(item.ArmSkuName, out var found)
            ? found
            : new VmPrice(item.ArmSkuName);

        prices.Sizes[item.ArmSkuName] = (isWindows, isSpot) switch
        {
            (true, false) => existing with { WindowsPerHour = rate },
            (true, true) => existing with { WindowsSpotPerHour = rate },
            (false, false) => existing with { LinuxPerHour = rate },
            (false, true) => existing with { LinuxSpotPerHour = rate }
        };
    }

    private sealed class PricePage
    {
        public PriceItem[]? Items { get; set; }

        [JsonPropertyName("NextPageLink")]
        public string? NextPageLink { get; set; }
    }

    private sealed class PriceItem
    {
        public string? ArmSkuName { get; set; }
        public string? ProductName { get; set; }
        public string? MeterName { get; set; }
        public string? CurrencyCode { get; set; }
        public decimal? RetailPrice { get; set; }
    }
}
