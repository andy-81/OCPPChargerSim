using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OcppSimulator;

public sealed class ChargerClient
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = null,
    };

    private static readonly IReadOnlyDictionary<string, string> DefaultConfiguration = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["MeterValueSampleInterval"] = "15",
        ["MeterValuesSampledData"] = "Energy.Active.Import.Register,Power.Active.Import",
        ["MeterValuesAlignedData"] = "",
        ["ClockAlignedDataInterval"] = "0",
        ["minSoC"] = "20",
        ["maxSoC"] = "100",
        ["chargingALimitConn1"] = "32",
        ["HeartbeatInterval"] = "60",
        ["AuthorizeRemoteTxRequests"] = "true",
        ["AuthEnabled"] = "true",
        ["AllowOfflineTxForUnknownId"] = "true",
        ["AuthDisabledIdTag"] = DefaultIdTag,
    };

    private const string BootNotificationId = "1027082133";
    private const string DefaultIdTag = "NoAuthorization";
    private const double BatteryCapacityWh = 60_000;
    private const double MaxCurrentAmps = 32.0;
    private const double NominalVoltage = 230.0;
    private const double MaxPowerKw = MaxCurrentAmps * NominalVoltage / 1000.0;
    private const double TargetCurrentAmps = 20.0;
    private const double CurrentJitterAmps = 4.0;
    private const double FixedStateOfCharge = 21.0;

    private readonly Uri _url;
    private readonly string _identity;
    private readonly string _authKey;
    private readonly ChargerIdentity _chargerIdentity;
    private readonly string _chargePointSerialOverride;
    private readonly string _chargeBoxSerialOverride;
    private readonly DualLogger _logger;
    private readonly int _connectorId;
    private readonly Vehicle _vehicle = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pendingCalls = new();
    private readonly Dictionary<string, string> _configuration = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _bootPayload;
    private readonly HashSet<string> _localAuthorizationList = new(StringComparer.OrdinalIgnoreCase);
    private int _localListVersion;
    private readonly Random _random = new();
    private string? _activeIdTag;
    private int? _activeTransactionId;
    private int _meterStartValue;
    private int _meterValue;
    private double _meterAccumulatorWh;
    private DateTimeOffset _lastMeterSampleTimestamp = DateTimeOffset.MinValue;
    private CancellationTokenSource? _meterLoopCts;
    private CancellationTokenSource? _manualSimulationCts;
    private CancellationTokenSource? _heartbeatLoopCts;
    private readonly object _manualLock = new();
    private readonly object _meterUpdateLock = new();
    private bool _manualSimulationActive;
    private readonly bool _supportSoC;
    private readonly bool _heartbeatEnabled;
    private readonly string _meterStateFilePath;
    private CancellationToken _runCancellationToken;
    private bool _isRunning;

    private enum StateInitiator
    {
        System,
        User,
        Remote,
    }

    private StateInitiator _lastStateInitiator = StateInitiator.System;

    private ClientWebSocket? _webSocket;

    public event Action<string>? VehicleStateChanged;
    public event Action<string>? ConnectorStatusChanged;
    public event Action<string, string>? ConfigurationChanged;
    public event Action<MeterSample>? MeterSampled;
    public event Action<string>? RemoteCommandIssued;

    public string VehicleState => _vehicle.State;
    public string ConnectorStatus { get; private set; } = "Initializing";

    public IReadOnlyDictionary<string, string> ConfigurationSnapshot
    {
        get
        {
            lock (_configuration)
            {
                return new Dictionary<string, string>(_configuration);
            }
        }
    }

    public IReadOnlyDictionary<string, string> BootConfiguration => new Dictionary<string, string>(_bootPayload);

    public MeterSample LatestSample { get; private set; } = MeterSample.Empty;

    public void SetLocalConfiguration(string key, string value)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("Key must be provided", nameof(key));
        }

        lock (_configuration)
        {
            _configuration[key] = value;
        }

        if (_heartbeatEnabled && string.Equals(key, "HeartbeatInterval", StringComparison.OrdinalIgnoreCase) && _isRunning)
        {
            StartHeartbeatLoop(_runCancellationToken);
        }

        ConfigurationChanged?.Invoke(key, value);
    }

    public void ApplyExternalMeterSample(double? energyWh, double? currentAmps)
    {
        if (!energyWh.HasValue && !currentAmps.HasValue)
        {
            return;
        }

        double? sanitizedEnergy = null;
        if (energyWh.HasValue && !double.IsNaN(energyWh.Value) && !double.IsInfinity(energyWh.Value) && energyWh.Value >= 0)
        {
            sanitizedEnergy = energyWh.Value;
        }

        double? sanitizedCurrent = null;
        if (currentAmps.HasValue && !double.IsNaN(currentAmps.Value) && !double.IsInfinity(currentAmps.Value) && currentAmps.Value >= 0)
        {
            sanitizedCurrent = currentAmps.Value;
        }

        if (!sanitizedEnergy.HasValue && !sanitizedCurrent.HasValue)
        {
            return;
        }

        MeterSample sample;
        lock (_meterUpdateLock)
        {
            var baseline = LatestSample;
            var timestamp = DateTimeOffset.UtcNow;

            var energyValue = sanitizedEnergy ?? baseline.EnergyWh;
            if (sanitizedEnergy.HasValue)
            {
                _meterAccumulatorWh = sanitizedEnergy.Value;
                _meterValue = (int)Math.Round(_meterAccumulatorWh, MidpointRounding.AwayFromZero);
                if (_activeTransactionId.HasValue)
                {
                    _meterStartValue = Math.Min(_meterStartValue, _meterValue);
                }
                else
                {
                    _meterStartValue = _meterValue;
                }

                PersistMeterAccumulator();
            }

            var currentValue = sanitizedCurrent ?? baseline.CurrentAmps;
            var powerValue = sanitizedCurrent.HasValue
                ? Math.Min(sanitizedCurrent.Value * NominalVoltage / 1000.0, MaxPowerKw)
                : baseline.PowerKw;

            var socValue = baseline.StateOfCharge;
            if (socValue < 0 && _supportSoC)
            {
                socValue = FixedStateOfCharge;
            }

            _lastMeterSampleTimestamp = timestamp;
            sample = new MeterSample(energyValue, powerValue, currentValue, socValue, timestamp);
        }

        PublishSample(sample);
    }

    public Task SendManualStatusAsync(string status, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            throw new ArgumentException("Status must be provided", nameof(status));
        }

        if (string.Equals(status, "Charging", StringComparison.OrdinalIgnoreCase))
        {
            var idTag = _activeIdTag ?? GetFallbackIdTag();
            if (!_activeTransactionId.HasValue)
            {
                StopMeterValueLoop();
                return BeginChargingSequenceAsync(idTag, default, null, StateInitiator.User, cancellationToken);
            }

            TransitionVehicleState("Charging", StateInitiator.User);
            return SendStatusNotificationAsync("Charging", cancellationToken, TimeSpan.FromSeconds(5), waitForResponse: false);
        }

        if (string.Equals(status, "Available", StringComparison.OrdinalIgnoreCase))
        {
            return HandleManualAvailableAsync(cancellationToken);
        }

        UpdateLocalVehicleState(status, StateInitiator.User);
        return SendStatusNotificationAsync(status, cancellationToken);
    }

    private async Task HandleManualAvailableAsync(CancellationToken cancellationToken)
    {
        if (_activeTransactionId.HasValue)
        {
            TransitionVehicleState("Finishing", StateInitiator.User);
            await SendStatusNotificationAsync("Finishing", cancellationToken, TimeSpan.FromSeconds(5), waitForResponse: false).ConfigureAwait(false);
            StopMeterValueLoop();
            await SendStopTransactionAsync("Local", cancellationToken).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
        }

        TransitionVehicleState("Available", StateInitiator.User);
        await SendStatusNotificationAsync("Available", cancellationToken).ConfigureAwait(false);
    }

    public Task StartManualSimulationAsync(CancellationToken cancellationToken)
    {
        CancellationTokenSource? cts = null;
        double startingEnergy;

        lock (_manualLock)
        {
            if (_manualSimulationCts is not null || _activeTransactionId.HasValue)
            {
                return Task.CompletedTask;
            }

            _manualSimulationCts = new CancellationTokenSource();
            cts = _manualSimulationCts;
            startingEnergy = LatestSample.EnergyWh;
            _manualSimulationActive = true;
        }

        var token = cts!.Token;
        _ = Task.Run(async () =>
        {
            var energyWh = startingEnergy;
            try
            {
                PublishSample(new MeterSample(energyWh, 0, 0, _supportSoC ? FixedStateOfCharge : -1, DateTimeOffset.UtcNow));
                while (!token.IsCancellationRequested)
                {
                    const double intervalSeconds = 2.0;
                    await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), token).ConfigureAwait(false);
                    if (token.IsCancellationRequested)
                    {
                        break;
                    }

                    var jitter = (_random.NextDouble() - 0.5) * CurrentJitterAmps;
                    var currentAmps = Math.Clamp(TargetCurrentAmps + jitter, 10.0, MaxCurrentAmps);
                    if (GetConfiguredCurrentLimit() is double configuredLimit)
                    {
                        currentAmps = Math.Min(currentAmps, configuredLimit);
                    }

                    var powerKw = Math.Min(currentAmps * NominalVoltage / 1000.0, MaxPowerKw);
                    var increment = powerKw * intervalSeconds / 3.6;
                    energyWh += increment;
                    PublishSample(new MeterSample(energyWh, powerKw, currentAmps, _supportSoC ? FixedStateOfCharge : -1, DateTimeOffset.UtcNow));
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
        }, CancellationToken.None);

        return Task.CompletedTask;
    }

    public Task StopManualSimulationAsync()
    {
        CancellationTokenSource? cts;
        lock (_manualLock)
        {
            cts = _manualSimulationCts;
            _manualSimulationCts = null;
            _manualSimulationActive = false;
        }

        if (cts is not null)
        {
            try
            {
                cts.Cancel();
            }
            finally
            {
                cts.Dispose();
            }
        }

        return Task.CompletedTask;
    }

    public ChargerClient(Uri url, string identity, string authKey, ChargerIdentity chargerIdentity, string chargePointSerialOverride, string chargeBoxSerialOverride, DualLogger logger, string storageDirectory, int connectorId = 1, bool supportSoC = false, bool enableHeartbeat = true)
    {
        _url = url;
        _identity = identity;
        _authKey = authKey;
        _chargerIdentity = chargerIdentity;
        _chargePointSerialOverride = string.IsNullOrWhiteSpace(chargePointSerialOverride) ? "0" : chargePointSerialOverride;
        _chargeBoxSerialOverride = string.IsNullOrWhiteSpace(chargeBoxSerialOverride) ? "0" : chargeBoxSerialOverride;
        _logger = logger;
        _connectorId = connectorId;
        _supportSoC = supportSoC;
        _heartbeatEnabled = enableHeartbeat;
        _meterStateFilePath = Path.Combine(storageDirectory, "meter_state.txt");
        _bootPayload = CreateBootPayload(chargerIdentity, _chargePointSerialOverride, _chargeBoxSerialOverride);

        foreach (var kvp in DefaultConfiguration)
        {
            _configuration[kvp.Key] = kvp.Value;
        }

        _configuration["ChargerId"] = chargerIdentity.Id;
        _configuration["ChargePointSerialNumber"] = _chargePointSerialOverride;
        _configuration["ChargeBoxSerialNumber"] = _chargeBoxSerialOverride;

        foreach (var kvp in _bootPayload)
        {
            _configuration[$"Boot.{kvp.Key}"] = kvp.Value;
        }


        _meterAccumulatorWh = LoadMeterAccumulator();
        _meterValue = (int)Math.Round(_meterAccumulatorWh, MidpointRounding.AwayFromZero);
        _meterStartValue = _meterValue;
        _lastMeterSampleTimestamp = DateTimeOffset.UtcNow;
        LatestSample = new MeterSample(_meterAccumulatorWh, 0, 0, _supportSoC ? FixedStateOfCharge : -1, _lastMeterSampleTimestamp);
    }

    private static Dictionary<string, string> CreateBootPayload(ChargerIdentity identity, string chargePointSerialOverride, string chargeBoxSerialOverride)
    {
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["chargePointSerialNumber"] = string.IsNullOrWhiteSpace(chargePointSerialOverride) ? identity.ChargePointSerialNumber : chargePointSerialOverride,
            ["chargePointVendor"] = identity.ChargePointVendor,
            ["meterType"] = identity.MeterType,
            ["meterSerialNumber"] = identity.MeterSerialNumber ?? string.Empty,
            ["chargePointModel"] = identity.ChargePointModel,
            ["iccid"] = identity.Iccid ?? string.Empty,
            ["chargeBoxSerialNumber"] = string.IsNullOrWhiteSpace(chargeBoxSerialOverride) ? identity.ChargeBoxSerialNumber : chargeBoxSerialOverride,
            ["firmwareVersion"] = identity.FirmwareVersion,
            ["imsi"] = identity.Imsi ?? string.Empty,
        };
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _runCancellationToken = cancellationToken;
        var attempt = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            ClientWebSocket? client = null;
            try
            {
                client = CreateWebSocketClient();
                _logger.Info($"Connecting to {_url}");
                await client.ConnectAsync(_url, cancellationToken).ConfigureAwait(false);

                _webSocket = client;
                _isRunning = true;
                attempt = 0;

                var receiveTask = ReceiveLoopAsync(cancellationToken);

                var bootAccepted = await SendBootNotificationAsync(BootNotificationId, sendStatusOnSuccess: true, cancellationToken).ConfigureAwait(false);
                if (!bootAccepted)
                {
                    throw new InvalidOperationException("BootNotification was rejected; retrying connection.");
                }

                await EnsureRemoteStartConfigurationAsync(cancellationToken).ConfigureAwait(false);
                StartHeartbeatLoop(cancellationToken);

                await receiveTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is WebSocketException or InvalidOperationException or IOException)
            {
                _logger.Error(ex, "Connection terminated");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Unhandled error in simulator loop");
            }
            finally
            {
                StopMeterValueLoop();
                StopHeartbeatLoop();

                var socket = _webSocket;
                _webSocket = null;

                if (socket is not null)
                {
                    try
                    {
                        socket.Dispose();
                    }
                    catch
                    {
                    }
                }

                if (client is not null && ReferenceEquals(socket, client))
                {
                    client = null;
                }

                client?.Dispose();
                _isRunning = false;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            attempt++;
            var delaySeconds = Math.Min(30, Math.Pow(2, attempt));
            var delay = TimeSpan.FromSeconds(delaySeconds);
            _logger.Info($"Disconnected. Reconnecting in {delay.TotalSeconds:F0} seconds (attempt {attempt}).");

            try
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private ClientWebSocket CreateWebSocketClient()
    {
        var client = new ClientWebSocket
        {
            Options =
            {
                KeepAliveInterval = TimeSpan.FromSeconds(30),
            },
        };

        client.Options.AddSubProtocol("ocpp1.6");
        client.Options.SetRequestHeader("Authorization", $"Basic {BuildAuthToken()}");
        client.Options.SetRequestHeader("chargePointIdentity", _identity);
        return client;
    }

    private async Task<bool> SendBootNotificationAsync(string uniqueId, bool sendStatusOnSuccess, CancellationToken cancellationToken)
    {
        var tcs = RegisterCall(uniqueId);
        await SendCallAsync(uniqueId, "BootNotification", _bootPayload, cancellationToken);

        try
        {
            var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(35), cancellationToken));
            if (completed != tcs.Task)
            {
                _logger.Error("BootNotification response timed out");
                return false;
            }

            var response = await tcs.Task.ConfigureAwait(false);
            if (TryGetString(response, "status", out var status) && string.Equals(status, "Accepted", StringComparison.OrdinalIgnoreCase))
            {
                if (sendStatusOnSuccess)
                {
                    var reportedState = string.IsNullOrWhiteSpace(_vehicle.State) ? "Available" : _vehicle.State;
                    await SendStatusNotificationAsync(reportedState, cancellationToken, TimeSpan.FromSeconds(5), waitForResponse: false).ConfigureAwait(false);
                    await SendBootMeterValuesAsync(cancellationToken).ConfigureAwait(false);

                    if (_activeTransactionId.HasValue)
                    {
                        StartMeterValueLoop(cancellationToken);
                        await SendMeterValuesAsync(cancellationToken).ConfigureAwait(false);
                    }
                }
                return true;
            }

            _logger.Error($"BootNotification rejected with status: {response.ToString()}");
            return false;
        }
        finally
        {
            _pendingCalls.TryRemove(uniqueId, out _);
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        if (_webSocket is null)
        {
            return;
        }

        var buffer = new byte[4096];

        while (!cancellationToken.IsCancellationRequested)
        {
            WebSocketReceiveResult result;
            using var ms = new MemoryStream();

            do
            {
                result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken).ConfigureAwait(false);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", cancellationToken).ConfigureAwait(false);
                    return;
                }

                ms.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            var raw = Encoding.UTF8.GetString(ms.ToArray());
            _logger.Info($"RECV {raw}");

            await HandleMessageAsync(raw, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task HandleMessageAsync(string raw, CancellationToken cancellationToken)
    {
        try
        {
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
            {
                _logger.Error($"Unexpected message structure: {raw}");
                return;
            }

            var messageType = root[0].GetInt32();

            switch (messageType)
            {
                case 2:
                    await HandleCallAsync(root, cancellationToken).ConfigureAwait(false);
                    break;
                case 3:
                    HandleCallResult(root);
                    break;
                case 4:
                    HandleCallError(root);
                    break;
                default:
                    _logger.Error($"Unknown message type: {messageType}");
                    break;
            }
        }
        catch (JsonException)
        {
            _logger.Error($"Invalid JSON from server: {raw}");
        }
    }

    private async Task HandleCallAsync(JsonElement message, CancellationToken cancellationToken)
    {
        if (message.GetArrayLength() < 4)
        {
            _logger.Error($"Malformed CALL message: {message}");
            return;
        }

        var uniqueId = message[1].GetString() ?? string.Empty;
        var action = message[2].GetString() ?? string.Empty;
        var payload = message[3].Clone();

        if (string.Equals(action, "RemoteStartTransaction", StringComparison.OrdinalIgnoreCase))
        {
            await HandleRemoteStartAsync(uniqueId, payload, cancellationToken).ConfigureAwait(false);
        }
        else if (string.Equals(action, "ChangeConfiguration", StringComparison.OrdinalIgnoreCase))
        {
            await HandleChangeConfigurationAsync(uniqueId, payload, cancellationToken).ConfigureAwait(false);
        }
        else if (string.Equals(action, "SendLocalList", StringComparison.OrdinalIgnoreCase))
        {
            await HandleSendLocalListAsync(uniqueId, payload, cancellationToken).ConfigureAwait(false);
        }
        else if (string.Equals(action, "TriggerMessage", StringComparison.OrdinalIgnoreCase))
        {
            await HandleTriggerMessageAsync(uniqueId, payload, cancellationToken).ConfigureAwait(false);
        }
        else if (string.Equals(action, "RemoteStopTransaction", StringComparison.OrdinalIgnoreCase))
        {
            await HandleRemoteStopTransactionAsync(uniqueId, payload, cancellationToken).ConfigureAwait(false);
        }
        else if (string.Equals(action, "GetConfiguration", StringComparison.OrdinalIgnoreCase))
        {
            await HandleGetConfigurationAsync(uniqueId, payload, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await SendCallErrorAsync(uniqueId, "NotSupported", "Action not implemented", new Dictionary<string, object>(), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task HandleRemoteStartAsync(string uniqueId, JsonElement payload, CancellationToken cancellationToken)
    {
        await StopManualSimulationAsync().ConfigureAwait(false);

        var idTag = TryGetString(payload, "idTag", out var providedIdTag) && !string.IsNullOrEmpty(providedIdTag)
            ? providedIdTag
            : GetFallbackIdTag();

        if (!IsVehiclePluggedIn())
        {
            _logger.Info("RemoteStartTransaction rejected: vehicle not connected.");
            await SendCallResultAsync(uniqueId, new Dictionary<string, object>
            {
                ["status"] = "Rejected",
            }, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (_activeTransactionId.HasValue)
        {
            _logger.Info("RemoteStartTransaction acknowledged: transaction already active.");
            await SendCallResultAsync(uniqueId, new Dictionary<string, object>
            {
                ["status"] = "Accepted",
            }, cancellationToken).ConfigureAwait(false);
            await SendStatusNotificationAsync(_vehicle.State, cancellationToken, TimeSpan.FromSeconds(5), waitForResponse: false).ConfigureAwait(false);
            return;
        }

        _activeIdTag = idTag;
        StopMeterValueLoop();
        NotifyRemoteCommand("RemoteStart");
        await BeginChargingSequenceAsync(idTag, payload, uniqueId, StateInitiator.Remote, cancellationToken).ConfigureAwait(false);
    }

private async Task BeginChargingSequenceAsync(string idTag, JsonElement payload, string? callUniqueId, StateInitiator initiator, CancellationToken cancellationToken)
{
    _activeIdTag = idTag;
    TransitionVehicleState("Preparing", initiator);
    _logger.Info($"Vehicle state updated to: {_vehicle.State}");

    if (!string.IsNullOrEmpty(callUniqueId))
    {
        await SendCallResultAsync(callUniqueId!, new Dictionary<string, object>
        {
            ["status"] = "Accepted",
        }, cancellationToken).ConfigureAwait(false);
    }

    await SendStatusNotificationAsync("Preparing", cancellationToken, TimeSpan.FromSeconds(5), waitForResponse: false).ConfigureAwait(false);

    var started = await SendStartTransactionAsync(idTag, payload, cancellationToken).ConfigureAwait(false);
    if (!started)
    {
        TransitionVehicleState("SuspendedEV", initiator);
        _activeIdTag = null;
        await SendStatusNotificationAsync("SuspendedEV", cancellationToken, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        return;
    }

    try
    {
        await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
        return;
    }

    TransitionVehicleState("Charging", initiator);
    _logger.Info($"Vehicle state updated to: {_vehicle.State}");

    await SendStatusNotificationAsync("Charging", cancellationToken, TimeSpan.FromSeconds(5), waitForResponse: false).ConfigureAwait(false);

    StartMeterValueLoop(cancellationToken);
    await SendMeterValuesAsync(cancellationToken).ConfigureAwait(false);
}

    private async Task<bool> SendStartTransactionAsync(string idTag, JsonElement payload, CancellationToken cancellationToken)
    {
        var uniqueId = GenerateUniqueId();
        var tcs = RegisterCall(uniqueId);

        lock (_meterUpdateLock)
        {
            _meterStartValue = (int)Math.Round(_meterAccumulatorWh, MidpointRounding.AwayFromZero);
            _meterValue = _meterStartValue;
            _lastMeterSampleTimestamp = DateTimeOffset.UtcNow;
            PersistMeterAccumulator();
        }

        var request = new Dictionary<string, object>
        {
            ["connectorId"] = _connectorId,
            ["idTag"] = idTag,
            ["meterStart"] = _meterStartValue,
            ["timestamp"] = DateTimeOffset.UtcNow.ToString("O"),
        };

        if (payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("reservationId", out var reservationElement) && reservationElement.ValueKind == JsonValueKind.Number && reservationElement.TryGetInt32(out var reservationId))
        {
            request["reservationId"] = reservationId;
        }

        await SendCallAsync(uniqueId, "StartTransaction", request, cancellationToken).ConfigureAwait(false);

        try
        {
            var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(35), cancellationToken));
            if (completed != tcs.Task)
            {
                _logger.Error("StartTransaction response timed out");
                return false;
            }

            var response = await tcs.Task.ConfigureAwait(false);

            if (!TryGetInt32(response, "transactionId", out var transactionId))
            {
                _logger.Error($"StartTransaction missing transactionId: {response.ToString()}");
                return false;
            }

            if (response.ValueKind == JsonValueKind.Object && response.TryGetProperty("idTagInfo", out var idTagInfo) && TryGetString(idTagInfo, "status", out var status) && !string.Equals(status, "Accepted", StringComparison.OrdinalIgnoreCase))
            {
                _logger.Error($"StartTransaction rejected with status: {status}");
                return false;
            }

            _activeTransactionId = transactionId;
            _logger.Info($"Transaction {transactionId} started for idTag {idTag}");
            return true;
        }
        finally
        {
            _pendingCalls.TryRemove(uniqueId, out _);
        }
    }

    private void StartMeterValueLoop(CancellationToken parentToken)
    {
        StopMeterValueLoop();

        if (!_activeTransactionId.HasValue)
        {
            return;
        }

        var interval = GetMeterSampleInterval();
        if (interval <= TimeSpan.Zero)
        {
            return;
        }

        var linked = CancellationTokenSource.CreateLinkedTokenSource(parentToken);
        _meterLoopCts = linked;
        var loopToken = linked.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                while (!loopToken.IsCancellationRequested && _activeTransactionId.HasValue)
                {
                    await Task.Delay(interval, loopToken).ConfigureAwait(false);
                    if (loopToken.IsCancellationRequested || !_activeTransactionId.HasValue)
                    {
                        break;
                    }

                    await SendMeterValuesAsync(loopToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (loopToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "MeterValues loop failed");
            }
        }, CancellationToken.None);
    }

    private void StopMeterValueLoop()
    {
        if (_meterLoopCts is null)
        {
            return;
        }

        try
        {
            _meterLoopCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            _meterLoopCts.Dispose();
            _meterLoopCts = null;
        }
    }

    private void StartHeartbeatLoop(CancellationToken parentToken)
    {
        StopHeartbeatLoop();

        if (!_heartbeatEnabled)
        {
            return;
        }

        var interval = GetHeartbeatInterval();
        if (interval <= TimeSpan.Zero)
        {
            return;
        }

        var linked = CancellationTokenSource.CreateLinkedTokenSource(parentToken);
        _heartbeatLoopCts = linked;
        var loopToken = linked.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await SendHeartbeatAsync(loopToken).ConfigureAwait(false);
                while (!loopToken.IsCancellationRequested)
                {
                    await Task.Delay(interval, loopToken).ConfigureAwait(false);
                    if (loopToken.IsCancellationRequested)
                    {
                        break;
                    }

                    await SendHeartbeatAsync(loopToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (loopToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Heartbeat loop failed");
            }
        }, CancellationToken.None);
    }

    private void StopHeartbeatLoop()
    {
        if (_heartbeatLoopCts is null)
        {
            return;
        }

        try
        {
            _heartbeatLoopCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            _heartbeatLoopCts.Dispose();
            _heartbeatLoopCts = null;
        }
    }

    private async Task SendMeterValuesAsync(CancellationToken cancellationToken)
    {
        if (!_activeTransactionId.HasValue)
        {
            return;
        }

        var uniqueId = GenerateUniqueId();
        var tcs = RegisterCall(uniqueId);

        var now = DateTimeOffset.UtcNow;
        double elapsedSeconds;
        if (_lastMeterSampleTimestamp == DateTimeOffset.MinValue)
        {
            elapsedSeconds = GetMeterSampleInterval().TotalSeconds;
            if (elapsedSeconds <= 0)
            {
                elapsedSeconds = 1.0;
            }
        }
        else
        {
            elapsedSeconds = (now - _lastMeterSampleTimestamp).TotalSeconds;
            if (elapsedSeconds <= 0)
            {
                elapsedSeconds = 1.0;
            }
        }

        double currentAmps;
        double powerKwValue;
        double energyWhValue;
        double meterAccumulatorSnapshot;
        var socValue = FixedStateOfCharge;

        lock (_meterUpdateLock)
        {
            _lastMeterSampleTimestamp = now;

            var jitter = (_random.NextDouble() - 0.5) * CurrentJitterAmps;
            currentAmps = Math.Clamp(TargetCurrentAmps + jitter, 10.0, MaxCurrentAmps);
            if (GetConfiguredCurrentLimit() is double configuredLimit)
            {
                currentAmps = Math.Min(currentAmps, configuredLimit);
            }

            powerKwValue = Math.Min(currentAmps * NominalVoltage / 1000.0, MaxPowerKw);

            var incrementWh = powerKwValue * elapsedSeconds / 3.6;
            _meterAccumulatorWh += incrementWh;
            _meterValue = (int)Math.Round(_meterAccumulatorWh);
            meterAccumulatorSnapshot = _meterAccumulatorWh;
            energyWhValue = Math.Round(_meterAccumulatorWh, 0, MidpointRounding.AwayFromZero);
        }
        var energy = energyWhValue.ToString(CultureInfo.InvariantCulture);
        var power = powerKwValue.ToString("0.0", CultureInfo.InvariantCulture);
        string? soc = null;
        if (_supportSoC)
        {
            soc = socValue.ToString("0.0", CultureInfo.InvariantCulture);
        }

        var payload = new Dictionary<string, object>
        {
            ["connectorId"] = _connectorId,
            ["transactionId"] = _activeTransactionId.Value,
            ["meterValue"] = new object[]
            {
                new Dictionary<string, object>
                {
                    ["timestamp"] = DateTimeOffset.UtcNow.ToString("O"),
                    ["sampledValue"] = new object[]
                    {
                        new Dictionary<string, object>
                        {
                            ["value"] = energy,
                            ["measurand"] = "Energy.Active.Import.Register",
                            ["unit"] = "Wh",
                            ["context"] = "Sample.Periodic",
                        },
                        new Dictionary<string, object>
                        {
                            ["value"] = power,
                            ["measurand"] = "Power.Active.Import",
                            ["unit"] = "kW",
                            ["context"] = "Sample.Periodic",
                        },
                    },
                },
            },
        };

        if (_supportSoC &&
            payload.TryGetValue("meterValue", out var meterValueObj) &&
            meterValueObj is object[] meterArray &&
            meterArray.Length > 0 &&
            meterArray[0] is Dictionary<string, object> meterEntryCandidate &&
            meterEntryCandidate.TryGetValue("sampledValue", out var sampledObj) &&
            sampledObj is object[] sampledValues)
        {
            var meterEntry = meterEntryCandidate;
            var extended = new object[sampledValues.Length + 1];
            Array.Copy(sampledValues, extended, sampledValues.Length);
            extended[^1] = new Dictionary<string, object>
            {
                ["value"] = soc!,
                ["measurand"] = "SoC",
                ["unit"] = "Percent",
                ["context"] = "Sample.Periodic",
            };
            meterEntry["sampledValue"] = extended;
        }

        await SendCallAsync(uniqueId, "MeterValues", payload, cancellationToken).ConfigureAwait(false);

        try
        {
            var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(35), cancellationToken));
            if (completed != tcs.Task)
            {
                _logger.Info("MeterValues response timed out");
            }
        }
        finally
        {
            _pendingCalls.TryRemove(uniqueId, out _);
        }

        PublishSample(new MeterSample(meterAccumulatorSnapshot, powerKwValue, currentAmps, _supportSoC ? socValue : -1, DateTimeOffset.UtcNow));
        PersistMeterAccumulator();
    }

    public async Task SendHeartbeatAsync(CancellationToken cancellationToken, bool ignoreDisabled = false)
    {
        if (!_heartbeatEnabled && !ignoreDisabled)
        {
            return;
        }

        var uniqueId = GenerateUniqueId();
        var tcs = RegisterCall(uniqueId);

        await SendCallAsync(uniqueId, "Heartbeat", new Dictionary<string, object>(), cancellationToken).ConfigureAwait(false);

        try
        {
            var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(35), cancellationToken));
            if (completed != tcs.Task)
            {
                _logger.Info("Heartbeat response timed out");
            }
        }
        finally
        {
            _pendingCalls.TryRemove(uniqueId, out _);
        }
    }

    public async Task CloseAsync(CancellationToken cancellationToken = default)
    {
        var socket = _webSocket;
        if (socket is null)
        {
            return;
        }

        if (socket.State == WebSocketState.Open || socket.State == WebSocketState.CloseReceived)
        {
            try
            {
                _logger.Info("Closing WebSocket on user request");
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Client requested disconnect", cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to close WebSocket");
            }
        }
    }

    private async Task HandleChangeConfigurationAsync(string uniqueId, JsonElement payload, CancellationToken cancellationToken)
    {
        if (!TryGetString(payload, "key", out var key))
        {
            await SendCallResultAsync(uniqueId, new Dictionary<string, object>
            {
                ["status"] = "Rejected",
            }, cancellationToken).ConfigureAwait(false);
            return;
        }

        var value = TryGetString(payload, "value", out var provided) ? provided : string.Empty;
        _configuration[key] = value;
        _logger.Info($"Configuration updated: {key}={value}");

        if (_activeTransactionId.HasValue && string.Equals(key, "MeterValueSampleInterval", StringComparison.OrdinalIgnoreCase))
        {
            StartMeterValueLoop(cancellationToken);
        }

        if (_heartbeatEnabled && string.Equals(key, "HeartbeatInterval", StringComparison.OrdinalIgnoreCase))
        {
            StartHeartbeatLoop(cancellationToken);
        }

        await SendCallResultAsync(uniqueId, new Dictionary<string, object>
        {
            ["status"] = "Accepted",
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsureRemoteStartConfigurationAsync(CancellationToken cancellationToken)
    {
        var desired = new (string Key, string Value)[]
        {
            ("AuthorizeRemoteTxRequests", "true"),
            ("AuthEnabled", "true"),
            ("AllowOfflineTxForUnknownId", "true"),
        };

        foreach (var (key, value) in desired)
        {
            string? current;
            lock (_configuration)
            {
                _configuration.TryGetValue(key, out current);
            }

            if (string.Equals(current, value, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            await SendChangeConfigurationRequestAsync(key, value, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task SendChangeConfigurationRequestAsync(string key, string value, CancellationToken cancellationToken)
    {
        var uniqueId = GenerateUniqueId();
        var tcs = RegisterCall(uniqueId);

        await SendCallAsync(uniqueId, "ChangeConfiguration", new Dictionary<string, object>
        {
            ["key"] = key,
            ["value"] = value,
        }, cancellationToken).ConfigureAwait(false);

        try
        {
            var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(35), cancellationToken)).ConfigureAwait(false);
            if (completed != tcs.Task)
            {
                _logger.Info($"ChangeConfiguration for {key} timed out");
                return;
            }

            JsonElement response;
            try
            {
                response = await tcs.Task.ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                _logger.Info($"ChangeConfiguration for {key} rejected by central system: {ex.Message}");
                return;
            }
            if (TryGetString(response, "status", out var status) && string.Equals(status, "Accepted", StringComparison.OrdinalIgnoreCase))
            {
                SetLocalConfiguration(key, value);
                _logger.Info($"Configuration ensured: {key}={value}");
            }
            else
            {
                _logger.Info($"ChangeConfiguration for {key} responded with: {response.ToString()}");
            }
        }
        finally
        {
            _pendingCalls.TryRemove(uniqueId, out _);
        }
    }

    private async Task HandleRemoteStopTransactionAsync(string uniqueId, JsonElement payload, CancellationToken cancellationToken)
    {
        if (!_activeTransactionId.HasValue)
        {
            if (_manualSimulationActive)
            {
                await StopManualSimulationAsync().ConfigureAwait(false);
                await SendCallResultAsync(uniqueId, new Dictionary<string, object>
                {
                    ["status"] = "Accepted",
                }, cancellationToken).ConfigureAwait(false);

                NotifyRemoteCommand("RemoteStop");
                TransitionVehicleState("Finishing", StateInitiator.Remote);
                await SendStatusNotificationAsync("Finishing", cancellationToken, TimeSpan.FromSeconds(5), waitForResponse: false).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
                TransitionVehicleState("SuspendedEV", StateInitiator.Remote);
                await SendStatusNotificationAsync("SuspendedEV", cancellationToken, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                return;
            }

            await SendCallResultAsync(uniqueId, new Dictionary<string, object>
            {
                ["status"] = "Rejected",
            }, cancellationToken).ConfigureAwait(false);
            return;
        }

        var requestedId = _activeTransactionId.Value;
        if (payload.TryGetProperty("transactionId", out var transactionElement) && transactionElement.ValueKind == JsonValueKind.Number && transactionElement.TryGetInt32(out var providedTransactionId))
        {
            requestedId = providedTransactionId;
        }

        if (_activeTransactionId.Value != requestedId)
        {
            await SendCallResultAsync(uniqueId, new Dictionary<string, object>
            {
                ["status"] = "Rejected",
            }, cancellationToken).ConfigureAwait(false);
            return;
        }

        await SendCallResultAsync(uniqueId, new Dictionary<string, object>
        {
            ["status"] = "Accepted",
        }, cancellationToken).ConfigureAwait(false);

        NotifyRemoteCommand("RemoteStop");
        TransitionVehicleState("Finishing", StateInitiator.Remote);
        await SendStatusNotificationAsync("Finishing", cancellationToken, TimeSpan.FromSeconds(5), waitForResponse: false).ConfigureAwait(false);

        StopMeterValueLoop();

        var stopTask = SendStopTransactionAsync("Remote", cancellationToken);

        await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);

        TransitionVehicleState("SuspendedEV", StateInitiator.Remote);
        _logger.Info($"Vehicle state updated to: {_vehicle.State}");

        await SendStatusNotificationAsync("SuspendedEV", cancellationToken, TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        await stopTask.ConfigureAwait(false);

    }

    private async Task<bool> SendStopTransactionAsync(string reason, CancellationToken cancellationToken)
    {
        await StopManualSimulationAsync().ConfigureAwait(false);

        if (!_activeTransactionId.HasValue)
        {
            return false;
        }

        var uniqueId = GenerateUniqueId();
        var tcs = RegisterCall(uniqueId);

        _meterValue = (int)Math.Round(_meterAccumulatorWh, MidpointRounding.AwayFromZero);

        var payload = new Dictionary<string, object>
        {
            ["transactionId"] = _activeTransactionId.Value,
            ["meterStop"] = _meterValue,
            ["timestamp"] = DateTimeOffset.UtcNow.ToString("O"),
            ["reason"] = reason,
        };

        if (!string.IsNullOrEmpty(_activeIdTag))
        {
            payload["idTag"] = _activeIdTag!;
        }

        await SendCallAsync(uniqueId, "StopTransaction", payload, cancellationToken).ConfigureAwait(false);

        try
        {
            var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(35), cancellationToken));
            if (completed != tcs.Task)
            {
                _logger.Info("StopTransaction response timed out");
                return false;
            }

            var response = await tcs.Task.ConfigureAwait(false);

            if (response.ValueKind == JsonValueKind.Object && response.TryGetProperty("idTagInfo", out var idTagInfo) && TryGetString(idTagInfo, "status", out var status) && !string.Equals(status, "Accepted", StringComparison.OrdinalIgnoreCase))
            {
                _logger.Info($"StopTransaction completed with status: {status}");
            }

            return true;
        }
        finally
        {
            _pendingCalls.TryRemove(uniqueId, out _);
            PublishSample(new MeterSample(_meterAccumulatorWh, 0, 0, _supportSoC ? FixedStateOfCharge : -1, DateTimeOffset.UtcNow));
            PersistMeterAccumulator();
            _activeTransactionId = null;
            _activeIdTag = null;
            _meterStartValue = (int)Math.Round(_meterAccumulatorWh, MidpointRounding.AwayFromZero);
            _meterValue = _meterStartValue;
            _lastMeterSampleTimestamp = DateTimeOffset.UtcNow;
        }
    }

    private void NotifyRemoteCommand(string command)
    {
        var handlers = RemoteCommandIssued;
        if (handlers is null)
        {
            return;
        }

        try
        {
            handlers.Invoke(command);
        }
        catch (Exception ex)
        {
            _logger.Error($"Remote command notification failed: {ex}");
        }
    }

    private async Task HandleSendLocalListAsync(string uniqueId, JsonElement payload, CancellationToken cancellationToken)
    {
        var updateType = TryGetString(payload, "updateType", out var type) ? type : "Full";
        var version = payload.TryGetProperty("listVersion", out var versionElement) && versionElement.TryGetInt32(out var parsedVersion)
            ? parsedVersion
            : _localListVersion + 1;

        int listCount;
        lock (_localAuthorizationList)
        {
            if (string.Equals(updateType, "Full", StringComparison.OrdinalIgnoreCase))
            {
                _localAuthorizationList.Clear();
            }

            if (payload.TryGetProperty("localAuthorizationList", out var listElement) && listElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in listElement.EnumerateArray())
                {
                    if (TryGetString(entry, "idTag", out var idTag) && !string.IsNullOrEmpty(idTag))
                    {
                        _localAuthorizationList.Add(idTag);
                    }
                }
            }

            _localListVersion = version;
            listCount = _localAuthorizationList.Count;
        }

        _logger.Info($"Local authorization list updated to version {_localListVersion} with {listCount} entries");

        await SendCallResultAsync(uniqueId, new Dictionary<string, object>
        {
            ["status"] = "Accepted",
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleTriggerMessageAsync(string uniqueId, JsonElement payload, CancellationToken cancellationToken)
    {
        var requestedMessage = TryGetString(payload, "requestedMessage", out var requested) ? requested : string.Empty;

        if (string.Equals(requestedMessage, "BootNotification", StringComparison.OrdinalIgnoreCase))
        {
            await SendCallResultAsync(uniqueId, new Dictionary<string, object>
            {
                ["status"] = "Accepted",
            }, cancellationToken).ConfigureAwait(false);

            _ = Task.Run(async () =>
            {
                try
                {
                    await SendBootNotificationAsync(GenerateUniqueId(), sendStatusOnSuccess: true, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Failed to send triggered BootNotification");
                }
            }, CancellationToken.None);
            return;
        }

        if (string.Equals(requestedMessage, "StatusNotification", StringComparison.OrdinalIgnoreCase))
        {
            await SendCallResultAsync(uniqueId, new Dictionary<string, object>
            {
                ["status"] = "Accepted",
            }, cancellationToken).ConfigureAwait(false);

            var currentStatus = _vehicle.State;
            var statusToSend = currentStatus switch
            {
                "Charging" or "Preparing" or "SuspendedEV" or "Finishing" or "Unavailable" => currentStatus,
                _ => "Available",
            };

            await SendStatusNotificationAsync(statusToSend, cancellationToken).ConfigureAwait(false);
            return;
        }

        await SendCallResultAsync(uniqueId, new Dictionary<string, object>
        {
            ["status"] = "NotImplemented",
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleGetConfigurationAsync(string uniqueId, JsonElement payload, CancellationToken cancellationToken)
    {
        var configurationEntries = new List<Dictionary<string, object>>();
        var unknownKeys = new List<string>();

        if (payload.TryGetProperty("key", out var keysElement) && keysElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var keyElement in keysElement.EnumerateArray())
            {
                var key = keyElement.GetString();
                if (string.IsNullOrEmpty(key))
                {
                    continue;
                }

                string? value;
                lock (_configuration)
                {
                    _configuration.TryGetValue(key, out value);
                }

                if (value is not null)
                {
                    configurationEntries.Add(new Dictionary<string, object>
                    {
                        ["key"] = key,
                        ["value"] = value,
                        ["readonly"] = false,
                    });
                }
                else
                {
                    unknownKeys.Add(key);
                }
            }
        }
        else
        {
            foreach (var pair in _configuration)
            {
                configurationEntries.Add(new Dictionary<string, object>
                {
                    ["key"] = pair.Key,
                    ["value"] = pair.Value,
                    ["readonly"] = false,
                });
            }
        }

        await SendCallResultAsync(uniqueId, new Dictionary<string, object>
        {
            ["configurationKey"] = configurationEntries,
            ["unknownKey"] = unknownKeys,
        }, cancellationToken).ConfigureAwait(false);
    }

    private void HandleCallResult(JsonElement message)
    {
        if (message.GetArrayLength() < 3)
        {
            _logger.Error($"Malformed CALLRESULT message: {message}");
            return;
        }

        var uniqueId = message[1].GetString();
        if (string.IsNullOrEmpty(uniqueId))
        {
            return;
        }

        if (_pendingCalls.TryRemove(uniqueId, out var tcs) && !tcs.Task.IsCompleted)
        {
            tcs.TrySetResult(message[2].Clone());
        }
    }

    private void HandleCallError(JsonElement message)
    {
        if (message.GetArrayLength() < 5)
        {
            _logger.Error($"Malformed CALLERROR message: {message}");
            return;
        }

        var uniqueId = message[1].GetString();
        if (string.IsNullOrEmpty(uniqueId))
        {
            return;
        }

        var errorCode = message[2].GetString() ?? string.Empty;
        var errorDescription = message[3].GetString() ?? string.Empty;

        if (_pendingCalls.TryRemove(uniqueId, out var tcs) && !tcs.Task.IsCompleted)
        {
            tcs.TrySetException(new InvalidOperationException($"{errorCode}: {errorDescription}"));
        }
    }

    private async Task SendStatusNotificationAsync(string status, CancellationToken cancellationToken, TimeSpan? responseTimeout = null, bool waitForResponse = true)
    {
        var uniqueId = GenerateUniqueId();
        var tcs = RegisterCall(uniqueId);

        ConnectorStatus = status;
        ConnectorStatusChanged?.Invoke(status);

        await SendCallAsync(uniqueId, "StatusNotification", new Dictionary<string, object>
        {
            ["connectorId"] = _connectorId,
            ["errorCode"] = "NoError",
            ["status"] = status,
            ["timestamp"] = DateTimeOffset.UtcNow.ToString("O"),
        }, cancellationToken).ConfigureAwait(false);

        var timeout = responseTimeout ?? TimeSpan.FromSeconds(35);

        if (!waitForResponse)
        {
            _ = MonitorPendingCallAsync(uniqueId, tcs, timeout, cancellationToken, "StatusNotification");
            return;
        }

        await MonitorPendingCallAsync(uniqueId, tcs, timeout, cancellationToken, "StatusNotification").ConfigureAwait(false);
    }

    private async Task MonitorPendingCallAsync(string uniqueId, TaskCompletionSource<JsonElement> tcs, TimeSpan timeout, CancellationToken cancellationToken, string actionName)
    {
        try
        {
            var completed = await Task.WhenAny(tcs.Task, Task.Delay(timeout, cancellationToken));
            if (completed != tcs.Task)
            {
                _logger.Info($"{actionName} response timed out");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            _pendingCalls.TryRemove(uniqueId, out _);
        }
    }

    private async Task SendCallAsync(string uniqueId, string action, object payload, CancellationToken cancellationToken)
    {
        var message = new object[] { 2, uniqueId, action, payload };
        await SendRawAsync(message, cancellationToken).ConfigureAwait(false);
    }

    private async Task SendCallResultAsync(string uniqueId, object payload, CancellationToken cancellationToken)
    {
        var message = new object[] { 3, uniqueId, payload };
        await SendRawAsync(message, cancellationToken).ConfigureAwait(false);
    }

    private async Task SendCallErrorAsync(string uniqueId, string errorCode, string errorDescription, object details, CancellationToken cancellationToken)
    {
        var message = new object[] { 4, uniqueId, errorCode, errorDescription, details };
        await SendRawAsync(message, cancellationToken).ConfigureAwait(false);
    }

    private async Task SendRawAsync(object message, CancellationToken cancellationToken)
    {
        if (_webSocket is null)
        {
            throw new InvalidOperationException("WebSocket is not connected");
        }

        var raw = JsonSerializer.Serialize(message, SerializerOptions);
        _logger.Info($"SEND {raw}");

        var bytes = Encoding.UTF8.GetBytes(raw);
        await _webSocket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
    }

    private TaskCompletionSource<JsonElement> RegisterCall(string uniqueId)
    {
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingCalls[uniqueId] = tcs;
        return tcs;
    }

    private void TransitionVehicleState(string status, StateInitiator initiator)
    {
        _vehicle.SetStatus(status);
        _lastStateInitiator = initiator;
        RaiseVehicleStateChanged();
    }

    private void RaiseVehicleStateChanged()
    {
        var suffix = _lastStateInitiator switch
        {
            StateInitiator.User => " (user)",
            StateInitiator.Remote => " (remote)",
            _ => string.Empty,
        };

        VehicleStateChanged?.Invoke(_vehicle.State + suffix);
    }

    private void PublishSample(MeterSample sample)
    {
        LatestSample = sample;
        MeterSampled?.Invoke(sample);
    }

private void UpdateLocalVehicleState(string status, StateInitiator initiator)
{
    var normalized = status.Trim();

    switch (normalized)
    {
        case "Available":
            StopMeterValueLoop();
            TransitionVehicleState("Available", initiator);
            break;
        case "Charging":
            TransitionVehicleState("Charging", initiator);
            break;
        case "Preparing":
            TransitionVehicleState("Preparing", initiator);
            break;
        case "SuspendedEV":
            StopMeterValueLoop();
            TransitionVehicleState("SuspendedEV", initiator);
            break;
        case "Finishing":
            TransitionVehicleState("Finishing", initiator);
            break;
        case "Unavailable":
            StopMeterValueLoop();
            TransitionVehicleState("Unavailable", initiator);
            break;
        default:
            TransitionVehicleState(normalized, initiator);
            break;
    }
}

    private string GetFallbackIdTag()
    {
        lock (_localAuthorizationList)
        {
            foreach (var tag in _localAuthorizationList)
            {
                if (!string.IsNullOrEmpty(tag))
                {
                    return tag;
                }
            }
        }

        return DefaultIdTag;
    }

    private double? GetConfiguredCurrentLimit()
    {
        lock (_configuration)
        {
            if (_configuration.TryGetValue("chargingALimitConn1", out var value) &&
                double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var limit) &&
                limit > 0)
            {
                return Math.Min(limit, MaxCurrentAmps);
            }
        }

        return null;
    }

    private bool IsVehiclePluggedIn()
    {
        var state = _vehicle.State;
        return !string.Equals(state, "Available", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(state, "Unavailable", StringComparison.OrdinalIgnoreCase);
    }

    private TimeSpan GetMeterSampleInterval()
    {
        string? configured;
        lock (_configuration)
        {
            _configuration.TryGetValue("MeterValueSampleInterval", out configured);
        }

        if (configured is not null && int.TryParse(configured, out var seconds) && seconds > 0)
        {
            return TimeSpan.FromSeconds(seconds);
        }

        return TimeSpan.FromSeconds(15);
    }

    private TimeSpan GetHeartbeatInterval()
    {
        string? configured;
        lock (_configuration)
        {
            _configuration.TryGetValue("HeartbeatInterval", out configured);
        }

        if (configured is not null && int.TryParse(configured, out var seconds) && seconds > 0)
        {
            return TimeSpan.FromSeconds(seconds);
        }

        return TimeSpan.FromSeconds(60);
    }

    private static bool TryGetString(JsonElement element, string propertyName, out string value)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String)
        {
            value = property.GetString() ?? string.Empty;
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static bool TryGetInt32(JsonElement element, string propertyName, out int value)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var parsed))
        {
            value = parsed;
            return true;
        }

        value = 0;
        return false;
    }

    private async Task SendBootMeterValuesAsync(CancellationToken cancellationToken)
    {
        var uniqueId = GenerateUniqueId();
        var tcs = RegisterCall(uniqueId);

        var sample = LatestSample;
        var energyValue = sample.EnergyWh > 0 ? sample.EnergyWh : _meterAccumulatorWh;
        var powerValue = sample.PowerKw;
        var currentValue = sample.CurrentAmps;

        var sampledValues = new List<Dictionary<string, object>>
        {
            new()
            {
                ["value"] = energyValue.ToString("0", CultureInfo.InvariantCulture),
                ["measurand"] = "Energy.Active.Import.Register",
                ["unit"] = "Wh",
                ["context"] = "Sample.Clock",
            },
        };

        sampledValues.Add(new Dictionary<string, object>
        {
            ["value"] = powerValue.ToString("0.0", CultureInfo.InvariantCulture),
            ["measurand"] = "Power.Active.Import",
            ["unit"] = "kW",
            ["context"] = "Sample.Clock",
        });

        sampledValues.Add(new Dictionary<string, object>
        {
            ["value"] = currentValue.ToString("0.0", CultureInfo.InvariantCulture),
            ["measurand"] = "Current.Import",
            ["unit"] = "A",
            ["context"] = "Sample.Clock",
        });

        if (_supportSoC)
        {
            sampledValues.Add(new Dictionary<string, object>
            {
                ["value"] = FixedStateOfCharge.ToString("0.0", CultureInfo.InvariantCulture),
                ["measurand"] = "SoC",
                ["unit"] = "Percent",
                ["context"] = "Sample.Clock",
            });
        }

        var payload = new Dictionary<string, object>
        {
            ["connectorId"] = _connectorId,
            ["meterValue"] = new object[]
            {
                new Dictionary<string, object>
                {
                    ["timestamp"] = DateTimeOffset.UtcNow.ToString("O"),
                    ["sampledValue"] = sampledValues.ToArray(),
                },
            },
        };

        if (_activeTransactionId.HasValue)
        {
            payload["transactionId"] = _activeTransactionId.Value;
        }

        await SendCallAsync(uniqueId, "MeterValues", payload, cancellationToken).ConfigureAwait(false);

        try
        {
            var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(35), cancellationToken));
            if (completed != tcs.Task)
            {
                _logger.Info("Boot MeterValues response timed out");
            }
        }
        finally
        {
            _pendingCalls.TryRemove(uniqueId, out _);
        }

        PublishSample(new MeterSample(energyValue, powerValue, currentValue, _supportSoC ? FixedStateOfCharge : -1, DateTimeOffset.UtcNow));
    }

    private static string GenerateUniqueId()
    {
        return DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff") + Guid.NewGuid().ToString("N")[..6];
    }

    private string BuildAuthToken()
    {
        var credentials = Encoding.UTF8.GetBytes($"{_identity}:{_authKey}");
        return Convert.ToBase64String(credentials);
    }

    private double LoadMeterAccumulator()
    {
        try
        {
            if (File.Exists(_meterStateFilePath))
            {
                var text = File.ReadAllText(_meterStateFilePath).Trim();
                if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value >= 0)
                {
                    return value;
                }
            }
        }
        catch
        {
        }

        return 0d;
    }

    private void PersistMeterAccumulator()
    {
        try
        {
            File.WriteAllText(_meterStateFilePath, _meterAccumulatorWh.ToString("0.###", CultureInfo.InvariantCulture));
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to persist meter accumulator");
        }
    }
}
