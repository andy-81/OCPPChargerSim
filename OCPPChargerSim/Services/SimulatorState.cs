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
    private OcppSimulator.ExternalMeterValues? _externalMeterValues;

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

    /// <summary>
    /// Stores the latest meter values received from an external source (e.g. Home Assistant).
    /// Pass null to clear and revert to simulated values.
    /// </summary>
    public void SetExternalMeterValues(OcppSimulator.ExternalMeterValues? values)
    {
        lock (_sync)
        {
            _externalMeterValues = values;
        }
    }

    /// <summary>
    /// Returns a snapshot of the latest external meter values, or null if none have been received.
    /// </summary>
    public OcppSimulator.ExternalMeterValues? GetExternalMeterValues()
    {
        lock (_sync)
        {
            if (_externalMeterValues is null)
            {
                return null;
            }

            // Return a copy so callers can't mutate shared state
            return new OcppSimulator.ExternalMeterValues
            {
                EnergyWhImport = _externalMeterValues.EnergyWhImport,
                PowerKwImport = _externalMeterValues.PowerKwImport,
                FrequencyHz = _externalMeterValues.FrequencyHz,
                PowerKwOffered = _externalMeterValues.PowerKwOffered,
                CurrentAmpsImport = _externalMeterValues.CurrentAmpsImport,
                CurrentAmpsOffered = _externalMeterValues.CurrentAmpsOffered,
                StateOfChargePercent = _externalMeterValues.StateOfChargePercent,
            };
        }
    }
}
