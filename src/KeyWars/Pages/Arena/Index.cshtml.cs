using KeyWars.Auth;
using KeyWars.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace KeyWars.Pages.Arena;

public sealed class IndexModel(ILiveRoomDispatcher rooms, CurrentUser currentUser) : PageModel
{
    private const int PageSize = 20;

    [BindProperty(SupportsGet = true)]
    public int Seite { get; set; } = 1;

    public IReadOnlyList<LiveRoomLobbySummary> Rooms { get; private set; } = [];
    public int TotalCount { get; private set; }
    public int TotalPages { get; private set; } = 1;
    public bool HasPreviousPage => Seite > 1;
    public bool HasNextPage => Seite < TotalPages;

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        var profile = await currentUser.RequireProfileAsync(User, cancellationToken);
        Seite = Math.Clamp(Seite, 1, int.MaxValue / PageSize);
        var page = await rooms.ListLobbySummariesAsync(
            profile.Id,
            (Seite - 1) * PageSize,
            PageSize,
            cancellationToken);
        TotalCount = page.Total;
        TotalPages = Math.Max(1, (TotalCount + PageSize - 1) / PageSize);
        if (Seite > TotalPages)
        {
            Seite = TotalPages;
            page = await rooms.ListLobbySummariesAsync(
                profile.Id,
                (Seite - 1) * PageSize,
                PageSize,
                cancellationToken);
        }

        Rooms = page.Items;
    }
}
