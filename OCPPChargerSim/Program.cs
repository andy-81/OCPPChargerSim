using System;
using System.IO;
using System.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OcppWeb.Hubs;
using OcppWeb.Services;

var builder = WebApplication.CreateBuilder(args);

var dataDirectory = ResolveDataDirectory(builder.Environment);
builder.Configuration.AddJsonFile(Path.Combine(dataDirectory, "simulator.json"), optional: true, reloadOnChange: true);

var chargerCatalog = ChargerCatalog.Load(builder.Environment.ContentRootPath);
builder.Services.AddSingleton(chargerCatalog);

var storageOptions = new SimulatorStorageOptions(dataDirectory);
builder.Services.AddSingleton(storageOptions);

builder.Services.AddSignalR();
builder.Services.AddSingleton<SimulatorState>();
builder.Services.AddSingleton<SimulatorCoordinator>();
builder.Services.AddSingleton(sp => new SimulatorConfigurationProvider(builder.Configuration, storageOptions.DataDirectory, chargerCatalog));
builder.Services.AddHostedService<SimulatorHostedService>();

builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenAnyIP(5000);
});

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapHub<SimulatorHub>("/hub/simulator");

app.MapGet("/api/logs", (SimulatorState state) => Results.Ok(state.GetLogs()));

app.MapGet("/api/status", (SimulatorState state) =>
{
    var sample = state.LatestSample;
    return Results.Ok(new
    {
        vehicleState = state.VehicleState,
        metrics = new
        {
            energyWh = sample.EnergyWh,
            powerKw = sample.PowerKw,
            currentAmps = sample.CurrentAmps,
            stateOfCharge = sample.StateOfCharge >= 0 ? sample.StateOfCharge : (double?)null,
            timestamp = sample.Timestamp,
        },
    });
});

app.MapGet("/api/configuration", (SimulatorState state) => Results.Ok(state.GetConfiguration()));

