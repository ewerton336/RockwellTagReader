using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace RockwellTagReader;

/// <summary>
/// Resilient EtherNet/IP + CIP read client for Allen-Bradley ControlLogix /
/// CompactLogix PLCs. Owns the lifecycle of a single TCP socket: lazy-connect,
/// per-request timeout, serialization via a semaphore, and automatic
/// reconnect+retry-once on IO/timeout/EIP 0x0064.
/// </summary>
/// <remarks>
/// One instance per PLC. Sharing across threads / tasks is safe — all requests
/// are serialized internally.
/// </remarks>
public sealed class RockwellPlcClient : IAsyncDisposable
{
    private readonly string _host;
    private readonly int _port;
    private readonly byte[] _routePath;
    private readonly TimeSpan _connectTimeout;
    private readonly TimeSpan _requestTimeout;
    private readonly int _maxReconnectAttempts;
    private readonly TimeSpan _reconnectBackoff;
    private readonly ILogger _logger;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private EipSession? _session;
    private int _reconnectCount;
    private bool _hasEverConnected;
    private bool _disposed;

    /// <summary>
    /// Creates a new client. The connection is opened lazily on the first read
    /// (or via <see cref="EnsureConnectedAsync"/>).
    /// </summary>
    /// <param name="options">Required configuration (host, port, route path, timeouts).</param>
    /// <param name="logger">Optional logger; <c>null</c> falls back to <see cref="NullLogger.Instance"/>.</param>
    public RockwellPlcClient(RockwellPlcClientOptions options, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        _host = options.Host;
        _port = options.Port;
        _routePath = options.GetRoutePathBytes();
        _connectTimeout = options.ConnectTimeout;
        _requestTimeout = options.RequestTimeout;
        _maxReconnectAttempts = Math.Max(0, options.MaxReconnectAttempts);
        _reconnectBackoff = options.ReconnectBackoff;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>How many times the client has reconnected since being created.</summary>
    public int ReconnectCount => Volatile.Read(ref _reconnectCount);

    /// <summary>True when the underlying EIP session is registered and the socket is open.</summary>
    public bool IsConnected => _session?.IsConnected ?? false;

    /// <summary>Forces the lazy connect to happen now (useful at startup).</summary>
    public async Task EnsureConnectedAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { await EnsureConnectedNoLockAsync(ct).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    /// <summary>Reads a single tag and returns its decoded value.</summary>
    public async Task<TagReadResult> ReadAsync(string tagName, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(tagName))
            throw new ArgumentException("tagName is required", nameof(tagName));

        var results = await ReadManyAsync(new[] { tagName }, ct).ConfigureAwait(false);
        return results[0];
    }

    /// <summary>
    /// Reads N tags batched into one or more MSP requests and returns decoded
    /// values, in the same order as <paramref name="tagNames"/>.
    /// </summary>
    public async Task<IReadOnlyList<TagReadResult>> ReadManyAsync(
        IReadOnlyList<string> tagNames, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tagNames);
        if (tagNames.Count == 0) return Array.Empty<TagReadResult>();

        var raw = await ReadManyRawNoLockOrLockedAsync(tagNames, ct).ConfigureAwait(false);
        var decoded = new TagReadResult[raw.Count];
        for (var i = 0; i < raw.Count; i++)
            decoded[i] = TagReadResult.From(raw[i]);
        return decoded;
    }

    /// <summary>
    /// Reads N tags batched into one or more MSP requests and returns the raw
    /// CIP type code + bytes for each. Use this when you want to decode the
    /// values yourself instead of going through the string-based decoder in
    /// <see cref="TagReadResult"/>.
    /// </summary>
    public async Task<IReadOnlyList<MspRawResult>> ReadRawAsync(
        IReadOnlyList<string> tagNames, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tagNames);
        if (tagNames.Count == 0) return Array.Empty<MspRawResult>();

        return await ReadManyRawNoLockOrLockedAsync(tagNames, ct).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<MspRawResult>> ReadManyRawNoLockOrLockedAsync(
        IReadOnlyList<string> tagNames, CancellationToken ct)
    {
        var batches = MultiServiceRequest.SplitTagsIntoBatches(tagNames);
        var results = new MspRawResult[tagNames.Count];
        var resultIdx = 0;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (var batch in batches)
            {
                IReadOnlyList<MspSubResult> batchSub;
                try
                {
                    var request = MultiServiceRequest.EncodeRequest(batch, _routePath);
                    var response = await SendWithRetryNoLockAsync(request, ct).ConfigureAwait(false);
                    batchSub = ParseSafe(response, batch.Count);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    _logger.LogWarning(ex,
                        "RockwellPlcClient.ReadMany: batch of {Count} tags failed", batch.Count);
                    batchSub = BuildErrorSubResults(batch.Count,
                        $"batch error: {ex.GetType().Name}: {ex.Message}");
                }

                for (var i = 0; i < batch.Count; i++)
                {
                    var sub = batchSub[i];
                    var tag = batch[i];
                    results[resultIdx++] = new MspRawResult(
                        TagName: tag,
                        GeneralStatus: sub.GeneralStatus,
                        TypeCode: sub.TypeCode,
                        ValueBytes: sub.ValueBytes,
                        ErrorMessage: sub.ErrorMessage);
                }
            }
        }
        finally
        {
            _gate.Release();
        }

