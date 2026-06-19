using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace KeyWars.Pages.Arena;

public sealed class RennenModel : PageModel
{
    public IActionResult OnGet(Guid id)
    {
        return RedirectToPage("/Arena/Raum", new { id });
    }
}
