namespace RockwellTagReader;

/// <summary>
/// Configuration for a <see cref="RockwellPlcClient"/>. All properties are
/// init-only — build once, pass to the constructor, treat as immutable.
/// </summary>
public sealed class RockwellPlcClientOptions
{
    /// <summary>PLC host name or IP address (required).</summary>
    public string Host { get; init; } = "";

    /// <summary>EtherNet/IP TCP port. Defaults to 44818 (the ODVA-assigned port).</summary>
    public int Port { get; init; } = 44818;

    /// <summary>
    /// CIP route path as a comma-separated byte sequence (e.g. <c>"1,0"</c> for
    /// backplane 1, slot 0 on a ControlLogix chassis). Empty string means no
    /// route (talk directly to the gateway).
    /// </summary>
    public string RoutePath { get; init; } = "1,0";

    /// <summary>How long <see cref="RockwellPlcClient.EnsureConnectedAsync"/> waits
    /// for the TCP connect + RegisterSession round-trip. Default 5s.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Per-request CIP timeout. Default 2s.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>How many times the client will reconnect+retry on a transient
    /// failure (IO/timeout/EIP 0x0064) before propagating the exception. Default 1.</summary>
    public int MaxReconnectAttempts { get; init; } = 1;

    /// <summary>Backoff between reconnect attempts. Default 200ms.</summary>
    public TimeSpan ReconnectBackoff { get; init; } = TimeSpan.FromMilliseconds(200);

    internal byte[] GetRoutePathBytes() => LogixTagPath.ParseRoutePath(RoutePath);

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(Host))
            throw new ArgumentException("Host is required", nameof(Host));
        if (Port is <= 0 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(Port), Port, "Port must be 1..65535");
        if (ConnectTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(ConnectTimeout), "Must be > 0");
        if (RequestTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(RequestTimeout), "Must be > 0");
        if (MaxReconnectAttempts < 0)
            throw new ArgumentOutOfRangeException(nameof(MaxReconnectAttempts), "Must be >= 0");
        if (ReconnectBackoff < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(ReconnectBackoff), "Must be >= 0");
    }
}
