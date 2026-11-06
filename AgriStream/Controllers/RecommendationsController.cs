using System.Security.Claims;
using AgriStream.Data;
using AgriStream.Models;
using AgriStream.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

namespace AgriStream.Controllers;

[Authorize(Roles = SystemRoles.Agronomist)]
public class RecommendationsController(ApplicationDbContext db, FarmAccess access) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Create() => View(await Populate(new RecommendationViewModel()));

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(RecommendationViewModel model)
    {
        // Check assignment independently of the submitted dropdown value.
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        if (!await db.FarmAgronomists.AnyAsync(a => a.FarmId == model.FarmId && a.AgronomistId == userId)) return Forbid();
        if (!ModelState.IsValid) return View(await Populate(model));
        db.Recommendations.Add(new Recommendation { FarmId = model.FarmId, AgronomistId = userId, Advice = model.Advice });
        await db.SaveChangesAsync();
        TempData["Success"] = "Recommendation shared with the farm.";
        return RedirectToAction("Index", "Dashboard");
    }

    private async Task<RecommendationViewModel> Populate(RecommendationViewModel model)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        model.Farms = await access.VisibleTo(User).Where(f => f.Agronomists.Any(a => a.AgronomistId == userId))
            .OrderBy(f => f.Name).Select(f => new SelectListItem(f.Name, f.Id.ToString())).ToListAsync();
        return model;
    }
}
