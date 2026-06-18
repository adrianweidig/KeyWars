using KeyWars.Data;
using KeyWars.Domain;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace KeyWars.Pages;

public sealed class RanglistenModel(KeyWarsDbContext db) : PageModel
{
    public IReadOnlyList<UserProfile> Rows { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Rows = await db.UserProfiles
            .Where(item => item.LeaderboardVisible && !item.Deleted)
            .OrderByDescending(item => item.ArenaRating)
            .ThenByDescending(item => item.SeasonPoints)
            .ThenBy(item => item.DisplayName)
            .Take(100)
            .ToListAsync(cancellationToken);
    }
}
