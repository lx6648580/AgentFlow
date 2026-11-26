using System.ComponentModel;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Server;

namespace AgentFlow.DeviceSample;

internal static class Program
{
    private static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        if (string.IsNullOrEmpty(builder.Configuration["urls"]))
            builder.WebHost.UseUrls("http://127.0.0.1:5190");
        var directory = Environment.GetEnvironmentVariable("AGENTFLOW_SAMPLE_DATA") ?? Path.Combine(AppContext.BaseDirectory, "data");
        var sample = new Devices(Path.GetFullPath(directory));
        builder.Services.AddMcpServer().WithHttpTransport(options => options.SessionMode = HttpServerSessionMode.Stateless)
            .WithTools(sample);
        var app = builder.Build();
        app.MapMcp("/mcp");
        app.MapGet("/health", () => Results.Ok(new { status = "ready" }));
        app.MapGet("/api/devices/{deviceId}", (string deviceId) => sample.Find(deviceId) is { } row ? Results.Ok(row) : Results.NotFound());
        app.MapGet("/sim/device", () => Results.Ok(new { deviceId = "pump-01", state = "warning", temperature = 72.5 }));
        app.MapGet("/sim/redirect", () => Results.Redirect("/api/devices/pump-01"));
        app.MapGet("/sim/binary", () => Results.Bytes([1, 0, 255], "application/octet-stream"));
        app.MapGet("/sim/large", () => Results.Text(new string('x', 61000)));
        app.MapGet("/sim/slow", async (CancellationToken token) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(15), token);
            return Results.Ok(new { state = "delayed" });
        });
        app.MapGet("/sim/counts", () => Results.Ok(new { sample.ReadCalls, sample.WriteCalls }));
        await app.RunAsync();
    }
}

public sealed record DeviceStatus(string DeviceId, string State, double Temperature);

[McpServerToolType]
public sealed class Devices
{
    private readonly string database;
    private int reads;
    private int writes;
    public int ReadCalls => Volatile.Read(ref reads);
    public int WriteCalls => Volatile.Read(ref writes);

    public Devices(string directory)
    {
        Directory.CreateDirectory(directory);
        database = Path.Combine(directory, "devices.db");
        if (!File.Exists(database))
        {
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString());
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE devices(device_id TEXT PRIMARY KEY, state TEXT, temperature REAL);
                INSERT INTO devices VALUES('pump-01','warning',72.5),('pump-02','normal',45.0);
                """;
            cmd.ExecuteNonQuery();
        }
        var export = Path.Combine(directory, "device-status.json");
        if (!File.Exists(export))
        {
            using var file = new FileStream(export, FileMode.CreateNew);
            JsonSerializer.Serialize(file, Find("pump-01"), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
    }

    public DeviceStatus? Find(string id)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = database, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT device_id,state,temperature FROM devices WHERE device_id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        using var row = cmd.ExecuteReader();
        return row.Read() ? new(row.GetString(0), row.GetString(1), row.GetDouble(2)) : null;
    }

    [McpServerTool(Name = "read_device", ReadOnly = true, Destructive = false, UseStructuredContent = true)]
    [Description("Read the current status of the device identified by the requested deviceId.")]
    public DeviceStatus ReadDevice(string deviceId)
    {
        Interlocked.Increment(ref reads);
        return Find(deviceId) ?? throw new InvalidOperationException("Unknown device.");
    }

    [McpServerTool(Name = "write_device", ReadOnly = false, Destructive = true)]
    [Description("A denied-operation fixture. This only records an attempted call.")]
    public string WriteDevice(string deviceId)
    {
        Interlocked.Increment(ref writes);
        return "Write fixture invoked";
    }
}
