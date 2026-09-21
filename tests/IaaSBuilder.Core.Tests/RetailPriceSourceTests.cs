using System.Net;
using System.Text;
using IaaSBuilder.Core.Pricing;

namespace IaaSBuilder.Core.Tests;

/// <summary>
/// The retail price feed against a canned payload, so the parsing rules are pinned without
/// depending on prices.azure.com being reachable from the build machine.
/// </summary>
/// <remarks>
/// The live feed was exercised separately while this was written - 22 checks, including every
/// failure path - but a test that needs the internet is not a test anyone can rely on.
/// </remarks>
public class RetailPriceSourceTests
{
    /// <summary>
    /// Real rows for one size, trimmed to the fields the parser reads. A single size has several
    /// rows distinguished only by prose, which is the whole difficulty here.
    /// </summary>
    private const string OnePage = """
    {
      "Items": [
        { "armSkuName": "Standard_D2s_v5", "retailPrice": 0.096, "currencyCode": "USD",
          "productName": "Virtual Machines Dsv5 Series", "meterName": "D2s v5" },
        { "armSkuName": "Standard_D2s_v5", "retailPrice": 0.213, "currencyCode": "USD",
          "productName": "Virtual Machines Dsv5 Series Windows", "meterName": "D2s v5" },
        { "armSkuName": "Standard_D2s_v5", "retailPrice": 0.0384, "currencyCode": "USD",
          "productName": "Virtual Machines Dsv5 Series Windows", "meterName": "D2s v5 Spot" },
        { "armSkuName": "Standard_D2s_v5", "retailPrice": 0.09, "currencyCode": "USD",
          "productName": "Virtual Machines Dsv5 Series Windows", "meterName": "D2s v5 Low Priority" }
      ],
      "NextPageLink": null
    }
    """;

    [Fact]
    public async Task Windows_and_linux_rates_are_kept_apart()
    {
        var prices = await FetchAsync(OnePage);

        var price = Assert.IsType<VmPrice>(prices!.Find("Standard_D2s_v5"));

        Assert.Equal(0.213m, price.WindowsPerHour);
        Assert.Equal(0.096m, price.LinuxPerHour);
        Assert.Equal(0.0384m, price.WindowsSpotPerHour);
    }

    /// <summary>
    /// Low Priority is the retired Batch-only tier. Its rate sits between Spot and pay-as-you-go,
    /// so folding it in would quietly understate what a lab costs rather than failing visibly.
    /// </summary>
    [Fact]
    public async Task The_low_priority_tier_is_ignored()
    {
        var price = (await FetchAsync(OnePage))!.Find("Standard_D2s_v5")!;

        Assert.NotEqual(0.09m, price.WindowsPerHour);
        Assert.NotEqual(0.09m, price.WindowsSpotPerHour);
    }

    [Fact]
    public async Task Windows_is_preferred_over_linux_because_that_is_what_this_tool_builds()
    {
        var price = (await FetchAsync(OnePage))!.Find("Standard_D2s_v5")!;

        Assert.Equal(0.213m, price.PerHour(spot: false));
        Assert.Equal(0.0384m, price.PerHour(spot: true));
    }

    [Fact]
    public async Task A_monthly_figure_is_the_hourly_rate_over_a_average_month()
    {
        var price = (await FetchAsync(OnePage))!.Find("Standard_D2s_v5")!;

        Assert.Equal(0.213m * VmPrice.HoursPerMonth, price.PerMonth(spot: false));
    }

    [Fact]
    public async Task Sizes_are_matched_regardless_of_case()
    {
        var prices = await FetchAsync(OnePage);

        Assert.NotNull(prices!.Find("standard_d2S_V5"));
    }

    [Fact]
    public async Task An_unknown_size_is_null_rather_than_an_error()
    {
        Assert.Null((await FetchAsync(OnePage))!.Find("Standard_Nonexistent"));
        Assert.Null((await FetchAsync(OnePage))!.Find(null));
    }

