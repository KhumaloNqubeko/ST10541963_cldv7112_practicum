using System.Data;
using Microsoft.EntityFrameworkCore;

namespace AgriStream.Irrigation;

public sealed class InvalidIrrigationCommandException(string reason):Exception(reason);
public interface IIrrigationStore
{
    Task<bool> AuthorisedAsync(IrrigationCommand command,string actor,CancellationToken ct);
    Task<IrrigationCommandRecord?> FindAsync(Guid commandId,CancellationToken ct);
    Task<string> ProcessAsync(IrrigationEnvelope envelope,DateTimeOffset processedAt,CancellationToken ct);
}
public class IrrigationStore(IrrigationDbContext db):IIrrigationStore
{
    public Task<IrrigationCommandRecord?> FindAsync(Guid id,CancellationToken ct)=>db.Commands.AsNoTracking().SingleOrDefaultAsync(c=>c.CommandId==id,ct);
    public async Task<bool> AuthorisedAsync(IrrigationCommand command,string actor,CancellationToken ct)
    {
        var device=await db.Devices.AsNoTracking().SingleOrDefaultAsync(d=>d.DeviceId==command.DeviceId,ct);
        return device is not null && await AuthorisedDeviceAsync(device,command,actor,ct);
    }
    private async Task<bool> AuthorisedDeviceAsync(IrrigationDevice device,IrrigationCommand c,string actor,CancellationToken ct)
    {
        if(!device.IsEnabled || device.FarmId!=c.FarmId || device.FieldId!=c.FieldId)return false;
        if(c.Source=="Sensor")return !string.IsNullOrEmpty(device.SensorSourceId) && device.SensorSourceId==actor;
        // Resolve current ownership and Farmer role from the existing authoritative SQL tables.
        return await db.Database.SqlQuery<int>($"SELECT f.Id AS Value FROM Farms f JOIN LoginRoles ur ON ur.UserId=f.FarmerId JOIN Roles r ON r.Id=ur.RoleId WHERE f.Id={c.FarmId} AND f.FarmerId={actor} AND r.Name='Farmer'").AnyAsync(ct);
    }
    public async Task<string> ProcessAsync(IrrigationEnvelope envelope,DateTimeOffset processedAt,CancellationToken ct)
    {
        var result="";
        await db.Database.CreateExecutionStrategy().ExecuteAsync(async()=>{
            db.ChangeTracker.Clear();
            await using var transaction=await db.Database.BeginTransactionAsync(IsolationLevel.Serializable,ct);
            var c=envelope.Command;
            if(db.Database.IsSqlServer()) {
                var resource="AgriStream.Irrigation:"+c.CommandId.ToString("N");
                await db.Database.ExecuteSqlInterpolatedAsync($"DECLARE @result int; EXEC @result=sys.sp_getapplock @Resource={resource}, @LockMode='Exclusive', @LockOwner='Transaction', @LockTimeout=30000; IF @result<0 THROW 50002,'Irrigation command lock unavailable.',1;",ct);
            }
            var existing=await FindAsync(c.CommandId,ct);
            if(existing is not null) {
                if(existing.CommandHash!=IrrigationValidation.Hash(c) || existing.ActorId!=envelope.ActorId)throw new InvalidIrrigationCommandException("Command ID was reused with a different payload.");
                result="AlreadyProcessed"; await transaction.CommitAsync(ct); return;
            }
            var devices=db.Database.IsSqlServer()
                ? db.Devices.FromSqlInterpolated($"SELECT * FROM IrrigationDevices WITH (UPDLOCK,HOLDLOCK) WHERE DeviceId={c.DeviceId}")
                : db.Devices.Where(d=>d.DeviceId==c.DeviceId);
            var device=await devices.SingleOrDefaultAsync(ct);
            if(device is null || !await AuthorisedDeviceAsync(device,c,envelope.ActorId,ct))throw new InvalidIrrigationCommandException("Device/farm/actor is no longer authorised.");
            result=device.LastAcceptedAt is not null && envelope.AcceptedAt<=device.LastAcceptedAt ? "Superseded":"Processed";
            if(result=="Processed") {
                device.OperationalStatus=c.Action=="Start"?"IrrigationRequested":"StopRequested";
                device.LastCommandId=c.CommandId; device.LastAction=c.Action; device.LastAcceptedAt=envelope.AcceptedAt;
                device.LastProcessedAt=processedAt; device.LastOutcome="Requested state recorded; no physical actuator integration.";
            }
            db.Commands.Add(new IrrigationCommandRecord{CommandId=c.CommandId,DeviceId=c.DeviceId,FarmId=c.FarmId,FieldId=c.FieldId,Action=c.Action,Source=c.Source,ActorId=envelope.ActorId,CommandHash=IrrigationValidation.Hash(c),AcceptedAt=envelope.AcceptedAt,ProcessedAt=processedAt,Outcome=result});
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        });
        return result;
    }
}
