namespace IaaSBuilder.Core.Models;

/// <summary>
/// Credentials required for a deployment, held separately from <see cref="DeploymentPlan"/>
/// so they can never be accidentally serialized into a saved plan file.
/// </summary>
/// <remarks>
/// The legacy script stored the form state with <c>Export-Csv</c> and passed the admin
/// password through the same splatted hashtable as every other parameter, which made it
/// easy to leak into transcripts. Keeping secrets in a separate, non-serializable,
/// disposable type makes the boundary explicit.
/// </remarks>
public sealed class DeploymentSecrets : IDisposable
{
    private char[]? _adminPassword;

    public DeploymentSecrets(string adminPassword)
    {
        _adminPassword = adminPassword.ToCharArray();
    }

    public string AdminPassword =>
        _adminPassword is null
            ? throw new ObjectDisposedException(nameof(DeploymentSecrets))
            : new string(_adminPassword);

    public bool HasAdminPassword => _adminPassword is { Length: > 0 };

    public void Dispose()
    {
        if (_adminPassword is not null)
        {
            Array.Clear(_adminPassword);
            _adminPassword = null;
        }
    }
}
