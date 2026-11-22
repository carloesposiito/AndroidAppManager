using AndroidManager.Core.Adb;

namespace AndroidManager.Core.Devices;

/// <summary>A connected device (USB or Wi-Fi) the user can select.</summary>
public sealed class DeviceSession(AdbEndpoint endpoint, string model, string product)
{
    public AdbEndpoint Endpoint { get; } = endpoint;
    public string Model { get; } = model;
    public string Product { get; } = product;
    public DeviceInfo? Info { get; set; }
}

/// <summary>
/// Single source of truth for the UI: the connected devices, which one is selected (every tab acts on it),
/// and whether an operation is running (every view disables its buttons while Busy).
/// </summary>
public sealed class DeviceHub
{
    private List<DeviceSession> _devices = [];

    public IReadOnlyList<DeviceSession> Devices => _devices;
    public DeviceSession? Selected { get; private set; }
    public bool Busy { get; private set; }

    public event Action? Changed;

    public AdbEndpoint? ActiveEndpoint => Selected?.Endpoint;

    /// <summary>Connected devices other than the selected one: the possible backup targets.</summary>
    public IEnumerable<DeviceSession> Others => _devices.Where(d => d != Selected);

    /// <summary>
    /// Replaces the device list. A device already known keeps its session (and its loaded info).
    /// The selection survives while its device is present; a lone device is selected automatically.
    /// </summary>
    public void SetDevices(IEnumerable<DeviceSession> online)
    {
        var before = (Selected, string.Join('|', _devices.Select(d => d.Endpoint.Serial)));
        var merged = new List<DeviceSession>();
        foreach (var d in online)
        {
            if (merged.Any(m => m.Endpoint.Serial == d.Endpoint.Serial)) continue;
            merged.Add(_devices.FirstOrDefault(o => o.Endpoint.Serial == d.Endpoint.Serial) ?? d);
        }
        _devices = merged;
        if (Selected is not null && !_devices.Contains(Selected)) Selected = null;
        if (Selected is null && _devices.Count == 1) Selected = _devices[0];
        if (before != (Selected, string.Join('|', _devices.Select(d => d.Endpoint.Serial)))) Changed?.Invoke();
    }

    /// <summary>Selects by serial; null clears. Returns false for a serial that is not connected.</summary>
    public bool Select(string? serial)
    {
        var target = serial is null ? null : _devices.FirstOrDefault(d => d.Endpoint.Serial == serial);
        if (serial is not null && target is null) return false;
        if (Selected == target) return true;
        Selected = target;
        Changed?.Invoke();
        return true;
    }

    public void SetBusy(bool busy)
    {
        if (Busy == busy) return;
        Busy = busy;
        Changed?.Invoke();
    }

    /// <summary>Raised after info (battery, storage) arrives for a known device.</summary>
    public void NotifyChanged() => Changed?.Invoke();
}
