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
builder.Services.AddHostedService<MqttIntegrationService>();

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

app.MapGet("/api/state", (SimulatorState state, SimulatorConfigurationProvider configProvider, ChargerCatalog catalog) =>
{
    var sample = state.LatestSample;
    var (url, identity, authKey) = state.GetConnectionDetails();
    var (requiresConfiguration, configFileMissing) = state.ConfigurationStatus;
    var (chargePointSerial, chargeBoxSerial) = state.GetSerialNumbers();
    var mqtt = state.GetMqttConfiguration();
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
        mqtt = new
        {
            enabled = mqtt.Enabled,
            host = mqtt.Host,
            port = mqtt.Port,
            username = mqtt.Username,
            password = mqtt.Password,
            statusTopic = mqtt.StatusTopic,
            publishTopic = mqtt.PublishTopic,
        },
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

    if (request.EnableMqtt)
    {
        if (string.IsNullOrWhiteSpace(request.MqttHost))
        {
            return Results.BadRequest(new { error = "Please provide an MQTT host when MQTT integration is enabled." });
        }

        if (string.IsNullOrWhiteSpace(request.MqttPublishTopic))
        {
            return Results.BadRequest(new { error = "Please provide an MQTT publish topic when MQTT integration is enabled." });
        }

        if (string.IsNullOrWhiteSpace(request.MqttStatusTopic))
        {
            return Results.BadRequest(new { error = "Please provide an MQTT status topic when MQTT integration is enabled." });
        }

        if (request.MqttPort.HasValue && (request.MqttPort.Value <= 0 || request.MqttPort.Value > 65535))
        {
            return Results.BadRequest(new { error = "MQTT port must be between 1 and 65535." });
        }
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
        EnableMqtt = request.EnableMqtt,
        MqttHost = request.MqttHost,
        MqttPort = request.MqttPort,
        MqttUsername = request.MqttUsername,
        MqttPassword = request.MqttPassword,
        MqttStatusTopic = request.MqttStatusTopic,
        MqttPublishTopic = request.MqttPublishTopic,
    }, cancellationToken).ConfigureAwait(false);

    state.SetConfigurationRequirement(snapshot.RequiresConfiguration, snapshot.ConfigurationFileMissing);
    state.SetConnectionDetails(snapshot.Options.Url ?? "—", snapshot.Options.Identity ?? "—", snapshot.Options.AuthKey ?? "—");
    state.SetSelectedCharger(snapshot.Options.ChargerId);
    state.SetSerialNumbers(snapshot.Options.ChargePointSerialNumber ?? "0", snapshot.Options.ChargeBoxSerialNumber ?? "0");
    state.SetMqttConfiguration(
        snapshot.Options.EnableMqtt,
        snapshot.Options.MqttHost,
        snapshot.Options.MqttPort,
        snapshot.Options.MqttUsername,
        snapshot.Options.MqttPassword,
        snapshot.Options.MqttStatusTopic,
        snapshot.Options.MqttPublishTopic);

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

record BootstrapRequest(
    string Url,
    string Identity,
    string AuthKey,
    string ChargerId,
    string ChargePointSerialNumber,
    string ChargeBoxSerialNumber,
    bool EnableMqtt,
    string? MqttHost,
    int? MqttPort,
    string? MqttUsername,
    string? MqttPassword,
    string? MqttStatusTopic,
    string? MqttPublishTopic);
