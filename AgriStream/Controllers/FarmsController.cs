using AgriStream.Data;
using AgriStream.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace AgriStream.Controllers;

[Authorize(Roles = SystemRoles.Admin)]
public class FarmsController(ApplicationDbContext db, UserManager<IdentityUser> users) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Create() => View(await Populate(new CreateFarmViewModel()));

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(CreateFarmViewModel model)
    {
        var farmer = await users.FindByIdAsync(model.FarmerId ?? "");
        if (farmer is null || !await users.IsInRoleAsync(farmer, SystemRoles.Farmer))
            ModelState.AddModelError(nameof(model.FarmerId), "Select a Farmer account.");
        if (!string.IsNullOrEmpty(model.AgronomistId)) {
            var agronomist = await users.FindByIdAsync(model.AgronomistId);
            if (agronomist is null || !await users.IsInRoleAsync(agronomist, SystemRoles.Agronomist))
                ModelState.AddModelError(nameof(model.AgronomistId), "Select an Agronomist account.");
        }
        if (!ModelState.IsValid) return View(await Populate(model));
        var farm = new Farm { Name = model.Name, Location = model.Location, FarmerId = farmer!.Id };
        if (!string.IsNullOrEmpty(model.AgronomistId)) farm.Agronomists.Add(new FarmAgronomist { AgronomistId = model.AgronomistId });
        db.Farms.Add(farm);
        await db.SaveChangesAsync();
        TempData["Success"] = "Farm created and access assigned.";
        return RedirectToAction("Index", "Dashboard");
    }

    private async Task<CreateFarmViewModel> Populate(CreateFarmViewModel model)
    {
        model.Farmers = (await users.GetUsersInRoleAsync(SystemRoles.Farmer)).OrderBy(u => u.UserName).Select(u => new SelectListItem(u.UserName, u.Id)).ToList();
        model.Agronomists = (await users.GetUsersInRoleAsync(SystemRoles.Agronomist)).OrderBy(u => u.UserName).Select(u => new SelectListItem(u.UserName, u.Id)).ToList();
        return model;
    }
}
