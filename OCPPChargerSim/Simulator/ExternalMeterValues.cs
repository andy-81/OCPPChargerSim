namespace OcppSimulator;

/// <summary>
/// Holds the latest meter values pushed in from an external source (e.g. Home Assistant).
/// All fields are optional — null means "use simulated value".
/// </summary>
public sealed class ExternalMeterValues
{
    /// <summary>Energy.Active.Import.Register in Wh</summary>
    public double? EnergyWhImport { get; set; }

    /// <summary>Power.Active.Import in kW</summary>
    public double? PowerKwImport { get; set; }

    /// <summary>Frequency in Hz</summary>
    public double? FrequencyHz { get; set; }

    /// <summary>Power.Offered in kW</summary>
    public double? PowerKwOffered { get; set; }

    /// <summary>Current.Import (actual draw) in A</summary>
    public double? CurrentAmpsImport { get; set; }

    /// <summary>Current.Offered in A</summary>
    public double? CurrentAmpsOffered { get; set; }

    /// <summary>SoC in % (optional — only if your car exposes it)</summary>
    public double? StateOfChargePercent { get; set; }
}
