using AgriStream.Data;
using AgriStream.Irrigation;
using AgriStream.Models;
using AgriStream.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
namespace AgriStream.Controllers;
[Authorize(Roles="Admin")]
public class IrrigationDevicesController(ApplicationDbContext db,FarmerTelemetryDashboard fields,IrrigationDashboard irrigation):Controller
{
    [HttpGet]public async Task<IActionResult> Create(CancellationToken ct)=>!irrigation.Enabled?NotFound():View(await Populate(new(),ct));
    [HttpPost,ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(CreateIrrigationDevice model,CancellationToken ct)
    {
        if(!irrigation.Enabled)return NotFound();
        if(!fields.FieldMappings().TryGetValue(model.FieldId??"",out var id) || id!=model.FarmId || !await db.Farms.AnyAsync(f=>f.Id==model.FarmId,ct))ModelState.AddModelError("FieldId","Field must map to the selected existing farm.");
        if(await db.IrrigationDevices.AnyAsync(d=>d.DeviceId==model.DeviceId,ct))ModelState.AddModelError("DeviceId","Device ID already exists.");
        if(!ModelState.IsValid)return View(await Populate(model,ct));
        db.IrrigationDevices.Add(new IrrigationDevice{DeviceId=model.DeviceId,Name=model.Name,FarmId=model.FarmId,FieldId=model.FieldId!});
        try{await db.SaveChangesAsync(ct);}catch(DbUpdateException){ModelState.AddModelError("","Device could not be registered. Check the device ID and farm.");return View(await Populate(model,ct));}
        TempData["Success"]="Irrigation device registered. Initial state is Idle; no physical device was activated.";
        return RedirectToAction("Index","Dashboard");
    }
    private async Task<CreateIrrigationDevice> Populate(CreateIrrigationDevice model,CancellationToken ct){model.Farms=await db.Farms.OrderBy(f=>f.Name).Select(f=>new SelectListItem(f.Name,f.Id.ToString())).ToListAsync(ct);return model;}
}
