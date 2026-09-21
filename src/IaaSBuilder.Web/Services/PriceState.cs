using IaaSBuilder.Core.Pricing;

namespace IaaSBuilder.Web.Services;

/// <summary>
/// Supplies VM prices to the UI, from cache first and the retail feed second.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately best-effort. Prices are decoration: they help an operator pick a size, and that is
/// all. Nothing here may block a render, fail a validation or delay a deployment, so every path
/// through it ends in "no price yet" rather than an error.
/// </para>
/// <para>
/// The fetch is fired and forgotten when a region is first asked about, and <see cref="Changed"/>
/// fires when it lands so the page fills in. Asking again for the same region while one is in
/// flight does nothing - without that guard, every re-render of a page with eight servers on it
/// would start eight downloads of the same 6,300-row list.
/// </para>
/// </remarks>
public sealed class PriceState
{
    /// <summary>
    /// Old enough to be worth refetching. Azure rate cards move at most monthly, and a stale
    /// price still tells an operator whether a size costs $20 or $2,000 a month.
    /// </summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromDays(30);

    private readonly PriceFileStore _store;
    private readonly RetailPriceSource _source;
    private readonly HashSet<string> _inFlight = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gate = new(1, 1);

    private PriceCatalog _catalog = new();
    private bool _loaded;

    public PriceState(PriceFileStore store, RetailPriceSource source)
    {
        _store = store;
        _source = source;
    }

    public event Action? Changed;

    /// <summary>Set when the feed was tried for the current region and could not be reached.</summary>
    public bool Unavailable { get; private set; }

    public bool IsLoading => _inFlight.Count > 0;

    /// <summary>
    /// Whether prices may be fetched at all. The retail feed is a different host from ARM, so an
    /// enclave that permits the management plane may still not permit this - and an operator who
    /// turned it off should not see the app reaching out anyway.
    /// </summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value) return;
            _enabled = value;
            Changed?.Invoke();
        }
    }

    private bool _enabled = true;

    /// <summary>
    /// The price for a size, or <see langword="null"/> if it is not known yet. Starts the fetch for
    /// the region the first time it is asked, and returns null until that completes.
    /// </summary>
    public VmPrice? Find(string? region, string? sizeName)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(region))
        {
            return null;
        }

        _ = EnsureRegionAsync(region);

        return _catalog.For(region)?.Find(sizeName);
    }

    public string Currency(string? region) => _catalog.For(region)?.Currency ?? "USD";

    /// <summary>True once this region's prices are in memory, so the UI can stop saying "loading".</summary>
    public bool HasPrices(string? region) => _catalog.For(region) is { Sizes.Count: > 0 };

    public DateTimeOffset? CapturedUtc(string? region) => _catalog.For(region)?.CapturedUtc;

    public async Task EnsureRegionAsync(string region, CancellationToken ct = default)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(region))
        {
            return;
        }

        var next = await DecideAsync(region, ct);

        switch (next)
        {
            case Next.Nudge:
                // The caller asked synchronously during a render and got null, because the store
                // read is async and had not finished. Without a nudge the page sits on "Loading
                // prices..." for ever on every visit after the first: the fetch path raises
                // Changed, the cache path did not, and the prices are one render away.
                Changed?.Invoke();
                break;

            case Next.Fetch:
                // Outside the lock: seven throttled round trips, and it can take a minute.
                _ = FetchAsync(region);
                break;
        }
    }

    private enum Next
    {
        Nothing,

        /// <summary>Prices are in hand but the UI asked before they were; re-render it.</summary>
        Nudge,

        Fetch,
    }

    private async Task<Next> DecideAsync(string region, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            // Only the transition matters. Raising Changed on every cache hit would be a render
            // loop, because a page with several hundred sizes calls Find once per option.
            var justLoaded = !_loaded;

            if (!_loaded)
            {
                _catalog = await _store.TryLoadAsync(ct) ?? new PriceCatalog();
                _loaded = true;
            }

            var cached = _catalog.For(region);

            if (cached is not null && cached.Age < StaleAfter)
            {
                return justLoaded ? Next.Nudge : Next.Nothing;
            }

            return _inFlight.Add(region) ? Next.Fetch : Next.Nothing;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task FetchAsync(string region)
    {
        try
        {
            var prices = await _source.TryGetAsync(region);

            await _gate.WaitAsync();
            try
            {
                if (prices is not null)
                {
                    _catalog.Regions[region] = prices;
                    Unavailable = false;
                }
                else
                {
                    // A stale entry is better than none, so it is left in place.
                    Unavailable = _catalog.For(region) is null;
                }

                _inFlight.Remove(region);
            }
            finally
            {
                _gate.Release();
            }

            if (prices is not null)
            {
                await _store.SaveAsync(_catalog);
            }
        }
        catch
        {
            // Prices are decoration. A background task that throws here would take down the
            // circuit for a feature nobody is blocked on.
            await _gate.WaitAsync();
            try
            {
                _inFlight.Remove(region);
                Unavailable = true;
            }
            finally
            {
                _gate.Release();
            }
        }

        Changed?.Invoke();
    }
}
