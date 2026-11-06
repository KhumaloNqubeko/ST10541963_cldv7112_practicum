using AgriStream.Data;
using AgriStream.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
namespace AgriStream.Controllers;
[Authorize(Roles = SystemRoles.Admin)]
public class AdminController(UserManager<IdentityUser> users) : Controller {
    [HttpGet] public IActionResult CreateUser() => View(new CreateUserViewModel());
    [HttpPost, ValidateAntiForgeryToken] public async Task<IActionResult> CreateUser(CreateUserViewModel model) {
        if (!SystemRoles.All.Contains(model.Role)) ModelState.AddModelError(nameof(model.Role), "Choose a valid role.");
        if (!ModelState.IsValid) return View(model);
        var user = new IdentityUser { UserName = model.Username, Email = model.Username };
        var result = await users.CreateAsync(user, model.Password);
        if (result.Succeeded) {
            result = await users.AddToRoleAsync(user, model.Role);
            if (result.Succeeded) { TempData["Success"] = "Account created."; return RedirectToAction(nameof(CreateUser)); }
            var cleanup = await users.DeleteAsync(user);
            if (!cleanup.Succeeded) throw new InvalidOperationException("Unable to roll back account creation.");
        }
        foreach (var error in result.Errors) ModelState.AddModelError("", error.Description);
        return View(model);
    }
}
