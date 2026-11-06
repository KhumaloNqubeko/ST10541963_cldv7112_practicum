using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using AgriStream.Data;
using AgriStream.Models;
using AgriStream.Services;
using AgriStream.Telemetry;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

internal static class TelemetryMvcChecks
{
    public static async Task RunAsync()
    {
        var count=0;
        void Check(bool value, string description) { if (!value) throw new InvalidOperationException(description); count++; }
        const string secret="test-only-signing-secret-with-at-least-32-bytes";
        var now=DateTimeOffset.Parse("2026-11-06T10:00:00Z");
        var clock=new TestClock(now);
        var settings=new Dictionary<string,string?> {
            ["TelemetryFunctions:BaseUrl"]="https://functions.example/api", ["TelemetryFunctions:FunctionKey"]="test-function-key",
            ["TelemetryAccess:SigningKey"]=secret, ["TelemetryFunctions:Fields:field-a:FarmId"]="1", ["TelemetryFunctions:Fields:field-b:FarmId"]="2"
        };
        var config=new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        await using var db=new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        db.Farms.AddRange(new Farm { Id=1, FarmerId="farmer-a", Name="A", Location="A" }, new Farm { Id=2, FarmerId="farmer-b", Name="B", Location="B" });
        await db.SaveChangesAsync();
        var handler=new TestHandler(now);
        using var http=new HttpClient(handler);
        var client=new FieldTelemetryClient(http,config,clock);
        var dashboard=new FarmerTelemetryDashboard(new FarmAccess(db),client,config,NullLogger<FarmerTelemetryDashboard>.Instance);
        var user=new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier,"farmer-a"),new Claim(ClaimTypes.Role,"Farmer")],"Test"));
        var model=new MonitoringDashboardViewModel();
        Check(await dashboard.PopulateAsync(model,user,null,default),"Farmer dashboard integration succeeds");
        Check(model.UsesFieldTelemetry && model.SelectedFieldId=="field-a" && model.TelemetryFields.SequenceEqual(["field-a"]) && model.FieldTelemetry!.Readings.Count==1,"Only owned mapped fields are available");
        Check(handler.Calls==1 && handler.Uri=="https://functions.example/api/fields/field-a/telemetry/latest","Farmer dashboard calls the exact Function route");
        Check(handler.FunctionKey=="test-function-key" && !handler.Uri!.Contains("test-function-key"),"Function key stays in server request header");
        Check(FieldAccessAssertion.Validate(handler.Assertion,"field-a",secret,now) && !FieldAccessAssertion.Validate(handler.Assertion,"field-b",secret,now),"Server signs field-bound access only after ownership check");
        Check(!await dashboard.PopulateAsync(new(),user,"field-b",default) && handler.Calls==1,"Other Farmer field rejected before HTTP call");
        Check(!await dashboard.PopulateAsync(new(),user,"unknown-field",default) && handler.Calls==1,"Unknown field returns no data and makes no HTTP call");
        var admin=new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier,"admin"),new Claim(ClaimTypes.Role,"Admin")],"Test"));
        Check(!await dashboard.PopulateAsync(new(),admin,"field-a",default) && handler.Calls==1,"Non-Farmer caller is rejected");
        handler.Status=HttpStatusCode.ServiceUnavailable;
        model=new(); await dashboard.PopulateAsync(model,user,"field-a",default);
        Check(model.TelemetryMessage is not null && model.FieldTelemetry is null,"Unavailable service leaves dashboard usable");
        handler.Status=HttpStatusCode.OK; handler.Empty=true;
        model=new(); await dashboard.PopulateAsync(model,user,"field-a",default);
        Check(model.TelemetryMessage is null && model.FieldTelemetry!.Readings.Count==0,"Empty telemetry is handled gracefully");
        handler.WrongField=true;
        model=new(); await dashboard.PopulateAsync(model,user,"field-a",default);
        Check(model.TelemetryMessage is not null && model.FieldTelemetry is null,"Mismatched response field is never displayed");
        config["TelemetryFunctions:BaseUrl"]="http://insecure.example/api";
        var before=handler.Calls;
        model=new(); await dashboard.PopulateAsync(model,user,"field-a",default);
        Check(model.TelemetryMessage is not null && handler.Calls==before,"Secrets are not sent over HTTP");
        Check(!FieldAccessAssertion.Validate(handler.Assertion,"field-a",secret,now.AddMinutes(2)),"Field access token expires");
        Console.WriteLine($"MVC telemetry checks passed: {count} assertions.");
    }

    private sealed class TestClock(DateTimeOffset now):TimeProvider { public override DateTimeOffset GetUtcNow()=>now; }
    private sealed class TestHandler(DateTimeOffset now):HttpMessageHandler
    {
        public int Calls {get;private set;}
        public string? Uri {get;private set;}
        public string? FunctionKey {get;private set;}
        public string? Assertion {get;private set;}
        public HttpStatusCode Status {get;set;}=HttpStatusCode.OK;
        public bool Empty {get;set;}
        public bool WrongField {get;set;}
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken)
        {
            Calls++; Uri=request.RequestUri!.ToString(); FunctionKey=request.Headers.GetValues("x-functions-key").Single(); Assertion=request.Headers.GetValues(FieldAccessAssertion.HeaderName).Single();
            IReadOnlyList<FieldTelemetryReading> readings=Empty?[]:[new("sensor-a",35,24,50,now)];
            return Task.FromResult(new HttpResponseMessage(Status){Content=JsonContent.Create(new FieldTelemetryResponse(WrongField?"field-b":"field-a",readings))});
        }
    }
}
