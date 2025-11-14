using System;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;
using OcppSimulator;

namespace OcppWeb.Services;

public sealed class MqttIntegrationService : BackgroundService
{
    private readonly SimulatorCoordinator _coordinator;
    private readonly SimulatorConfigurationProvider _configurationProvider;
    private readonly SimulatorState _state;
    private readonly ILogger<MqttIntegrationService> _logger;
    private readonly Channel<bool> _configurationChanges = Channel.CreateUnbounded<bool>();
    private readonly SemaphoreSlim _connectionLock = new(1, 1);
    private readonly SemaphoreSlim _publishLock = new(1, 1);
    private readonly MqttFactory _mqttFactory = new();
    private readonly JsonSerializerOptions _serializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
    private readonly Func<MqttApplicationMessageReceivedEventArgs, Task> _messageHandler;
    private readonly Func<MqttClientConnectedEventArgs, Task> _connectedHandler;
    private readonly Func<MqttClientDisconnectedEventArgs, Task> _disconnectedHandler;

    private IDisposable? _configurationSubscription;
    private IMqttClient? _client;
    private ChargerClient? _attachedClient;
    private readonly object _clientEventSync = new();
    private MqttSettings? _desiredSettings;
    private MqttSettings? _connectedSettings;
    private CancellationToken _publishToken = CancellationToken.None;
    private bool _isStopping;

    public MqttIntegrationService(
        SimulatorCoordinator coordinator,
        SimulatorConfigurationProvider configurationProvider,
        SimulatorState state,
        ILogger<MqttIntegrationService> logger)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _configurationProvider = configurationProvider ?? throw new ArgumentNullException(nameof(configurationProvider));
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _messageHandler = OnApplicationMessageReceivedAsync;
        _connectedHandler = OnClientConnectedAsync;
        _disconnectedHandler = OnClientDisconnectedAsync;

        _coordinator.ClientAttached += OnClientAttached;
        _configurationSubscription = _configurationProvider.Subscribe(() => _configurationChanges.Writer.TryWrite(true));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        _publishToken = linkedCts.Token;

        await ApplyConfigurationAsync(_configurationProvider.Snapshot.Options, stoppingToken).ConfigureAwait(false);

        var reader = _configurationChanges.Reader;

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await reader.ReadAsync(stoppingToken).ConfigureAwait(false);
                    while (reader.TryRead(out _))
                    {
                        // drain any queued signals
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }

                await ApplyConfigurationAsync(_configurationProvider.Snapshot.Options, stoppingToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _isStopping = true;
            linkedCts.Cancel();
            await _connectionLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await DisconnectAsync().ConfigureAwait(false);
            }
            finally
            {
                _connectionLock.Release();
            }

