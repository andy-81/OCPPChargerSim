using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OcppSimulator;
using OcppWeb.Hubs;

namespace OcppWeb.Services;

public sealed class SimulatorHostedService : BackgroundService
{
    private readonly SimulatorCoordinator _coordinator;
    private readonly SimulatorState _state;
    private readonly IHubContext<SimulatorHub, ISimulatorClient> _hubContext;
    private readonly ILogger<SimulatorHostedService> _logger;
    private readonly SimulatorConfigurationProvider _configurationProvider;
    private readonly SimulatorStorageOptions _storageOptions;
    private readonly ChargerCatalog _catalog;

    public SimulatorHostedService(
        SimulatorCoordinator coordinator,
        SimulatorState state,
        IHubContext<SimulatorHub, ISimulatorClient> hubContext,
        SimulatorConfigurationProvider configurationProvider,
        ChargerCatalog catalog,
        SimulatorStorageOptions storageOptions,
        ILogger<SimulatorHostedService> logger)
    {
        _coordinator = coordinator;
        _state = state;
        _hubContext = hubContext;
        _logger = logger;
        _configurationProvider = configurationProvider;
        _catalog = catalog;
        _storageOptions = storageOptions;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var snapshot = _configurationProvider.Snapshot;
        _state.SetMqttConfiguration(
            snapshot.Options.EnableMqtt,
            snapshot.Options.MqttHost,
            snapshot.Options.MqttPort,
            snapshot.Options.MqttUsername,
            snapshot.Options.MqttPassword,
            snapshot.Options.MqttStatusTopic,
            snapshot.Options.MqttPublishTopic,
            snapshot.Options.MqttMeterTopic,
            snapshot.Options.MqttCurrentTopic);
            _state.SetConfigurationRequirement(snapshot.RequiresConfiguration, snapshot.ConfigurationFileMissing);
            _state.SetSelectedCharger(_catalog.TryGet(snapshot.Options.ChargerId, out _) ? snapshot.Options.ChargerId : null);
            _state.SetSerialNumbers(snapshot.Options.ChargePointSerialNumber ?? "0", snapshot.Options.ChargeBoxSerialNumber ?? "0");

            if (snapshot.RequiresConfiguration)
            {
                try
                {
                    snapshot = await _configurationProvider.WaitForValidAsync(stoppingToken).ConfigureAwait(false);
                    _state.SetMqttConfiguration(
                        snapshot.Options.EnableMqtt,
                        snapshot.Options.MqttHost,
                        snapshot.Options.MqttPort,
                        snapshot.Options.MqttUsername,
                        snapshot.Options.MqttPassword,
                        snapshot.Options.MqttStatusTopic,
                        snapshot.Options.MqttPublishTopic,
                        snapshot.Options.MqttMeterTopic,
                        snapshot.Options.MqttCurrentTopic);
                    _state.SetConfigurationRequirement(snapshot.RequiresConfiguration, snapshot.ConfigurationFileMissing);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }

            try
            {
                await RunWithConfigurationAsync(snapshot, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Simulator encountered an error; retrying shortly");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }

    private async Task RunWithConfigurationAsync(SimulatorConfigurationSnapshot snapshot, CancellationToken stoppingToken)
    {
        var options = snapshot.Options;
        if (string.IsNullOrWhiteSpace(options.Url) || string.IsNullOrWhiteSpace(options.Identity) || string.IsNullOrWhiteSpace(options.AuthKey))
        {
            _state.SetConfigurationRequirement(true, snapshot.ConfigurationFileMissing);
            _state.SetSelectedCharger(null);
            _state.SetSerialNumbers("0", "0");
            return;
        }

        if (!_catalog.TryGet(options.ChargerId, out var charger))
        {
            _state.SetConfigurationRequirement(true, snapshot.ConfigurationFileMissing);
            _state.SetSelectedCharger(null);
            _state.SetSerialNumbers("0", "0");
            return;
        }

        var logPath = Path.Combine(_storageOptions.DataDirectory, options.LogFile ?? "log.txt");
        using var logger = new DualLogger(logPath);
        var identity = new ChargerIdentity(
            charger.Id,
            charger.Make,
            charger.Model,
            charger.ChargePointModel,
            charger.ChargePointVendor,
            charger.ChargePointSerialNumber,
            charger.ChargeBoxSerialNumber,
            charger.FirmwareVersion,
            charger.MeterType,
            charger.MeterSerialNumber,
            charger.Iccid,
            charger.Imsi);

        var client = new ChargerClient(
            new Uri(options.Url),
            options.Identity,
            options.AuthKey,
            identity,
            options.ChargePointSerialNumber ?? "0",
            options.ChargeBoxSerialNumber ?? "0",
            logger,
            _storageOptions.DataDirectory,
            supportSoC: options.SupportSoC,
            enableHeartbeat: options.SupportHeartbeat);

        _coordinator.Attach(client, logger);
        _state.SetVehicleState(client.VehicleState);
        _state.SetConfigurationSnapshot(client.ConfigurationSnapshot);
        _state.SetBootConfiguration(client.BootConfiguration);
        _state.SetMetrics(client.LatestSample);
        _state.SetConnectionDetails(options.Url, options.Identity, options.AuthKey);
        _state.SetLoggingEnabled(logger.IsFileLoggingEnabled);
        _state.SetSelectedCharger(charger.Id);
        _state.SetSerialNumbers(options.ChargePointSerialNumber ?? "0", options.ChargeBoxSerialNumber ?? "0");
        _state.SetConfigurationRequirement(false, snapshot.ConfigurationFileMissing);

        logger.MessageLogged += OnMessageLogged;
        client.VehicleStateChanged += OnVehicleStateChanged;
        client.ConfigurationChanged += OnConfigurationChanged;
        client.MeterSampled += OnMeterSampled;

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        using var subscription = _configurationProvider.Subscribe(() => linkedCts.Cancel());

        try
        {
            await client.RunAsync(linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (linkedCts.IsCancellationRequested)
        {
            // configuration changed or cancellation requested
        }
        finally
        {
            logger.MessageLogged -= OnMessageLogged;
            client.VehicleStateChanged -= OnVehicleStateChanged;
            client.ConfigurationChanged -= OnConfigurationChanged;
            client.MeterSampled -= OnMeterSampled;
        }
    }

    private void OnMessageLogged(string message)
    {
        _state.AddLog(message);
        _ = _hubContext.Clients.All.LogAdded(message);
    }

    private void OnVehicleStateChanged(string state)
    {
        _state.SetVehicleState(state);
        _ = _hubContext.Clients.All.VehicleStateChanged(state);
    }

    private void OnConfigurationChanged(string key, string value)
    {
        _state.UpdateConfiguration(key, value);
        _ = _hubContext.Clients.All.ConfigurationUpdated(key, value);
    }

    private void OnMeterSampled(MeterSample sample)
    {
        _state.SetMetrics(sample);
        _ = _hubContext.Clients.All.MeterValuesUpdated(new MeterSnapshotDto(sample.EnergyWh, sample.PowerKw, sample.CurrentAmps, sample.StateOfCharge >= 0 ? sample.StateOfCharge : null, sample.Timestamp));
    }
}
