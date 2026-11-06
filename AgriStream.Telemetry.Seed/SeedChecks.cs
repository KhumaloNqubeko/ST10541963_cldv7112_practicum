using AgriStream.Functions;
using Azure;
using Azure.Core;
using Azure.Data.Tables;

namespace AgriStream.Telemetry.Seed;

internal static class SeedChecks
{
    public static async Task RunAsync()
    {
        var count=0;
        void Check(bool condition,string label){if(!condition)throw new InvalidOperationException(label);count++;}
        var plan=DemoTelemetrySeed.Plan(DemoTelemetrySeed.DefaultAnchor);
        Check(plan.Count==9 && plan.GroupBy(r=>r.PartitionKey).All(g=>g.Count()==3),"Three sensors per field");
        Check(plan.Select(r=>(r.PartitionKey,r.RowKey)).Distinct().Count()==9 && plan.All(r=>r.RowKey=="latest|"+r.GetString("SensorId")),"Stable correct snapshot keys");
        Check(plan.All(r=>r["SoilMoisturePercent"] is double && r["TemperatureCelsius"] is double && r["HumidityPercent"] is double && r["RecordedAt"] is DateTimeOffset d && d.Offset==TimeSpan.Zero),"Exact Table numeric and UTC timestamp types");
        var table=new TestTable();
        var first=plan[0];
        var newer=new TableEntity(first.PartitionKey,first.RowKey){["SensorId"]=first.GetString("SensorId"),["SoilMoisturePercent"]=89.0,["RecordedAt"]=DemoTelemetrySeed.DefaultAnchor.AddDays(7)};
        table.Rows.Add((newer.PartitionKey,newer.RowKey),newer);
        await DemoTelemetrySeed.PreflightAsync(table,plan,default);
        var added=0;
        foreach(var row in plan) if(await DemoTelemetrySeed.InsertMissingAsync(table,row,default))added++;
        Check(added==8 && table.Rows.Count==9,"Only missing records inserted");
        Check(ReferenceEquals(table.Rows[(first.PartitionKey,first.RowKey)],newer) && newer.GetDouble("SoilMoisturePercent")==89.0,"Newer production reading never replaced");
        added=0;
        foreach(var row in plan) if(await DemoTelemetrySeed.InsertMissingAsync(table,row,default))added++;
        Check(added==0 && table.Rows.Count==9,"Rerun is idempotent");
        Check((await DemoTelemetrySeed.VerifyAsync(table,plan,default)).Count==9,"Post-insert point reads and production reader verification");
        var older=new TableEntity("field-001","latest|older"){["SensorId"]="older",["RecordedAt"]=DemoTelemetrySeed.DefaultAnchor.AddDays(-7),["SoilMoisturePercent"]=10.0};
        table.Rows.Add((older.PartitionKey,older.RowKey),older);
        Check(!await DemoTelemetrySeed.InsertMissingAsync(table,older,default),"Even older existing readings are preserved");
        table.Failure=new RequestFailedException(503,"Test failure","ServerBusy",null);
        var propagated=false;
        try{await DemoTelemetrySeed.InsertMissingAsync(table,plan[0],default);}catch(RequestFailedException){propagated=true;}
        Check(propagated,"Storage failures propagate");
        table.Failure=new RequestFailedException(409,"Test conflict","TableBeingDeleted",null);
        propagated=false;
        try{await DemoTelemetrySeed.InsertMissingAsync(table,plan[0],default);}catch(RequestFailedException){propagated=true;}
        Check(propagated,"Unrelated conflicts are not mistaken for duplicates");
        var full=new TestTable();
        for(var i=0;i<100;i++)full.Rows.Add(("field-001",$"latest|real-{i}"),new TableEntity("field-001",$"latest|real-{i}"));
        var rejected=false;
        try{await DemoTelemetrySeed.PreflightAsync(full,plan,default);}catch(InvalidOperationException){rejected=true;}
        Check(rejected && full.AddCalls==0,"Preflight prevents exceeding Function 2 sensor limit");
        var wrong=new TableClient(new Uri("https://other.table.core.windows.net/SensorTelemetry"),new AzureSasCredential("test-only"));
        rejected=false;
        try{DemoTelemetrySeed.ValidateTarget(wrong);}catch(InvalidOperationException){rejected=true;}
        Check(rejected,"Wrong account blocked before requests");
        var diagnostic=SeedErrors.Describe(new RequestFailedException(403,"AccountKey=do-not-print","AuthorizationPermissionMismatch",null));
        Check(diagnostic.Contains("HTTP status: 403") && diagnostic.Contains("AuthorizationPermissionMismatch"),"HTTP status and Azure error code are reported");
        Check(!diagnostic.Contains("do-not-print") && diagnostic.Contains("access denied"),"Storage message is useful without exposing credentials");
        Check(!SeedErrors.Describe(new ArgumentException("AccountKey=do-not-print")).Contains("do-not-print"),"Local configuration failures do not leak credentials");
        Console.WriteLine($"Seed self-checks passed: {count} assertions. No Azure requests were made.");
    }

    private sealed class TestTable:TableClient
    {
        public Dictionary<(string,string),TableEntity> Rows {get;}=[];
        public int AddCalls {get;private set;}
        public Exception? Failure {get;set;}
        public override Task<Response> AddEntityAsync<T>(T entity,CancellationToken cancellationToken=default)
        {
            AddCalls++;
            if(Failure is not null)throw Failure;
            var row=(TableEntity)(object)entity;
            if(!Rows.TryAdd((row.PartitionKey,row.RowKey),row))throw new RequestFailedException(409,"Exists","EntityAlreadyExists",null);
            return Task.FromResult<Response>(new TestResponse());
        }
        public override Task<Response<T>> GetEntityAsync<T>(string partitionKey,string rowKey,IEnumerable<string>? select=null,CancellationToken cancellationToken=default)
            =>Task.FromResult(Response.FromValue((T)(object)Rows[(partitionKey,rowKey)],new TestResponse()));
        public override AsyncPageable<T> QueryAsync<T>(string? filter=null,int? maxPerPage=null,IEnumerable<string>? select=null,CancellationToken cancellationToken=default)
        {
            var rows=Rows.Values.Where(r=>filter!.Contains($"PartitionKey eq '{r.PartitionKey}'",StringComparison.Ordinal) && string.CompareOrdinal(r.RowKey,"latest|")>=0 && string.CompareOrdinal(r.RowKey,"latest}")<0).Select(r=>(T)(object)r).ToArray();
            return AsyncPageable<T>.FromPages([Page<T>.FromValues(rows,null,new TestResponse())]);
        }
    }
    private sealed class TestResponse:Response
    {
        public override int Status=>200;
        public override string ReasonPhrase=>"Test";
        public override Stream? ContentStream {get;set;}
        public override string ClientRequestId {get;set;}="test";
        public override void Dispose(){}
        protected override bool ContainsHeader(string name)=>false;
        protected override IEnumerable<HttpHeader> EnumerateHeaders()=>[];
        protected override bool TryGetHeader(string name,out string value){value="";return false;}
        protected override bool TryGetHeaderValues(string name,out IEnumerable<string> values){values=[];return false;}
    }
}
