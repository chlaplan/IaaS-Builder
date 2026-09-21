namespace IaaSBuilder.Web.Services;

/// <summary>
/// Whether the setup checklist is expanded, remembered for the session rather than per page.
/// </summary>
/// <remarks>
/// <para>
/// The checklist starts collapsed to a single line. The per-field highlighting and the deploy gate
/// already tell an operator what is outstanding and where, so a permanently expanded panel on four
/// pages was repeating what the pages themselves said and pushing the actual fields down.
/// </para>
/// <para>
/// This lives in a scoped service because the component is re-created on every navigation. Holding
/// the flag in the component meant expanding it on one page and finding it collapsed again on the
/// next, which reads as the toggle being broken.
/// </para>
/// </remarks>
public sealed class ChecklistState
{
    private bool _expanded;

    public event Action? Changed;

    public bool Expanded
    {
        get => _expanded;
        set
        {
            if (_expanded == value)
            {
                return;
            }

            _expanded = value;
            Changed?.Invoke();
        }
    }
}
