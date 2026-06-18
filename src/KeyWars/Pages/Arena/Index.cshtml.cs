using KeyWars.Services;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace KeyWars.Pages.Arena;

public sealed class IndexModel(LiveRoomManager rooms) : PageModel
{
    public IReadOnlyList<LiveRoomSnapshot> Rooms { get; private set; } = [];

    public void OnGet()
    {
        Rooms = rooms.ListOpenRooms();
    }
}