app.MapPost("/api/status", async (StatusRequest request, SimulatorCoordinator coordinator, HttpContext context) =>
{
    try
    {
        await coordinator.SendManualStatusAsync(request.Status, context.RequestAborted).ConfigureAwait(false);
        return Results.Accepted();
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapPost("/api/configuration", (ConfigurationRequest request, SimulatorCoordinator coordinator) =>
{
    try
    {
        coordinator.SetLocalConfiguration(request.Key, request.Value);
        return Results.Accepted();
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapPost("/api/heartbeat", async (SimulatorCoordinator coordinator, HttpContext context) =>
{
    try
    {
        await coordinator.SendHeartbeatAsync(context.RequestAborted).ConfigureAwait(false);
        return Results.Accepted();
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (Exception ex)
    {
        return Results.Problem(detail: ex.Message, statusCode: 500);
    }
});

app.MapPost("/api/close", async (SimulatorCoordinator coordinator, HttpContext context) =>
{
    try
    {
        await coordinator.CloseConnectionAsync(context.RequestAborted).ConfigureAwait(false);
        return Results.Accepted();
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (Exception ex)
    {
        return Results.Problem(detail: ex.Message, statusCode: 500);
    }
});

app.MapPost("/api/logging", (LoggingRequest request, SimulatorCoordinator coordinator) =>
{
    try
    {
        coordinator.SetLoggingEnabled(request.Enabled);
        return Results.Accepted();
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (Exception ex)
    {
        return Results.Problem(detail: ex.Message, statusCode: 500);
    }
});

// ---------------------------------------------------------------------------
// External meter values endpoint
// Called by Home Assistant (or any external source) to push real meter data.
// The simulator will use these values in the next MeterValues message to Octopus.
// POST /api/meters
// {
//   "energyWhImport": 256937,       // Energy.Active.Import.Register in Wh
//   "powerKwImport": 3.45,          // Power.Active.Import in kW
//   "frequencyHz": 50.01,           // Frequency in Hz
//   "powerKwOffered": 3.45,         // Power.Offered in kW
//   "currentAmpsOffered": 15.0,     // Current.Offered in A
//   "stateOfChargePercent": 42.0    // SoC in % (optional)
// }
// All fields are optional — omit any you don't have and the simulator will
// fall back to its own calculated value for that measurand.
// ---------------------------------------------------------------------------
app.MapPost("/api/meters", (OcppSimulator.ExternalMeterValues values, SimulatorState state) =>
{
    state.SetExternalMeterValues(values);
    return Results.Accepted();
});

app.MapGet("/api/meters", (SimulatorState state) =>
{
    var values = state.GetExternalMeterValues();
    if (values is null)
    {
        return Results.Ok(new { source = "simulated", values = (object?)null });
    }

    return Results.Ok(new { source = "external", values });
});

app.MapDelete("/api/meters", (SimulatorState state) =>
{
    state.SetExternalMeterValues(null);
    return Results.Accepted();
});

app.MapGet("/api/state", (SimulatorState state, SimulatorConfigurationProvider configProvider, ChargerCatalog catalog) =>
{
    var sample = state.LatestSample;
    var (url, identity, authKey) = state.GetConnectionDetails();
    var (requiresConfiguration, configFileMissing) = state.ConfigurationStatus;
    var (chargePointSerial, chargeBoxSerial) = state.GetSerialNumbers();
    var externalMeters = state.GetExternalMeterValues();
    return Results.Ok(new
    {
        vehicleState = state.VehicleState,
        configuration = state.GetConfiguration(),
        logs = state.GetLogs(),
        metrics = new
        {
            energyWh = sample.EnergyWh,
            powerKw = sample.PowerKw,
            currentAmps = sample.CurrentAmps,
            stateOfCharge = sample.StateOfCharge >= 0 ? sample.StateOfCharge : (double?)null,
            timestamp = sample.Timestamp,
        },
        connection = new { url, identity, authKey },
        loggingEnabled = state.LoggingEnabled,
        requiresConfiguration,
        configurationFileMissing = configFileMissing,
        chargers = catalog.Chargers.Select(c => new
        {
            c.Id,
            c.Make,
            c.Model,
            c.ChargePointModel,
            c.ChargePointVendor,
        }),
        selectedCharger = state.SelectedChargerId,
        serialNumbers = new { chargePointSerial, chargeBoxSerial },
        externalMeterSource = externalMeters is not null ? "external" : "simulated",
    });
});

app.MapPost("/api/bootstrap", async (BootstrapRequest request, SimulatorConfigurationProvider provider, SimulatorState state, ChargerCatalog catalog, CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.Url) || string.IsNullOrWhiteSpace(request.Identity) || string.IsNullOrWhiteSpace(request.AuthKey))
    {
        return Results.BadRequest(new { error = "All fields are required." });
    }

    if (string.IsNullOrWhiteSpace(request.ChargerId) || !catalog.TryGet(request.ChargerId, out _))
    {
        return Results.BadRequest(new { error = "Please select a valid charger type." });
    }

    var cpSerial = string.IsNullOrWhiteSpace(request.ChargePointSerialNumber) ? "0" : request.ChargePointSerialNumber.Trim();
    var cbSerial = string.IsNullOrWhiteSpace(request.ChargeBoxSerialNumber) ? "0" : request.ChargeBoxSerialNumber.Trim();

    var snapshot = await provider.PersistAsync(new SimulatorOptions
    {
        Url = request.Url,
        Identity = request.Identity,
        AuthKey = request.AuthKey,
        ChargerId = request.ChargerId,
        ChargePointSerialNumber = cpSerial,
        ChargeBoxSerialNumber = cbSerial,
    }, cancellationToken).ConfigureAwait(false);

    state.SetConfigurationRequirement(snapshot.RequiresConfiguration, snapshot.ConfigurationFileMissing);
    state.SetConnectionDetails(snapshot.Options.Url ?? "—", snapshot.Options.Identity ?? "—", snapshot.Options.AuthKey ?? "—");
    state.SetSelectedCharger(snapshot.Options.ChargerId);
    state.SetSerialNumbers(snapshot.Options.ChargePointSerialNumber ?? "0", snapshot.Options.ChargeBoxSerialNumber ?? "0");

    return Results.Accepted();
});

app.MapFallbackToFile("index.html");

app.Run();

static string ResolveDataDirectory(IHostEnvironment environment)
{
    var configured = Environment.GetEnvironmentVariable("APP_DATA");
    var basePath = string.IsNullOrWhiteSpace(configured)
        ? environment.ContentRootPath
        : Path.GetFullPath(configured);

    Directory.CreateDirectory(basePath);
    return basePath;
}

public sealed record SimulatorStorageOptions(string DataDirectory);

record StatusRequest(string Status);

record ConfigurationRequest(string Key, string Value);

record LoggingRequest(bool Enabled);

record BootstrapRequest(string Url, string Identity, string AuthKey, string ChargerId, string ChargePointSerialNumber, string ChargeBoxSerialNumber);
