namespace IaaSBuilder.Web.Services;

/// <summary>
/// Which of the two ways this ships is currently running.
/// </summary>
/// <remarks>
/// This is a security control, not a cosmetic one. The interactive browser sign-in opens a
/// browser <b>on the machine running the server</b>. On the offline executable that is the
/// operator's own desktop and it is the best flow available. On a hosted website it would try to
/// launch a browser on the web server - which either fails, or on a misconfigured box succeeds
/// and strands an interactive logon nobody can see, while the visitor waits for a redirect that
/// never comes. So the flow has to be offered on exactly one of them.
/// </remarks>
public sealed class HostingMode
{
    private HostingMode(bool isLocalOperator, string reason)
    {
        IsLocalOperator = isLocalOperator;
        Reason = reason;
    }

    /// <summary>
    /// True when the server is bound only to loopback, which means the only person who can reach
    /// it is sitting at the machine it is running on.
    /// </summary>
    public bool IsLocalOperator { get; }

    /// <summary>Why we concluded that, for the diagnostics panel.</summary>
    public string Reason { get; }

    /// <summary>
    /// Derived from the bound addresses rather than from a configuration switch, because a switch
    /// can be left at its development value when the app is published to a real site - and the
    /// failure mode of getting this wrong is silent.
    /// </summary>
    public static HostingMode FromUrls(IEnumerable<string> urls)
    {
        var listed = urls
            .SelectMany(u => u.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToList();

        if (listed.Count == 0)
        {
            // No explicit binding means the host's defaults, which are loopback.
            return new HostingMode(true, "No explicit binding; the host listens on loopback only.");
        }

        var remote = listed.Where(u => !IsLoopback(u)).ToList();

        return remote.Count == 0
            ? new HostingMode(true, $"Bound to loopback only ({string.Join(", ", listed)}).")
            : new HostingMode(false, $"Reachable from the network ({string.Join(", ", remote)}).");
    }

    private static bool IsLoopback(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            // A wildcard binding such as http://*:5099 or http://+:80 is not a valid Uri and is
            // never loopback. Treating an unparseable binding as remote is the safe direction.
            return false;
        }

        return uri.IsLoopback;
    }
}
