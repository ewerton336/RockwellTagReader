namespace RockwellTagReader;

/// <summary>
/// Raised when an EtherNet/IP encapsulation header reports a non-zero status,
/// e.g. 0x0064 (Invalid Session Handle) or 0x0001 (Invalid/Unsupported command).
/// Status is exposed so callers can react without parsing messages.
/// </summary>
public sealed class EipException : InvalidOperationException
{
    /// <summary>EIP encapsulation status code returned by the CLP.</summary>
    public uint Status { get; }

    /// <summary>EIP command associated with the failure (e.g. 0x006F SendRRData).</summary>
    public ushort Command { get; }

    public EipException(ushort command, uint status, string message) : base(message)
    {
        Command = command;
        Status = status;
    }

    /// <summary>True if <see cref="Status"/> is 0x0064 (Invalid Session Handle).</summary>
    public bool IsInvalidSession => Status == 0x0064;
}