        return results;
    }

    private async Task EnsureConnectedNoLockAsync(CancellationToken ct)
    {
        if (_session is { IsConnected: true }) return;

        _session?.Dispose();
        var session = new EipSession();
        try
        {
            await session.ConnectAsync(_host, _port, _connectTimeout, ct).ConfigureAwait(false);
        }
        catch
        {
            session.Dispose();
            throw;
        }
        _session = session;

        if (_hasEverConnected)
            Interlocked.Increment(ref _reconnectCount);
        else
            _hasEverConnected = true;
    }

    private async Task<byte[]> SendWithRetryNoLockAsync(byte[] cipMessage, CancellationToken ct)
    {
        var attempts = 0;
        while (true)
        {
            await EnsureConnectedNoLockAsync(ct).ConfigureAwait(false);

            using var perRequestCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            perRequestCts.CancelAfter(_requestTimeout);

            try
            {
                return await _session!.SendUnconnectedRRDataAsync(cipMessage, perRequestCts.Token)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (IsTransient(ex, ct, perRequestCts.Token))
            {
                if (ex is OperationCanceledException && perRequestCts.IsCancellationRequested
                    && !ct.IsCancellationRequested)
                {
                    if (attempts >= _maxReconnectAttempts)
                    {
                        _logger.LogWarning(
                            "RockwellPlcClient: request timeout after {Ms}ms (attempts={Attempts})",
                            _requestTimeout.TotalMilliseconds, attempts + 1);
                        throw new TimeoutException(
                            $"CIP request timed out after {_requestTimeout.TotalMilliseconds}ms");
                    }
                }
                else if (attempts >= _maxReconnectAttempts)
                {
                    _logger.LogWarning(ex,
                        "RockwellPlcClient: failure after {Attempts} attempt(s) — propagating", attempts + 1);
                    throw;
                }

                attempts++;
                await ReconnectNoLockAsync(ex, ct).ConfigureAwait(false);
            }
        }
    }

    private static bool IsTransient(Exception ex, CancellationToken outerCt, CancellationToken perReqCt)
    {
        if (ex is OperationCanceledException)
        {
            if (outerCt.IsCancellationRequested) return false;
            return perReqCt.IsCancellationRequested;
        }
        return ex is IOException
            or SocketException
            or EipException { IsInvalidSession: true };
    }

    private async Task ReconnectNoLockAsync(Exception cause, CancellationToken ct)
    {
        _logger.LogWarning(
            "RockwellPlcClient: connection lost ({Cause}). Reconnecting...",
            $"{cause.GetType().Name}: {cause.Message}");

        try { _session?.Dispose(); } catch { /* ignore */ }
        _session = null;

        if (_reconnectBackoff > TimeSpan.Zero)
            await Task.Delay(_reconnectBackoff, ct).ConfigureAwait(false);

        await EnsureConnectedNoLockAsync(ct).ConfigureAwait(false);
    }

    private static IReadOnlyList<MspSubResult> ParseSafe(byte[] response, int expected)
    {
        try
        {
            var parsed = MultiServiceResponse.Parse(response);
            if (parsed.Count == expected) return parsed;

            var padded = new MspSubResult[expected];
            for (var i = 0; i < expected; i++)
            {
                padded[i] = i < parsed.Count
                    ? parsed[i]
                    : new MspSubResult(0xFF, null, null, "missing sub-reply");
            }
            return padded;
        }
        catch (Exception ex)
        {
            return BuildErrorSubResults(expected, $"parse error: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static MspSubResult[] BuildErrorSubResults(int count, string message)
    {
        var arr = new MspSubResult[count];
        for (var i = 0; i < count; i++)
            arr[i] = new MspSubResult(0xFF, null, null, message);
        return arr;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        var disposeTimeout = TimeSpan.FromMilliseconds(
            Math.Max(1, _requestTimeout.TotalMilliseconds * 2));

        var acquired = false;
        try
        {
            acquired = await _gate.WaitAsync(disposeTimeout).ConfigureAwait(false);
        }
        catch { /* dispose best-effort */ }

        try { _session?.Dispose(); } catch { /* ignore */ }
        _session = null;

        if (acquired) _gate.Release();
        _gate.Dispose();
    }
}
