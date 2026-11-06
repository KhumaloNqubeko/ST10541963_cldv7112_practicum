using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using AgriStream.Controllers;
using AgriStream.Data;
using AgriStream.Irrigation;
using AgriStream.Models;
using AgriStream.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;

internal static class IrrigationMvcChecks
{
    public static async Task RunAsync()
    {
        var count=0;void Check(bool value,string label){if(!value)throw new InvalidOperationException(label);count++;}
        var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{
            ["Irrigation:Enabled"]="true",["IrrigationFunctions:BaseUrl"]="https://example.test/api",["IrrigationFunctions:FunctionKey"]="test-key",["IrrigationAccess:SigningKey"]="irrigation-secret-with-at-least-32-bytes",
            ["TelemetryFunctions:Fields:field-001:FarmId"]="1",["TelemetryFunctions:Fields:field-002:FarmId"]="2"}).Build();
        await using var db=new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        db.Farms.AddRange(new Farm{Id=1,FarmerId="farmer-a",Name="A",Location="A"},new Farm{Id=2,FarmerId="farmer-b",Name="B",Location="B"});
        var pump=new IrrigationDevice{DeviceId="pump-a",FarmId=1,FieldId="field-001",Name="Pump A"};
        db.IrrigationDevices.AddRange(pump,new IrrigationDevice{DeviceId="pump-b",FarmId=2,FieldId="field-002",Name="Pump B"});await db.SaveChangesAsync();
        var clock=TimeProvider.System;var handler=new TestHandler();using var http=new HttpClient(handler);
        var access=new FarmAccess(db);var fields=new FarmerTelemetryDashboard(access,new FieldTelemetryClient(http,config,clock),config,NullLogger<FarmerTelemetryDashboard>.Instance);
        var dashboard=new IrrigationDashboard(db,fields,new EphemeralDataProtectionProvider(),config,clock);
        var user=new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier,"farmer-a"),new Claim(ClaimTypes.Role,"Farmer")],"Test"));
        var model=new MonitoringDashboardViewModel{SelectedFieldId="field-001"};await dashboard.PopulateAsync(model,user,default);
        Check(model.IrrigationEnabled && model.IrrigationDevices.Count==1 && model.IrrigationDevices[0].Device.DeviceId=="pump-a","Controls shown only for owned farm/field devices");
        var token=model.IrrigationDevices[0].StartToken;var form=dashboard.Read(token)!;
        Check(form.Command.Source=="Farmer" && form.UserId=="farmer-a" && form.Command.Action=="Start","Server-issued form binds action and actor");
        Check(dashboard.Read(token)!.Command.CommandId==form.Command.CommandId,"Retries preserve original command ID");
        Check(dashboard.Read(token+"tampered") is null,"Tampered form rejected");
        Check(await dashboard.OwnsAsync(user,form.Command,default),"Owner may command device");
        Check(!await dashboard.OwnsAsync(user,form.Command with{DeviceId="pump-b",FarmId=2,FieldId="field-002"},default),"Farmer cannot command another farm");
        var client=new IrrigationCommandClient(http,config,clock);var receipt=await client.SubmitAsync(form.Command,"farmer-a",default);
        Check(receipt.CommandId==form.Command.CommandId && handler.RequestUri=="https://example.test/api/irrigation/commands" && handler.Method==HttpMethod.Post,"Typed server-side client POSTs exact route");
        Check(handler.Key=="test-key" && IrrigationSecurity.VerifyRequest(form.Command,handler.Assertion,config["IrrigationAccess:SigningKey"]!,clock.GetUtcNow())=="farmer-a","Key and command-bound assertion remain server-side");
        Check(typeof(IrrigationController).GetMethod("Trigger")!.GetCustomAttributes(typeof(ValidateAntiForgeryTokenAttribute),true).Length==1,"Farmer form endpoint enforces CSRF protection");
        Check(pump.OperationalStatus=="Idle","MVC never claims physical activation or pre-processes registry state");
        handler.Status=HttpStatusCode.ServiceUnavailable;
        var logger=new TestLogger();
        var controller=new IrrigationController(dashboard,client,logger){ControllerContext=new ControllerContext{HttpContext=new DefaultHttpContext{User=user}}};
        var failed=(ViewResult)await controller.Trigger(token,default);
        Check(failed.Model is IrrigationResult retry && retry.CommandId==form.Command.CommandId && retry.RetryToken==token,"HTTP failure preserves original command for safe retry");
        Check(logger.Message!.Contains("HttpStatus=503") && !logger.Message.Contains("test-key") && !logger.Message.Contains(handler.Assertion!,StringComparison.Ordinal),"HTTP diagnostics include status without credentials or signatures");
        pump.IsEnabled=false;await db.SaveChangesAsync();Check(!await dashboard.OwnsAsync(user,form.Command,default),"Disabled controller cannot be triggered");
        config["Irrigation:Enabled"]="false";model=new();await dashboard.PopulateAsync(model,user,default);
        Check(!model.IrrigationEnabled && model.IrrigationDevices.Count==0,"Feature disabled preserves existing dashboard before migration");
        Console.WriteLine($"MVC irrigation checks passed: {count} assertions.");
    }
    private sealed class TestHandler:HttpMessageHandler
    {
        public string? RequestUri{get;private set;}
        public HttpMethod? Method{get;private set;}
        public string? Key{get;private set;}
        public string? Assertion{get;private set;}
        public HttpStatusCode Status{get;set;}=HttpStatusCode.Accepted;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            RequestUri=request.RequestUri!.ToString();Method=request.Method;Key=request.Headers.GetValues("x-functions-key").Single();Assertion=request.Headers.GetValues(IrrigationSecurity.Header).Single();
            var command=(await request.Content!.ReadFromJsonAsync<IrrigationCommand>(cancellationToken:ct))!;
            return new HttpResponseMessage(Status){Content=JsonContent.Create(new IrrigationReceipt(command.CommandId,"Queued"))};
        }
    }
    private sealed class TestLogger:ILogger<IrrigationController>
    {
        public string? Message{get;private set;}
        public IDisposable? BeginScope<TState>(TState state)where TState:notnull=>null;
        public bool IsEnabled(LogLevel level)=>true;
        public void Log<TState>(LogLevel level,EventId id,TState state,Exception? exception,Func<TState,Exception?,string> formatter)=>Message=formatter(state,exception);
    }
}
