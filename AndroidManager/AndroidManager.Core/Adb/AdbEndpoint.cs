using System.Net;
using System.Net.Sockets;

namespace AndroidManager.Core.Adb;

/// <summary>A device: validated IPv4 host + TCP port (Wi-Fi), or a bare adb serial (USB).</summary>
public sealed record AdbEndpoint
{
    public const int DefaultPort = 5555;

    /// <summary>Empty for USB devices.</summary>
    public string Host { get; }
    /// <summary>0 for USB devices.</summary>
    public int Port { get; }
    public string Serial { get; }
    public bool IsWireless => Port > 0;

    private AdbEndpoint(string host, int port, string serial) => (Host, Port, Serial) = (host, port, serial);

    private AdbEndpoint(string host, int port) : this(host, port, $"{host}:{port}") { }

    /// <summary>USB (or any non-network) device identified only by its adb serial.</summary>
    public static AdbEndpoint ForSerial(string serial)
    {
        if (string.IsNullOrWhiteSpace(serial))
            throw new ArgumentException("Seriale mancante.", nameof(serial));
        return new("", 0, serial.Trim());
    }

    /// <summary>Rebuilds an endpoint from a serial adb reported: "ip:port" becomes a Wi-Fi endpoint, anything else USB.</summary>
    public static AdbEndpoint FromSerial(string serial) =>
        serial.Contains(':') && TryParse(serial, out var wifi, out _) ? wifi! : ForSerial(serial);

    public static bool TryCreate(string? host, int port, out AdbEndpoint? endpoint, out string? error)
    {
        endpoint = null;
        host = host?.Trim();
        if (string.IsNullOrEmpty(host))
        {
            error = "Indirizzo IP mancante.";
            return false;
        }
        // IPAddress.TryParse accepts "1" or "1.2" shorthand: require 4 dotted octets.
        if (host.Count(c => c == '.') != 3
            || !IPAddress.TryParse(host, out var ip)
            || ip.AddressFamily != AddressFamily.InterNetwork)
        {
            error = $"Indirizzo IPv4 non valido: {host}";
            return false;
        }
        if (port is < 1 or > 65535)
        {
            error = $"Porta non valida: {port} (1-65535).";
            return false;
        }
        endpoint = new AdbEndpoint(ip.ToString(), port);
        error = null;
        return true;
    }

    /// <summary>Parses "ip" or "ip:port"; port defaults to 5555.</summary>
    public static bool TryParse(string? text, out AdbEndpoint? endpoint, out string? error)
    {
        endpoint = null;
        var parts = (text ?? "").Split(':');
        if (parts.Length > 2)
        {
            error = "Formato non valido. Usa ip o ip:porta.";
            return false;
        }
        var port = DefaultPort;
        if (parts.Length == 2 && !int.TryParse(parts[1].Trim(), out port))
        {
            error = $"Porta non valida: {parts[1].Trim()}";
            return false;
        }
        return TryCreate(parts[0], port, out endpoint, out error);
    }
}
