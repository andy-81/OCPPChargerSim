using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OcppSimulator;

namespace OcppWeb.Services;

public sealed class SimulatorCoordinator
{
    private readonly SimulatorState _state;
    private readonly ILogger<SimulatorCoordinator> _logger;
    private readonly object _lock = new();
    private ChargerClient? _client;
    private DualLogger? _dualLogger;

    public event Action<ChargerClient>? ClientAttached;

    public SimulatorCoordinator(SimulatorState state, ILogger<SimulatorCoordinator> logger)
    {
        _state = state;
        _logger = logger;
    }

    public SimulatorState State => _state;

    public void Attach(ChargerClient client, DualLogger logger)
    {
        if (client is null)
        {
            throw new ArgumentNullException(nameof(client));
        }

        if (logger is null)
        {
            throw new ArgumentNullException(nameof(logger));
        }

        lock (_lock)
        {
            _client = client;
            _dualLogger = logger;
        }

        ClientAttached?.Invoke(client);
    }

    public ChargerClient? GetClient()
    {
        lock (_lock)
        {
            return _client;
        }
    }

    public DualLogger? GetLogger()
    {
        lock (_lock)
        {
            return _dualLogger;
        }
    }

    public async Task SendManualStatusAsync(string status, CancellationToken cancellationToken)
    {
        try
        {
            var client = EnsureClient();
            await client.SendManualStatusAsync(status, cancellationToken).ConfigureAwait(false);

            if (string.Equals(status, "Charging", StringComparison.OrdinalIgnoreCase))
            {
                await client.StartManualSimulationAsync(cancellationToken).ConfigureAwait(false);
            }
            else if (string.Equals(status, "Available", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(status, "Unavailable", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(status, "SuspendedEV", StringComparison.OrdinalIgnoreCase))
            {
                await client.StopManualSimulationAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send manual status '{Status}'", status);
            throw;
        }
    }

    public void SetLocalConfiguration(string key, string value)
    {
        try
        {
            var client = EnsureClient();
            client.SetLocalConfiguration(key, value);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to set local configuration {Key}", key);
            throw;
        }
    }

    public void UpdateMeterReading(double energyWh)
    {
        try
        {
            var client = EnsureClient();
            client.ApplyExternalMeterSample(energyWh, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to apply external meter reading {EnergyWh}", energyWh);
            throw;
        }
    }

    public void UpdateChargeCurrent(double currentAmps)
    {
        try
        {
            var client = EnsureClient();
            client.ApplyExternalMeterSample(null, currentAmps);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to apply external charge current {Current}", currentAmps);
            throw;
        }
    }

    public async Task SendHeartbeatAsync(CancellationToken cancellationToken)
    {
        try
        {
            var client = EnsureClient();
            await client.SendHeartbeatAsync(cancellationToken, ignoreDisabled: true).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send heartbeat");
            throw;
        }
    }

    public async Task CloseConnectionAsync(CancellationToken cancellationToken)
    {
        try
        {
            var client = EnsureClient();
            await client.CloseAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to close WebSocket connection");
            throw;
        }
    }

    public void SetLoggingEnabled(bool enabled)
    {
        try
        {
            var logger = EnsureLogger();
            logger.SetFileLoggingEnabled(enabled);
            _state.SetLoggingEnabled(enabled);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update logging state");
            throw;
        }
    }

    private ChargerClient EnsureClient()
    {
        var client = GetClient();
        if (client is null)
        {
            throw new InvalidOperationException("Simulator client is not initialized yet.");
        }

        return client;
    }

    private DualLogger EnsureLogger()
    {
        var logger = GetLogger();
        if (logger is null)
        {
            throw new InvalidOperationException("Logger is not initialized yet.");
        }

        return logger;
    }
}