            _publishToken = CancellationToken.None;
        }
    }

    public override void Dispose()
    {
        base.Dispose();
        _coordinator.ClientAttached -= OnClientAttached;
        _configurationSubscription?.Dispose();
        _configurationSubscription = null;

        lock (_clientEventSync)
        {
            if (_attachedClient is not null)
            {
                _attachedClient.VehicleStateChanged -= OnVehicleStateChanged;
                _attachedClient.MeterSampled -= OnMeterSampled;
                _attachedClient.RemoteCommandIssued -= OnRemoteCommandIssued;
                _attachedClient = null;
            }
        }
    }

    private async Task ApplyConfigurationAsync(SimulatorOptions options, CancellationToken cancellationToken)
    {
        var settings = ExtractSettings(options);
        _desiredSettings = settings;

        try
        {
            await _connectionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        try
        {
            await EnsureConnectionAsync(settings, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to apply MQTT configuration");
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    private async Task EnsureConnectionAsync(MqttSettings settings, CancellationToken cancellationToken)
    {
        if (!settings.Enabled)
        {
            await DisconnectAsync().ConfigureAwait(false);
            return;
        }

        if (_client is not null && _client.IsConnected && _connectedSettings.HasValue && _connectedSettings.Value.Equals(settings))
        {
            return;
        }

        await DisconnectAsync().ConfigureAwait(false);
        await ConnectClientAsync(settings, cancellationToken).ConfigureAwait(false);
    }

    private async Task ConnectClientAsync(MqttSettings settings, CancellationToken cancellationToken)
    {
        if (!settings.Enabled || settings.Host is null || settings.PublishTopic is null)
        {
            return;
        }

        var client = _mqttFactory.CreateMqttClient();
        client.ApplicationMessageReceivedAsync += _messageHandler;
        client.ConnectedAsync += _connectedHandler;
        client.DisconnectedAsync += _disconnectedHandler;

        _client = client;
        _connectedSettings = settings;

        var builder = new MqttClientOptionsBuilder()
            .WithTcpServer(settings.Host, settings.Port);

        if (!string.IsNullOrEmpty(settings.Username))
        {
            builder = builder.WithCredentials(settings.Username, settings.Password ?? string.Empty);
        }

        try
        {
            await client.ConnectAsync(builder.Build(), cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Connected to MQTT broker {Host}:{Port}", settings.Host, settings.Port);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to MQTT broker {Host}:{Port}", settings.Host, settings.Port);

            client.ApplicationMessageReceivedAsync -= _messageHandler;
            client.ConnectedAsync -= _connectedHandler;
            client.DisconnectedAsync -= _disconnectedHandler;
            client.Dispose();
            _client = null;
            _connectedSettings = null;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
                    _configurationChanges.Writer.TryWrite(true);
                }
                catch
                {
                    // ignore
                }
            });
        }
    }

    private async Task DisconnectAsync()
    {
        var client = _client;
        if (client is null)
        {
            _connectedSettings = null;
            return;
        }

        _client = null;

        try
        {
            if (client.IsConnected)
            {
                await client.DisconnectAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error while disconnecting MQTT client");
        }
        finally
        {
            client.ApplicationMessageReceivedAsync -= _messageHandler;
            client.ConnectedAsync -= _connectedHandler;
            client.DisconnectedAsync -= _disconnectedHandler;
            client.Dispose();
        }

        _connectedSettings = null;
    }

    private Task OnClientConnectedAsync(MqttClientConnectedEventArgs _)
    {
        var settings = _connectedSettings;
        var client = _client;
        if (!settings.HasValue || client is null)
        {
            return Task.CompletedTask;
        }

        var topic = settings.Value.StatusTopic;
        if (!string.IsNullOrWhiteSpace(topic))
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await client.SubscribeAsync(
                        new MqttTopicFilterBuilder()
                            .WithTopic(topic)
                            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                            .Build()).ConfigureAwait(false);
                    _logger.LogInformation("Subscribed to MQTT topic {Topic}", topic);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to subscribe to MQTT topic {Topic}", topic);
                }
            });
        }

        _ = PublishStatusSnapshotAsync();
        return Task.CompletedTask;
    }

    private Task OnClientDisconnectedAsync(MqttClientDisconnectedEventArgs args)
    {
        if (_isStopping)
        {
            return Task.CompletedTask;
        }

        var settings = _desiredSettings;
        if (!settings.HasValue || !settings.Value.Enabled)
        {
            return Task.CompletedTask;
        }

        _logger.LogWarning("MQTT client disconnected ({Reason}); scheduling reconnect", args.ReasonString);

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
                if (_isStopping)
                {
                    return;
                }

                await _connectionLock.WaitAsync().ConfigureAwait(false);
                try
                {
                    await EnsureConnectionAsync(settings.Value, CancellationToken.None).ConfigureAwait(false);
                }
                finally
                {
                    _connectionLock.Release();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MQTT reconnect attempt failed");
            }
        });

        return Task.CompletedTask;
    }

    private async Task OnApplicationMessageReceivedAsync(MqttApplicationMessageReceivedEventArgs args)
    {
        var settings = _connectedSettings;
        if (!settings.HasValue || string.IsNullOrWhiteSpace(settings.Value.StatusTopic))
        {
            return;
        }

        if (!string.Equals(args.ApplicationMessage.Topic, settings.Value.StatusTopic, StringComparison.Ordinal))
        {
            return;
        }

        var payloadSegment = args.ApplicationMessage.PayloadSegment;
        if (payloadSegment.IsEmpty)
        {
            return;
        }

        string payload;
        try
        {
            payload = Encoding.UTF8.GetString(payloadSegment);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to decode MQTT payload from topic {Topic}", args.ApplicationMessage.Topic);
            return;
        }

        await HandleInboundStatusAsync(payload).ConfigureAwait(false);
    }

    private async Task HandleInboundStatusAsync(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return;
        }

        string? status = null;
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.String)
            {
                status = root.GetString();
            }
            else if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("status", out var statusProperty) && statusProperty.ValueKind == JsonValueKind.String)
                {
                    status = statusProperty.GetString();
                }
                else if (root.TryGetProperty("state", out var stateProperty) && stateProperty.ValueKind == JsonValueKind.String)
                {
                    status = stateProperty.GetString();
                }
            }
        }
        catch (JsonException)
        {
            status = payload.Trim();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse MQTT status payload: {Payload}", payload);
            return;
        }

        if (string.IsNullOrWhiteSpace(status))
        {
            return;
        }

        try
        {
            await _coordinator.SendManualStatusAsync(status, CancellationToken.None).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Simulator not ready to process MQTT status {Status}", status);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to forward MQTT status {Status}", status);
        }
    }

    private void OnClientAttached(ChargerClient client)
    {
        lock (_clientEventSync)
        {
            if (_attachedClient is not null)
            {
                _attachedClient.VehicleStateChanged -= OnVehicleStateChanged;
                _attachedClient.MeterSampled -= OnMeterSampled;
                _attachedClient.RemoteCommandIssued -= OnRemoteCommandIssued;
            }

            _attachedClient = client;
            client.VehicleStateChanged += OnVehicleStateChanged;
            client.MeterSampled += OnMeterSampled;
            client.RemoteCommandIssued += OnRemoteCommandIssued;
        }

        _ = PublishStatusSnapshotAsync();
    }

    private void OnVehicleStateChanged(string state)
    {
        var sample = _state.LatestSample;
        var token = _publishToken;
        _ = PublishStatusUpdateAsync(state, sample, token);
    }

    private void OnMeterSampled(MeterSample sample)
    {
        var token = _publishToken;
        _ = PublishStatusUpdateAsync(_state.VehicleState, sample, token);
    }

    private void OnRemoteCommandIssued(string command)
    {
        var token = _publishToken;
        _ = PublishRemoteCommandAsync(command, token);
    }

    private Task PublishStatusSnapshotAsync()
    {
        var sample = _state.LatestSample;
        var state = _state.VehicleState;
        return PublishStatusUpdateAsync(state, sample, _publishToken);
    }

    private Task PublishStatusUpdateAsync(string state, MeterSample sample, CancellationToken cancellationToken)
    {
        var settings = _connectedSettings;
        if (!settings.HasValue || string.IsNullOrWhiteSpace(settings.Value.PublishTopic))
        {
            return Task.CompletedTask;
        }

        var payload = new
        {
            type = "status",
            state,
            metrics = new
            {
                energyWh = sample.EnergyWh,
                powerKw = sample.PowerKw,
                currentAmps = sample.CurrentAmps,
                stateOfCharge = sample.StateOfCharge >= 0 ? sample.StateOfCharge : (double?)null,
                timestamp = sample.Timestamp,
            },
            timestamp = DateTimeOffset.UtcNow,
        };

        return PublishMessageAsync(settings.Value.PublishTopic!, payload, cancellationToken);
    }

    private Task PublishRemoteCommandAsync(string command, CancellationToken cancellationToken)
    {
        var settings = _connectedSettings;
        if (!settings.HasValue || string.IsNullOrWhiteSpace(settings.Value.PublishTopic))
        {
            return Task.CompletedTask;
        }

        var payload = new
        {
            type = "command",
            command,
            state = _state.VehicleState,
            timestamp = DateTimeOffset.UtcNow,
        };

        return PublishMessageAsync(settings.Value.PublishTopic!, payload, cancellationToken);
    }

    private async Task PublishMessageAsync(string topic, object payload, CancellationToken cancellationToken)
    {
        var client = _client;
        if (client is null || !client.IsConnected)
        {
            return;
        }

        string json;
        try
        {
            json = JsonSerializer.Serialize(payload, _serializerOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to serialize MQTT payload for topic {Topic}", topic);
            return;
        }

        var message = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(json)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .Build();

        try
        {
            await _publishLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await client.PublishAsync(message, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _publishLock.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // ignore cancellation
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish MQTT message to topic {Topic}", topic);
        }
    }

    private static MqttSettings ExtractSettings(SimulatorOptions options)
    {
        var host = string.IsNullOrWhiteSpace(options.MqttHost) ? null : options.MqttHost.Trim();
        var publishTopic = string.IsNullOrWhiteSpace(options.MqttPublishTopic) ? null : options.MqttPublishTopic.Trim();
        var statusTopic = string.IsNullOrWhiteSpace(options.MqttStatusTopic) ? null : options.MqttStatusTopic.Trim();
        var username = string.IsNullOrWhiteSpace(options.MqttUsername) ? null : options.MqttUsername.Trim();
        var password = options.MqttPassword;

        var port = 1883;
        if (options.MqttPort.HasValue && options.MqttPort.Value > 0 && options.MqttPort.Value <= 65535)
        {
            port = options.MqttPort.Value;
        }

        var enabled = options.EnableMqtt && host is not null && publishTopic is not null && statusTopic is not null;

        return new MqttSettings(enabled, host, port, username, password, statusTopic, publishTopic);
    }

    private readonly record struct MqttSettings(
        bool Enabled,
        string? Host,
        int Port,
        string? Username,
        string? Password,
        string? StatusTopic,
        string? PublishTopic);
}
