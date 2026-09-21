using IaaSBuilder.Core.Pricing;
using IaaSBuilder.Web.Services;

namespace IaaSBuilder.Core.Tests;

/// <summary>
/// The cache-hit path through <see cref="PriceState"/>.
/// </summary>
/// <remarks>
/// <para>
/// This exists because of a bug a browser found by accident and no unit test could see. Prices are
/// read synchronously during a render (<c>Find</c> is called once per option), but the store read
/// is asynchronous, so the first render of a visit always misses. The fetch path raised
/// <c>Changed</c> when it landed and the page filled in; the cache path returned silently, so
/// every visit after the first sat on "Loading prices..." for ever with the prices already in
/// memory one render away.
/// </para>
/// <para>
/// The fix has to be exact in both directions, which is what makes it worth pinning. Never
/// raising is the original bug. Raising on every cache hit is a render loop, because a page
/// showing a region's ~950 sizes calls <c>Find</c> ~950 times per render and each one would
/// schedule another.
/// </para>
/// </remarks>
public class PriceStateTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "iaasb-pricestate-" + Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// A feed that fails the test if it is called at all. The cache paths below must not reach the
    /// network; asserting "no price appeared" would pass for a request that simply hadn't landed
    /// yet, so the absence of the call is the thing worth asserting.
    /// </summary>
    private sealed class NeverCalled : HttpMessageHandler
    {
        public int Calls;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            throw new InvalidOperationException(
                "The retail feed was called on a path that should have been served from cache.");
        }
    }

    private PriceState Build(out NeverCalled feed, out PriceFileStore store, TimeSpan? age = null)
    {
        Directory.CreateDirectory(_dir);
        store = new PriceFileStore(Path.Combine(_dir, "prices.json"));

        var catalog = new PriceCatalog();
        catalog.Regions["usgovvirginia"] = new RegionPrices
        {
            Region = "usgovvirginia",
            Currency = "USD",
            CapturedUtc = DateTimeOffset.UtcNow - (age ?? TimeSpan.FromDays(1)),
            Sizes =
            {
                ["Standard_D2s_v5"] = new VmPrice("Standard_D2s_v5", WindowsPerHour: 0.213m)
            }
        };

        store.SaveAsync(catalog).GetAwaiter().GetResult();

        feed = new NeverCalled();
        return new PriceState(store, new RetailPriceSource(new HttpClient(feed)));
    }

    [Fact]
    public async Task A_cached_region_raises_changed_so_the_page_re_renders()
    {
        var state = Build(out var feed, out _);

        var changed = 0;
        state.Changed += () => changed++;

        // What a render does: ask synchronously, get null because the store read has not finished.
        Assert.Null(state.Find("usgovvirginia", "Standard_D2s_v5"));

        await state.EnsureRegionAsync("usgovvirginia");

        Assert.Equal(1, changed);
        Assert.True(state.HasPrices("usgovvirginia"));
        Assert.Equal(0.213m, state.Find("usgovvirginia", "Standard_D2s_v5")!.WindowsPerHour);
        Assert.Equal(0, feed.Calls);
    }

    [Fact]
    public async Task The_nudge_fires_once_however_many_sizes_the_page_shows()
    {
        var state = Build(out var feed, out _);

        var changed = 0;
        state.Changed += () => changed++;

        // A page rendering a region's whole size list, twice over.
        for (var i = 0; i < 500; i++)
        {
            state.Find("usgovvirginia", "Standard_D2s_v5");
            await state.EnsureRegionAsync("usgovvirginia");
        }

        Assert.Equal(1, changed);
        Assert.Equal(0, feed.Calls);
    }

    [Fact]
    public async Task A_region_the_cache_does_not_cover_is_fetched()
    {
        var state = Build(out var feed, out _);

        // Deliberately allowed to reach the feed, which throws - the point is that it was tried.
        await state.EnsureRegionAsync("usgovarizona");

        // Fired and forgotten, so give the background task a moment to fail.
        for (var i = 0; i < 50 && feed.Calls == 0; i++) await Task.Delay(20);

        Assert.True(feed.Calls > 0, "A region with no cached prices should have been fetched.");
        Assert.False(state.HasPrices("usgovarizona"));
    }

    [Fact]
    public async Task A_stale_cached_region_is_refetched()
    {
        var state = Build(out var feed, out _, age: PriceState.StaleAfter + TimeSpan.FromDays(1));

        await state.EnsureRegionAsync("usgovvirginia");

        for (var i = 0; i < 50 && feed.Calls == 0; i++) await Task.Delay(20);

        Assert.True(feed.Calls > 0, "Prices older than StaleAfter should have been refetched.");
    }

    /// <summary>
    /// The egress toggle. An operator who turned prices off should not see the app reach out, and
    /// should not see it read a cache either - the answer is "no prices", not "old prices".
    /// </summary>
    [Fact]
    public async Task Disabled_reaches_neither_the_cache_nor_the_feed()
    {
        var state = Build(out var feed, out _);
        state.Enabled = false;

        var changed = 0;
        state.Changed += () => changed++;

        Assert.Null(state.Find("usgovvirginia", "Standard_D2s_v5"));
        await state.EnsureRegionAsync("usgovvirginia");

        Assert.Equal(0, changed);
        Assert.Equal(0, feed.Calls);
        Assert.False(state.HasPrices("usgovvirginia"));
    }

    [Fact]
    public void Turning_the_toggle_raises_changed_only_on_a_real_transition()
    {
        var state = Build(out _, out _);

        var changed = 0;
        state.Changed += () => changed++;

        state.Enabled = true;   // already true
        Assert.Equal(0, changed);

        state.Enabled = false;
        Assert.Equal(1, changed);

        state.Enabled = false;  // no change
        Assert.Equal(1, changed);
    }
}
