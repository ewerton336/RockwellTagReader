using System.Buffers.Binary;
using System.Net.Sockets;

namespace RockwellTagReader;

/// <summary>
/// EtherNet/IP encapsulation session with an Allen-Bradley CLP.
/// Implements RegisterSession / SendRRData / UnRegisterSession (24-byte header).
/// </summary>
internal sealed class EipSession : IDisposable
{
    private const ushort CmdRegisterSession   = 0x0065;
    private const ushort CmdUnRegisterSession = 0x0066;
    private const ushort CmdSendRRData        = 0x006F;

    private const int HeaderSize = 24;

    private readonly TcpClient _tcp = new() { NoDelay = true };
    private NetworkStream? _stream;
    private uint _sessionHandle;
    private bool _disposed;

    public uint SessionHandle => _sessionHandle;

    public bool IsConnected => _stream is not null && _tcp.Connected && _sessionHandle != 0;

    public async Task ConnectAsync(string host, int port, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        await _tcp.ConnectAsync(host, port, cts.Token);
        _stream = _tcp.GetStream();

        await RegisterSessionAsync(cts.Token);
    }

    private async Task RegisterSessionAsync(CancellationToken ct)
    {
        // Payload: ProtocolVersion (UINT16) + OptionFlags (UINT16)
        var payload = new byte[4];
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(0, 2), 0x0001);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(2, 2), 0x0000);

        var (status, _) = await SendCommandAsync(CmdRegisterSession, sessionHandle: 0, payload, ct);
        if (status != 0)
            throw new EipException(CmdRegisterSession, status, $"RegisterSession failed: status=0x{status:X8}");
        if (_sessionHandle == 0)
            throw new EipException(CmdRegisterSession, 0, "RegisterSession returned a null session handle");
    }

    /// <summary>
    /// Sends one EIP-encapsulated command and returns (status, payload).
    /// Captures and stores the SessionHandle returned for RegisterSession.
    /// </summary>
    public async Task<(uint Status, byte[] Payload)> SendCommandAsync(
        ushort command, uint sessionHandle, byte[] payload, CancellationToken ct)
    {
        if (_stream is null) throw new InvalidOperationException("Not connected");

        var frame = new byte[HeaderSize + payload.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(0, 2), command);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(2, 2), (ushort)payload.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(4, 4), sessionHandle);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(8, 4), 0);  // Status (always 0 on send)
        // SenderContext (8 bytes) + Options (4 bytes) left as zero
        Buffer.BlockCopy(payload, 0, frame, HeaderSize, payload.Length);

        await _stream.WriteAsync(frame, ct);

        // Read response header
        var header = new byte[HeaderSize];
        await ReadExactAsync(_stream, header, ct);

        var respCmd    = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(0, 2));
        var respLen    = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(2, 2));
        var respHandle = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4, 4));
        var respStatus = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8, 4));

        if (respCmd != command)
            throw new EipException(command, respStatus,
                $"EIP command mismatch: sent 0x{command:X4}, got 0x{respCmd:X4}");

        if (command == CmdRegisterSession && respStatus == 0)
            _sessionHandle = respHandle;

        var body = new byte[respLen];
        if (respLen > 0)
            await ReadExactAsync(_stream, body, ct);

        return (respStatus, body);
    }

    /// <summary>
    /// Sends a CIP message embedded in an SendRRData unconnected encapsulation.
    /// Returns the CIP response bytes (without the SendRRData wrapper).
    /// </summary>
    public async Task<byte[]> SendUnconnectedRRDataAsync(byte[] cipMessage, CancellationToken ct)
    {
        if (_sessionHandle == 0) throw new InvalidOperationException("Session not registered");

        // SendRRData payload structure:
        //   InterfaceHandle (UDINT) = 0
        //   Timeout (UINT16) = 5
        //   ItemCount (UINT16) = 2
        //   AddressItem: TypeID=0x0000 (Null), Length=0
        //   DataItem:    TypeID=0x00B2 (Unconnected Data), Length=N, Data=cipMessage
        var payload = new byte[4 + 2 + 2 + 4 + 4 + cipMessage.Length];
        var span = payload.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(span[..4], 0);                      // InterfaceHandle
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(4, 2), 5);               // Timeout
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(6, 2), 2);               // ItemCount
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(8, 2), 0x0000);          // AddressItem TypeID (Null)
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(10, 2), 0);              // AddressItem Length
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(12, 2), 0x00B2);         // DataItem TypeID (Unconnected Data)
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(14, 2), (ushort)cipMessage.Length);
        Buffer.BlockCopy(cipMessage, 0, payload, 16, cipMessage.Length);

        var (status, response) = await SendCommandAsync(CmdSendRRData, _sessionHandle, payload, ct);
        if (status != 0)
            throw new EipException(CmdSendRRData, status, $"SendRRData EIP status=0x{status:X8}");

        // Strip SendRRData wrapper to return raw CIP response bytes from the DataItem.
        // Wrapper before the DataItem payload: 4 (InterfaceHandle) + 2 (Timeout) + 2 (ItemCount)
        //   + 4 (AddressItem header, length=0)
        //   + 4 (DataItem header: TypeID + Length)
        const int wrapperLen = 4 + 2 + 2 + 4 + 4;
        if (response.Length < wrapperLen)
            throw new InvalidOperationException("SendRRData response too short");

        var dataItemLen = BinaryPrimitives.ReadUInt16LittleEndian(response.AsSpan(14, 2));
        if (response.Length < wrapperLen + dataItemLen)
            throw new InvalidOperationException("SendRRData DataItem truncated");

        var cipResp = new byte[dataItemLen];
        Buffer.BlockCopy(response, wrapperLen, cipResp, 0, dataItemLen);
        return cipResp;
    }

    private static async Task ReadExactAsync(NetworkStream stream, byte[] buffer, CancellationToken ct)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read), ct);
            if (n <= 0)
                throw new IOException("EIP socket closed unexpectedly");
            read += n;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_sessionHandle != 0 && _stream is not null)
        {
            // Best-effort UnRegisterSession (fire and forget; ignore errors).
            _ = SendCommandAsync(CmdUnRegisterSession, _sessionHandle, [], CancellationToken.None)
                .ContinueWith(_ => { }, TaskScheduler.Default);
        }

        _stream?.Dispose();
        _tcp.Dispose();
    }
}
