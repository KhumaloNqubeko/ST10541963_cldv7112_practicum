using System.Security.Claims;
using System.Text.Json;
using AgriStream.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgriStream.Controllers;

[Authorize(Roles="Farmer")]
public sealed class IrrigationController(IrrigationDashboard dashboard,IrrigationCommandClient client,ILogger<IrrigationController> logger):Controller
{
    [HttpPost,ValidateAntiForgeryToken]
    public async Task<IActionResult> Trigger(string submission,CancellationToken ct)
    {
        if(!dashboard.Enabled)return NotFound();
        var form=dashboard.Read(submission);
        var userId=User.FindFirstValue(ClaimTypes.NameIdentifier);
        if(form is null || form.UserId!=userId)return BadRequest();
        if(!await dashboard.OwnsAsync(User,form.Command,ct))return NotFound();
        try {
            var receipt=await client.SubmitAsync(form.Command,userId!,ct);
            return View("Result",new IrrigationResult(receipt.CommandId,receipt.Status=="Queued"?"Irrigation command queued. Refresh the dashboard to see processing status.":"This command has already been processed.",null));
        }
        catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
        catch(Exception ex)when(ex is HttpRequestException or TaskCanceledException or InvalidOperationException or InvalidDataException or JsonException) {
            logger.LogWarning("Irrigation submission {CommandId} unavailable. ErrorType={ErrorType}, HttpStatus={HttpStatus}, TimedOut={TimedOut}",
                form.Command.CommandId,ex.GetType().Name,ex is HttpRequestException httpError && httpError.StatusCode.HasValue?(int)httpError.StatusCode.Value:(int?)null,ex is TaskCanceledException);
            return View("Result",new IrrigationResult(form.Command.CommandId,"The submission could not be confirmed. Retry this same command to avoid creating another request.",submission));
        }
    }
}
