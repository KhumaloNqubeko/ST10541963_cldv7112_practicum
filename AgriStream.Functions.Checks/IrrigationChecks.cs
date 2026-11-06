using System.Net;
using System.Reflection;
using System.Text.Json;
using AgriStream.Functions;
using AgriStream.Irrigation;
using Azure;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

internal static class IrrigationChecks
{
    public static async Task RunAsync()
    {
        var count=0;void Check(bool value,string label){if(!value)throw new InvalidOperationException(label);count++;}
        const string secret="irrigation-test-secret-at-least-32-bytes";
        var now=DateTimeOffset.Parse("2026-11-06T12:00:00Z");
        var clock=new TestClock(now);
        var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{["IrrigationAccess:SigningKey"]=secret}).Build();
        await using var connection=new SqliteConnection("Data Source=:memory:");await connection.OpenAsync();
        await using var db=new IrrigationDbContext(new DbContextOptionsBuilder<IrrigationDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        await db.Database.ExecuteSqlRawAsync("CREATE TABLE Farms(Id INTEGER PRIMARY KEY,FarmerId TEXT); CREATE TABLE Roles(Id TEXT PRIMARY KEY,Name TEXT); CREATE TABLE LoginRoles(UserId TEXT,RoleId TEXT); INSERT INTO Farms VALUES(1,'farmer-a'),(2,'farmer-b'); INSERT INTO Roles VALUES('farmer-role','Farmer'); INSERT INTO LoginRoles VALUES('farmer-a','farmer-role'),('farmer-b','farmer-role');");
        db.Devices.Add(new IrrigationDevice{DeviceId="pump-a",FarmId=1,FieldId="field-001",Name="Pump A"});
        db.Devices.Add(new IrrigationDevice{DeviceId="pump-b",FarmId=2,FieldId="field-002",Name="Pump B"});await db.SaveChangesAsync();
        var store=new IrrigationStore(db);var queue=new TestQueue();
        var submit=new SubmitIrrigationCommand(store,queue,config,clock,NullLogger<SubmitIrrigationCommand>.Instance);
        var process=new ProcessIrrigationCommand(store,config,clock,NullLogger<ProcessIrrigationCommand>.Instance);
        var options=new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var c=new IrrigationCommand(Guid.NewGuid(),"pump-a",1,"field-001","Start","Farmer");
        TelemetryChecks.TestRequest Request(IrrigationCommand command,string actor="farmer-a",bool signed=true){
            var request=new TelemetryChecks.TestRequest("POST",JsonSerializer.Serialize(command,options));
            if(signed)request.Headers.Add(IrrigationSecurity.Header,IrrigationSecurity.SignRequest(command,actor,secret,now));return request;
        }
        var trigger=typeof(SubmitIrrigationCommand).GetMethod("RunAsync")!.GetParameters()[0].GetCustomAttribute<Microsoft.Azure.Functions.Worker.HttpTriggerAttribute>();
        Check(trigger is not null && trigger.AuthLevel==AuthorizationLevel.Function && trigger.Route=="irrigation/commands" && trigger.Methods.SequenceEqual(["post"]),"Function 3 POST metadata");
        var queueTrigger=typeof(ProcessIrrigationCommand).GetMethod("RunAsync")!.GetParameters()[0].GetCustomAttribute<QueueTriggerAttribute>();
        Check(queueTrigger?.QueueName=="irrigation-commands" && queueTrigger.Connection=="IrrigationQueueConnection","Function 4 queue metadata");
        Check((await submit.RunAsync(Request(c),default)).StatusCode==HttpStatusCode.Accepted && queue.Messages.Count==1,"Valid POST queues a message");
        Check(queue.Messages[0].Command==c && IrrigationSecurity.VerifyEnvelope(queue.Messages[0],secret),"Queue envelope is canonical and signed");
        Check((await submit.RunAsync(new TelemetryChecks.TestRequest("POST","not-json"),default)).StatusCode==HttpStatusCode.BadRequest,"Malformed JSON returns 400");
        Check((await submit.RunAsync(new TelemetryChecks.TestRequest("POST",JsonSerializer.Serialize(c with{Action="ActivateAnything"},options)),default)).StatusCode==HttpStatusCode.BadRequest,"Invalid action returns 400");
        Check((await submit.RunAsync(Request(c,signed:false),default)).StatusCode==HttpStatusCode.Forbidden,"Direct Function key cannot bypass signed actor access");
        Check((await submit.RunAsync(Request(c,"farmer-b"),default)).StatusCode==HttpStatusCode.Forbidden,"Farmer cannot command another farm's device");
        Check((await submit.RunAsync(Request(c with{FarmId=2}),default)).StatusCode==HttpStatusCode.Forbidden,"Spoofed farm is rejected against registry");
        Check((await submit.RunAsync(Request(c with{FieldId="field-002"}),default)).StatusCode==HttpStatusCode.Forbidden,"Spoofed field is rejected against registry");
        var forged=Request(c);forged.Headers.Remove(IrrigationSecurity.Header);forged.Headers.Add(IrrigationSecurity.Header,"forged.signature");
        Check((await submit.RunAsync(forged,default)).StatusCode==HttpStatusCode.Forbidden,"Forged signature rejected");
        queue.Failure=new RequestFailedException(503,"private details","ServerBusy",null);
        Check((await submit.RunAsync(Request(c),default)).StatusCode==HttpStatusCode.ServiceUnavailable,"Queue outage returns retryable 503");queue.Failure=null;
        var envelope=queue.Messages[0];var json=JsonSerializer.Serialize(envelope,options);
        await process.RunAsync(json,default);db.ChangeTracker.Clear();
        var device=await db.Devices.SingleAsync(d=>d.DeviceId=="pump-a");
        Check(device.OperationalStatus=="IrrigationRequested" && device.LastCommandId==c.CommandId && device.LastAction=="Start" && device.LastProcessedAt==now,"Queue deserialization updates operational requested state");
        Check(device.LastOutcome!.Contains("no physical") && await db.Commands.CountAsync()==1,"Ledger and accurate non-physical outcome recorded atomically");
        await process.RunAsync(json,default);Check(await db.Commands.CountAsync()==1,"Duplicate queue delivery does not repeat completed command");
        Check((await submit.RunAsync(Request(c),default)).StatusCode==HttpStatusCode.Accepted && queue.Messages.Count==1,"Duplicate completed POST does not requeue");
        Check((await submit.RunAsync(Request(c with{Action="Stop"}),default)).StatusCode==HttpStatusCode.Conflict,"Command ID cannot be reused with different payload");
        var stop=c with{CommandId=Guid.NewGuid(),Action="Stop"};
        await process.RunAsync(JsonSerializer.Serialize(IrrigationSecurity.Seal(stop,"farmer-a",now.AddMinutes(1),secret),options),default);
        db.ChangeTracker.Clear();Check((await db.Devices.SingleAsync(d=>d.DeviceId=="pump-a")).OperationalStatus=="StopRequested","Stop command records requested stop state");
        var stale=c with{CommandId=Guid.NewGuid()};await process.RunAsync(JsonSerializer.Serialize(IrrigationSecurity.Seal(stale,"farmer-a",now.AddMinutes(-1),secret),options),default);
        db.ChangeTracker.Clear();Check((await db.Devices.SingleAsync(d=>d.DeviceId=="pump-a")).LastCommandId==stop.CommandId && (await store.FindAsync(stale.CommandId,default))!.Outcome=="Superseded","Old queued messages cannot regress device status");
        var rejected=false;try{await process.RunAsync("{bad-json",default);}catch(JsonException){rejected=true;}
        Check(rejected,"Invalid JSON is thrown for runtime retry/poison handling");
        rejected=false;try{await process.RunAsync(JsonSerializer.Serialize(envelope with{Signature="forged"},options),default);}catch(InvalidIrrigationCommandException){rejected=true;}
        Check(rejected,"Tampered queue payload fails without SQL updates");
        var transient=new FailingStore();var retryProcessor=new ProcessIrrigationCommand(transient,config,clock,NullLogger<ProcessIrrigationCommand>.Instance);
        rejected=false;try{await retryProcessor.RunAsync(json,default);}catch(TimeoutException){rejected=true;}
        Check(rejected && transient.Attempts==1,"SQL transient failure propagates for queue retry");
        await retryProcessor.RunAsync(json,default);Check(transient.Attempts==2,"Retry can complete after SQL failure");
        var sensorCommand=c with{CommandId=Guid.NewGuid(),Source="Sensor"};
        Check((await submit.RunAsync(new TelemetryChecks.TestRequest("POST",JsonSerializer.Serialize(sensorCommand,options)),default)).StatusCode==HttpStatusCode.Forbidden,"Sensor automation disabled without provisioned credentials");
        Check(await db.Devices.Where(d=>d.DeviceId=="pump-b").Select(d=>d.OperationalStatus).SingleAsync()=="Idle","Other farm's device remains unchanged");
        await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER RejectCommand BEFORE INSERT ON IrrigationCommands BEGIN SELECT RAISE(ABORT,'Simulated ledger failure'); END;");
        var rollback=c with{CommandId=Guid.NewGuid()};rejected=false;
        try{await process.RunAsync(JsonSerializer.Serialize(IrrigationSecurity.Seal(rollback,"farmer-a",now.AddMinutes(2),secret),options),default);}catch(DbUpdateException){rejected=true;}
        db.ChangeTracker.Clear();
        Check(rejected && (await db.Devices.SingleAsync(d=>d.DeviceId=="pump-a")).OperationalStatus=="StopRequested" && await store.FindAsync(rollback.CommandId,default) is null,"Ledger failure rolls back requested-state update");
        await db.Database.ExecuteSqlRawAsync("DROP TRIGGER RejectCommand;");
        const string sensorSecret="separate-sensor-test-secret-at-least-32-bytes";
        config["IrrigationAccess:SensorSigningKey"]=sensorSecret;
        var provisioned=await db.Devices.SingleAsync(d=>d.DeviceId=="pump-a");provisioned.SensorSourceId="soil-sensor-a";await db.SaveChangesAsync();
        var sensorRequest=new TelemetryChecks.TestRequest("POST",JsonSerializer.Serialize(sensorCommand,options));
        sensorRequest.Headers.Add(IrrigationSecurity.Header,IrrigationSecurity.SignRequest(sensorCommand,"soil-sensor-a",sensorSecret,now));
        Check((await submit.RunAsync(sensorRequest,default)).StatusCode==HttpStatusCode.Accepted,"Provisioned trusted sensor backend can queue command");
        var wrongSensorCommand=sensorCommand with{CommandId=Guid.NewGuid()};var wrongSensor=new TelemetryChecks.TestRequest("POST",JsonSerializer.Serialize(wrongSensorCommand,options));
        wrongSensor.Headers.Add(IrrigationSecurity.Header,IrrigationSecurity.SignRequest(wrongSensorCommand,"different-sensor",sensorSecret,now));
        Check((await submit.RunAsync(wrongSensor,default)).StatusCode==HttpStatusCode.Forbidden,"Sensor credentials cannot control an unassigned controller");
        await db.Database.ExecuteSqlRawAsync("UPDATE Farms SET FarmerId='farmer-b' WHERE Id=1;");
        Check((await submit.RunAsync(Request(c with{CommandId=Guid.NewGuid()}),default)).StatusCode==HttpStatusCode.Forbidden,"HTTP checks current SQL ownership after reassignment");
        rejected=false;try{await process.RunAsync(JsonSerializer.Serialize(IrrigationSecurity.Seal(c with{CommandId=Guid.NewGuid()},"farmer-a",now.AddMinutes(3),secret),options),default);}catch(InvalidIrrigationCommandException){rejected=true;}
        Check(rejected,"Queued commands recheck current SQL owner before processing");
        Console.WriteLine($"Irrigation Function/SQL checks passed: {count} assertions.");
    }
    private sealed class TestClock(DateTimeOffset now):TimeProvider{public override DateTimeOffset GetUtcNow()=>now;}
    private sealed class TestQueue:IIrrigationQueue{public List<IrrigationEnvelope> Messages {get;}=[];public Exception? Failure {get;set;}public Task SendAsync(IrrigationEnvelope e,CancellationToken ct){if(Failure is not null)throw Failure;Messages.Add(e);return Task.CompletedTask;}}
    private sealed class FailingStore:IIrrigationStore
    {
        public int Attempts{get;private set;}
        public Task<bool> AuthorisedAsync(IrrigationCommand c,string a,CancellationToken ct)=>Task.FromResult(true);
        public Task<IrrigationCommandRecord?> FindAsync(Guid id,CancellationToken ct)=>Task.FromResult<IrrigationCommandRecord?>(null);
        public Task<string> ProcessAsync(IrrigationEnvelope e,DateTimeOffset t,CancellationToken ct){if(++Attempts==1)throw new TimeoutException("Simulated transient SQL failure");return Task.FromResult("Processed");}
    }
}
