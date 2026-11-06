using System.Security.Claims;
using AgriStream.Data;
using AgriStream.Models;
using Microsoft.EntityFrameworkCore;

namespace AgriStream.Services;

public class FarmAccess(ApplicationDbContext db)
{
    public IQueryable<Farm> VisibleTo(ClaimsPrincipal user)
    {
        if (user.IsInRole(SystemRoles.Admin)) return db.Farms;
        var id = user.FindFirstValue(ClaimTypes.NameIdentifier);
        var farmer = user.IsInRole(SystemRoles.Farmer);
        var agronomist = user.IsInRole(SystemRoles.Agronomist);
        return db.Farms.Where(f => (farmer && f.FarmerId == id) ||
            (agronomist && f.Agronomists.Any(a => a.AgronomistId == id)));
    }

    public async Task<MonitoringDashboardViewModel> DashboardAsync(ClaimsPrincipal user, string audience, bool includeSqlReadings = true)
    {
        var farms = VisibleTo(user).AsNoTracking();
        var ids = farms.Select(f => f.Id);
        return new MonitoringDashboardViewModel {
            Audience = audience,
            FarmCount = await farms.CountAsync(),
            SensorCount = await db.Sensors.CountAsync(s => ids.Contains(s.FarmId) && s.IsActive),
            OpenAlertCount = await db.FarmAlerts.CountAsync(a => ids.Contains(a.FarmId) && !a.IsResolved),
            PendingImageCount = await db.DroneImages.CountAsync(i => ids.Contains(i.FarmId) && (i.Status == ImageProcessingStatus.Pending || i.Status == ImageProcessingStatus.Processing)),
            Farms = await farms.OrderBy(f => f.Name).Take(50).ToListAsync(),
            Readings = includeSqlReadings ? await db.SensorReadings.AsNoTracking().Include(r => r.Sensor).ThenInclude(s => s.Farm).Where(r => ids.Contains(r.Sensor.FarmId)).OrderByDescending(r => r.RecordedAt).Take(20).ToListAsync() : [],
            Images = await db.DroneImages.AsNoTracking().Include(i => i.Farm).Where(i => ids.Contains(i.FarmId)).OrderByDescending(i => i.CapturedAt).Take(10).ToListAsync(),
            Alerts = await db.FarmAlerts.AsNoTracking().Include(a => a.Farm).Where(a => ids.Contains(a.FarmId) && !a.IsResolved).OrderByDescending(a => a.CreatedAt).Take(10).ToListAsync(),
            Recommendations = await db.Recommendations.AsNoTracking().Include(r => r.Farm).Where(r => ids.Contains(r.FarmId)).OrderByDescending(r => r.CreatedAt).Take(10).ToListAsync()
        };
    }
}
