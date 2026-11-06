using AgriStream.Data;
using AgriStream.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgriStream.Controllers;

[Authorize(Roles = "Farmer,Agronomist,Admin")]
public class DashboardController(FarmAccess access, FarmerTelemetryDashboard telemetry,IrrigationDashboard irrigation) : Controller
{
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var audience = User.IsInRole(SystemRoles.Admin) ? "Admin" : User.IsInRole(SystemRoles.Agronomist) ? "Agronomist" : "Farmer";
        var model = await access.DashboardAsync(User, audience, includeSqlReadings: audience != "Farmer");
        if (audience == "Farmer") await telemetry.PopulateAsync(model, User, null, cancellationToken);
        await irrigation.PopulateAsync(model,User,cancellationToken);
        if(User.IsInRole("Admin"))model.IrrigationEnabled=irrigation.Enabled;
        return View(model);
    }
    [Authorize(Roles = SystemRoles.Farmer)]
    public async Task<IActionResult> Farmer(CancellationToken cancellationToken)
    {
        var model = await access.DashboardAsync(User, "Farmer", includeSqlReadings: false);
        await telemetry.PopulateAsync(model, User, null, cancellationToken);
        await irrigation.PopulateAsync(model,User,cancellationToken);
        return View("Index", model);
    }

    [Authorize(Roles = SystemRoles.Farmer)]
    public async Task<IActionResult> Field(string fieldId, CancellationToken cancellationToken)
    {
        if (!AgriStream.Telemetry.FieldIdentifier.IsValid(fieldId)) return BadRequest();
        var model = await access.DashboardAsync(User, "Farmer", includeSqlReadings: false);
        // Reject an unowned/unmapped field before any Function request is made.
        if (!await telemetry.PopulateAsync(model, User, fieldId, cancellationToken)) return NotFound();
        await irrigation.PopulateAsync(model,User,cancellationToken);
        return View("Index", model);
    }
    [Authorize(Roles = SystemRoles.Agronomist)]
    public async Task<IActionResult> Agronomist() => View("Index", await access.DashboardAsync(User, "Agronomist"));
    [Authorize(Roles = SystemRoles.Admin)]
    public async Task<IActionResult> Admin() => View("Index", await access.DashboardAsync(User, "Admin"));
}
