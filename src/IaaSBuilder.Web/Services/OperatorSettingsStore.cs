using Microsoft.JSInterop;

namespace IaaSBuilder.Web.Services;

/// <summary>
/// Persists <see cref="OperatorSettings"/> in the browser's localStorage.
/// </summary>
/// <remarks>
/// The browser is the right place for both shipping modes. Hosted, settings belong to the
/// visitor and must not be written to the server, which would put one visitor's subscription id
/// where another could read it. Offline, the exe serves a browser too, so the same code works
/// with no file system permissions and nothing left behind on a shared machine beyond the
/// operator's own profile.
///
/// Every call is best-effort. localStorage throws in private-browsing modes and when a site is
/// denied storage, and none of that is worth failing a render over - the cost of losing settings
/// is retyping four fields.
/// </remarks>
public sealed class OperatorSettingsStore(IJSRuntime js)
{
    private const string Key = "iaasbuilder.settings";

    private bool _loaded;
    private OperatorSettings? _cached;

    /// <summary>
    /// Only ever true after a successful load or save, so the UI can tell "nothing saved yet"
    /// from "saved, but every field was empty".
    /// </summary>
    public bool HasSaved { get; private set; }

    /// <summary>
    /// Reads storage at most once per circuit and caches the result.
    /// </summary>
    /// <remarks>
    /// Every component that shows saved-settings state calls this from its own first render
    /// rather than relying on the layout having got there first. That ordering is a genuine
    /// race - the layout's read is an async JS round trip, so a page can render before it
    /// returns - and it failed exactly that way in testing: the fields restored but the
    /// "Forget saved settings" button stayed hidden, because the page had already rendered
    /// against HasSaved == false and nothing re-rendered it afterwards.
    /// </remarks>
    public async Task<OperatorSettings?> EnsureLoadedAsync()
    {
        if (_loaded)
        {
            return _cached;
        }

        _cached = await LoadAsync();
        _loaded = true;
        return _cached;
    }

    public async Task<OperatorSettings?> LoadAsync()
    {
        try
        {
            var json = await js.InvokeAsync<string?>("localStorage.getItem", Key);
            var settings = OperatorSettings.FromJson(json);
            HasSaved = settings is not null;
            return settings;
        }
        catch (Exception ex) when (ex is JSException or InvalidOperationException or TaskCanceledException)
        {
            return null;
        }
    }

    public async Task<bool> SaveAsync(OperatorSettings settings)
    {
        try
        {
            await js.InvokeVoidAsync("localStorage.setItem", Key, settings.ToJson());
            HasSaved = true;
            _cached = settings;
            _loaded = true;
            return true;
        }
        catch (Exception ex) when (ex is JSException or InvalidOperationException or TaskCanceledException)
        {
            return false;
        }
    }

    public async Task<bool> ClearAsync()
    {
        try
        {
            await js.InvokeVoidAsync("localStorage.removeItem", Key);
            HasSaved = false;
            _cached = null;
            _loaded = true;
            return true;
        }
        catch (Exception ex) when (ex is JSException or InvalidOperationException or TaskCanceledException)
        {
            return false;
        }
    }
}
