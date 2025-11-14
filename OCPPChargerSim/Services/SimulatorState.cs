using System.Collections.Generic;
using System.Linq;
using OcppSimulator;

namespace OcppWeb.Services;

public sealed class SimulatorState
{
    private const int MaxLogEntries = 500;
    private readonly object _sync = new();
    private readonly LinkedList<string> _logs = new();
    private string _vehicleState = "Initializing";
    private readonly Dictionary<string, string> _configuration = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _bootConfiguration = new(StringComparer.OrdinalIgnoreCase);
    private MeterSample _latestSample = MeterSample.Empty;
    private string _url = "—";
    private string _identity = "—";
    private string _authKey = "—";
    private bool _loggingEnabled = true;
    private bool _requiresConfiguration;
    private bool _configurationFileMissing;
    private string? _selectedChargerId;
    private string _chargePointSerial = "0";
    private string _chargeBoxSerial = "0";
    private bool _mqttEnabled;
    private string _mqttHost = string.Empty;
    private int? _mqttPort;
    private string _mqttUsername = string.Empty;
    private string _mqttPassword = string.Empty;
    private string _mqttStatusTopic = string.Empty;
    private string _mqttPublishTopic = string.Empty;
    private string _mqttMeterTopic = string.Empty;
    private string _mqttCurrentTopic = string.Empty;

    public void AddLog(string message)
    {
        lock (_sync)
        {
            _logs.AddLast(message);
            while (_logs.Count > MaxLogEntries)
            {
                _logs.RemoveFirst();
            }
        }
    }

    public IReadOnlyList<string> GetLogs()
    {
        lock (_sync)
        {
            return _logs.ToList();
        }
    }

    public void SetVehicleState(string state)
    {
        lock (_sync)
        {
            _vehicleState = state;
        }
    }

    public string VehicleState
    {
        get
        {
            lock (_sync)
            {
                return _vehicleState;
            }
        }
    }

    public void UpdateConfiguration(string key, string value)
    {
        lock (_sync)
        {
            _configuration[key] = value;
        }
    }

    public void SetMetrics(MeterSample sample)
    {
        lock (_sync)
        {
            _latestSample = sample;
        }
    }

    public void SetConfigurationRequirement(bool requiresConfiguration, bool configurationFileMissing)
    {
        lock (_sync)
        {
            _requiresConfiguration = requiresConfiguration;
            _configurationFileMissing = configurationFileMissing;
        }
    }

    public void SetConnectionDetails(string url, string identity, string authKey)
    {
        lock (_sync)
        {
            _url = string.IsNullOrWhiteSpace(url) ? "—" : url;
            _identity = string.IsNullOrWhiteSpace(identity) ? "—" : identity;
            _authKey = string.IsNullOrWhiteSpace(authKey) ? "—" : authKey;
        }
    }

    public (string Url, string Identity, string AuthKey) GetConnectionDetails()
    {
        lock (_sync)
        {
            return (_url, _identity, _authKey);
        }
    }

    public void SetLoggingEnabled(bool enabled)
    {
        lock (_sync)
        {
            _loggingEnabled = enabled;
        }
    }

    public bool LoggingEnabled
    {
        get
        {
            lock (_sync)
            {
                return _loggingEnabled;
            }
        }
    }

    public MeterSample LatestSample
    {
        get
        {
            lock (_sync)
            {
                return _latestSample;
            }
        }
    }

    public void SetSelectedCharger(string? chargerId)
    {
        lock (_sync)
        {
            _selectedChargerId = string.IsNullOrWhiteSpace(chargerId) ? null : chargerId;
        }
    }

    public string? SelectedChargerId
    {
        get
        {
            lock (_sync)
            {
                return _selectedChargerId;
            }
        }
    }

    public void SetSerialNumbers(string chargePointSerial, string chargeBoxSerial)
    {
        lock (_sync)
        {
            _chargePointSerial = string.IsNullOrWhiteSpace(chargePointSerial) ? "0" : chargePointSerial;
            _chargeBoxSerial = string.IsNullOrWhiteSpace(chargeBoxSerial) ? "0" : chargeBoxSerial;
        }
    }

    public (string ChargePointSerial, string ChargeBoxSerial) GetSerialNumbers()
    {
        lock (_sync)
        {
            return (_chargePointSerial, _chargeBoxSerial);
        }
    }

    public (bool RequiresConfiguration, bool ConfigurationFileMissing) ConfigurationStatus
    {
        get
        {
            lock (_sync)
            {
                return (_requiresConfiguration, _configurationFileMissing);
            }
        }
    }

    public void SetMqttConfiguration(
        bool enabled,
        string? host,
        int? port,
        string? username,
        string? password,
        string? statusTopic,
        string? publishTopic,
        string? meterTopic,
        string? currentTopic)
    {
        lock (_sync)
        {
            _mqttEnabled = enabled;
            _mqttHost = host ?? string.Empty;
            _mqttPort = port;
            _mqttUsername = username ?? string.Empty;
            _mqttPassword = password ?? string.Empty;
            _mqttStatusTopic = statusTopic ?? string.Empty;
            _mqttPublishTopic = publishTopic ?? string.Empty;
            _mqttMeterTopic = meterTopic ?? string.Empty;
            _mqttCurrentTopic = currentTopic ?? string.Empty;
        }
    }

    public MqttConfigurationSnapshot GetMqttConfiguration()
    {
        lock (_sync)
        {
            return new MqttConfigurationSnapshot(
                _mqttEnabled,
                string.IsNullOrWhiteSpace(_mqttHost) ? null : _mqttHost,
                _mqttPort,
                string.IsNullOrWhiteSpace(_mqttUsername) ? null : _mqttUsername,
                string.IsNullOrEmpty(_mqttPassword) ? null : _mqttPassword,
                string.IsNullOrWhiteSpace(_mqttStatusTopic) ? null : _mqttStatusTopic,
                string.IsNullOrWhiteSpace(_mqttPublishTopic) ? null : _mqttPublishTopic,
                string.IsNullOrWhiteSpace(_mqttMeterTopic) ? null : _mqttMeterTopic,
                string.IsNullOrWhiteSpace(_mqttCurrentTopic) ? null : _mqttCurrentTopic);
        }
    }

    public void SetConfigurationSnapshot(IReadOnlyDictionary<string, string> snapshot)
    {
        lock (_sync)
        {
            _configuration.Clear();
            foreach (var pair in snapshot)
            {
                _configuration[pair.Key] = pair.Value;
            }
        }
    }

    public void SetBootConfiguration(IReadOnlyDictionary<string, string> snapshot)
    {
        lock (_sync)
        {
            _bootConfiguration.Clear();
            foreach (var pair in snapshot)
            {
                _bootConfiguration[pair.Key] = pair.Value;
            }
        }
    }

    public IReadOnlyDictionary<string, string> GetConfiguration()
    {
        lock (_sync)
        {
            return new Dictionary<string, string>(_configuration);
        }
    }

    public IReadOnlyDictionary<string, string> GetBootConfiguration()
    {
        lock (_sync)
        {
            return new Dictionary<string, string>(_bootConfiguration);
        }
    }
}

public readonly record struct MqttConfigurationSnapshot(
    bool Enabled,
    string? Host,
    int? Port,
    string? Username,
    string? Password,
    string? StatusTopic,
    string? PublishTopic,
    string? MeterTopic,
    string? CurrentTopic);