    /// <summary>
    /// Prices are decoration - nothing validates or blocks on them - so every failure has to end
    /// in "no price", never in an exception that would take down the circuit.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task A_failed_request_returns_no_prices(HttpStatusCode status) =>
        Assert.Null(await FetchAsync("", status));

    [Fact]
    public async Task Unreadable_json_returns_no_prices() =>
        Assert.Null(await FetchAsync("not json at all"));

    [Fact]
    public async Task A_blocked_endpoint_returns_no_prices()
    {
        var source = new RetailPriceSource(
            new HttpClient(new ThrowingHandler()),
            "https://prices.example.invalid/api/retail/prices");

        Assert.Null(await source.TryGetAsync("usgovvirginia"));
    }

    [Fact]
    public async Task An_empty_region_is_not_requested()
    {
        var handler = new StubHandler(OnePage, HttpStatusCode.OK);
        var source = new RetailPriceSource(new HttpClient(handler), "https://example.test/prices");

        Assert.Null(await source.TryGetAsync(""));
        Assert.Equal(0, handler.Calls);
    }

    /// <summary>
    /// A page failure part way through would otherwise leave some sizes priced and others blank
    /// for no reason the operator can see, which is worse than showing no prices at all.
    /// </summary>
    [Fact]
    public async Task A_partial_result_is_discarded_rather_than_shown()
    {
        var first = """
        {
          "Items": [
            { "armSkuName": "Standard_D2s_v5", "retailPrice": 0.213, "currencyCode": "USD",
              "productName": "Virtual Machines Dsv5 Series Windows", "meterName": "D2s v5" }
          ],
          "NextPageLink": "https://example.test/prices?page=2"
        }
        """;

        var handler = new SequenceHandler([
            (HttpStatusCode.OK, first),
            (HttpStatusCode.InternalServerError, ""),
        ]);

        var source = new RetailPriceSource(new HttpClient(handler), "https://example.test/prices");

        Assert.Null(await source.TryGetAsync("usgovvirginia"));
    }

    [Fact]
    public async Task The_region_is_filtered_on_so_a_sovereign_cloud_gets_its_own_rates()
    {
        var handler = new StubHandler(OnePage, HttpStatusCode.OK);
        var source = new RetailPriceSource(new HttpClient(handler), "https://example.test/prices");

        await source.TryGetAsync("usgovvirginia");

        var requested = Uri.UnescapeDataString(handler.LastUrl ?? "");

        Assert.Contains("armRegionName eq 'usgovvirginia'", requested);
        Assert.Contains("serviceName eq 'Virtual Machines'", requested);
        Assert.Contains("priceType eq 'Consumption'", requested);
    }

    /// <summary>
    /// The preview api-version returns 1000 rows a page instead of 100 - seven round trips for a
    /// region instead of sixty-three, against a service that throttles hard.
    /// </summary>
    [Fact]
    public async Task The_large_page_api_version_is_requested()
    {
        var handler = new StubHandler(OnePage, HttpStatusCode.OK);
        var source = new RetailPriceSource(new HttpClient(handler), "https://example.test/prices");

        await source.TryGetAsync("usgovvirginia");

        Assert.Contains("api-version=2023-01-01-preview", handler.LastUrl);
    }

    private static async Task<RegionPrices?> FetchAsync(
        string body,
        HttpStatusCode status = HttpStatusCode.OK)
    {
        var source = new RetailPriceSource(
            new HttpClient(new StubHandler(body, status)),
            "https://example.test/prices");

        return await source.TryGetAsync("usgovvirginia");
    }

    private sealed class StubHandler(string body, HttpStatusCode status) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string? LastUrl { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            LastUrl = request.RequestUri?.ToString();

            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class SequenceHandler(List<(HttpStatusCode Status, string Body)> pages)
        : HttpMessageHandler
    {
        private int _next;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            var (status, body) = pages[Math.Min(_next++, pages.Count - 1)];

            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException("No route to host.");
    }
}
