# RockwellTagReader

[![NuGet](https://img.shields.io/nuget/v/RockwellTagReader.svg)](https://www.nuget.org/packages/RockwellTagReader/)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![CI](https://github.com/ewerton336/RockwellTagReader/actions/workflows/ci.yml/badge.svg)](https://github.com/ewerton336/RockwellTagReader/actions/workflows/ci.yml)

Pure-C# read-only EtherNet/IP + CIP client for **Allen-Bradley ControlLogix / CompactLogix** PLCs.
Zero native dependencies, multi-targeted from `net6.0` to `net10.0`.

- Source: <https://github.com/ewerton336/RockwellTagReader>
- Issues: <https://github.com/ewerton336/RockwellTagReader/issues>

## What it does

- Connects via EtherNet/IP encapsulation (RegisterSession / SendRRData / UnRegisterSession).
- Issues CIP **Read Tag** (service `0x4C`) wrapped in **Unconnected Send** (service `0x52`).
- Batches multiple tags into **Multiple Service Packet** (service `0x0A`) requests, automatically
  splitting them to fit the ~504-byte unconnected-send budget.
- Decodes atomic types: `BOOL`, `SINT`, `INT`, `DINT`, `LINT` (signed and unsigned variants),
  `REAL`, `LREAL`, `BYTE`, `WORD`, `DWORD`, `LWORD`.
- Handles Logix tag syntax: `Tag`, `Struct.Member`, `Array[N]`, atomic bit access `Tag.N`.
- Resilient: lazy-connect, request serialization, per-request timeout, automatic
  reconnect-and-retry on socket / EIP `0x0064` (Invalid Session) errors.

## What it does NOT do (yet)

- No `Write Tag`.
- No connected (Class 3 / Forward Open) sessions — Unconnected Send only.
- No `STRING`, `UDT`, or array reads as a single batch (read each element individually).
- Tested on ControlLogix / CompactLogix only — PLC-5 / SLC-500 are out of scope.

## Install

```sh
dotnet add package RockwellTagReader
```

## Quickstart

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using RockwellTagReader;

await using var plc = new RockwellPlcClient(
    new RockwellPlcClientOptions
    {
        Host           = "192.168.1.10",
        Port           = 44818,            // default EtherNet/IP port
        RoutePath      = "1,0",            // backplane 1, slot 0
        ConnectTimeout = TimeSpan.FromSeconds(5),
        RequestTimeout = TimeSpan.FromSeconds(2),
    },
    logger: NullLogger.Instance);

// Single tag
var speed = await plc.ReadAsync("Motor1.Speed");
Console.WriteLine($"{speed.TagName} = {speed.DecodedValue} (success={speed.IsSuccess})");

// Multiple tags in one batched call
var tags = new[] { "Motor1.Speed", "Motor1.Current", "Tank.Level", "Pump.Running.0" };
var results = await plc.ReadManyAsync(tags);
foreach (var r in results)
    Console.WriteLine($"{r.TagName} = {(r.IsSuccess ? r.DecodedValue : r.ErrorMessage)}");
```

## Tag syntax

| Form              | Meaning                                                                |
|-------------------|------------------------------------------------------------------------|
| `MyTag`           | Symbolic top-level tag.                                                |
| `Struct.Member`   | UDT/struct member access (any depth).                                  |
| `Array[5]`        | Array element 5.                                                       |
| `Mat[2].State`    | Combinations.                                                          |
| `Flag.3`          | Bit 3 of an atomic (`BOOL/SINT/INT/DINT/LINT` and unsigned variants).  |
|                   | The CIP wire only carries the parent path; the bit is masked client-side. |

Trailing `.N` (digit-only suffix) is treated as bit access, not as an array element.
Use `[N]` for explicit array indexing.

## API overview

### `RockwellPlcClient`

```csharp
public sealed class RockwellPlcClient : IAsyncDisposable
{
    public RockwellPlcClient(RockwellPlcClientOptions options, ILogger? logger = null);

    public bool IsConnected { get; }
    public int  ReconnectCount { get; }

    public Task EnsureConnectedAsync(CancellationToken ct = default);

    public Task<TagReadResult> ReadAsync(string tagName, CancellationToken ct = default);

    public Task<IReadOnlyList<TagReadResult>> ReadManyAsync(
        IReadOnlyList<string> tagNames, CancellationToken ct = default);

    public Task<IReadOnlyList<MspRawResult>> ReadRawAsync(
        IReadOnlyList<string> tagNames, CancellationToken ct = default);
}
```

### `TagReadResult` (decoded)

```csharp
public sealed record TagReadResult(
    string  TagName,
    byte    GeneralStatus,
    ushort? TypeCode,
    byte[]? ValueBytes,
    int?    BitNumber,
    string? DecodedValue,
    string? ErrorMessage)
{
    public bool IsSuccess { get; }   // GeneralStatus == 0 && DecodedValue != null
}
```

### `MspRawResult` (raw — for `ReadRawAsync`)

```csharp
public sealed record MspRawResult(
    string  TagName,
    byte    GeneralStatus,
    ushort? TypeCode,
    byte[]? ValueBytes,
    string? ErrorMessage)
{
    public bool IsSuccess { get; }   // GeneralStatus == 0 && ValueBytes != null
}
```

Use **`ReadRawAsync`** when you want to decode `ValueBytes` yourself
(e.g. raw byte stream, custom format, or `ushort`/`uint` arithmetic without going via string).

## Logging

Pass any `ILogger` from `Microsoft.Extensions.Logging.Abstractions`. `null` is accepted —
the client will use `NullLogger.Instance` internally. The client only logs warnings on
transient failures and reconnection events; there are no info-level chatter logs in the
hot path.

## Threading

A single `RockwellPlcClient` instance owns one TCP socket and **serializes** all requests
through an internal semaphore. Sharing one instance across threads / tasks is safe and the
intended usage pattern; create one instance per PLC you need to talk to.

## Releasing

Releases to nuget.org are automated by `.github/workflows/ci.yml`:

1. Bump `<Version>` in `RockwellTagReader.csproj` and add a `CHANGELOG.md` entry.
2. Commit and push to `main`.
3. Tag and push: `git tag v0.1.0 && git push origin v0.1.0`.
4. The workflow's `publish` job picks up the tag, downloads the built `nupkg` /
   `snupkg`, and pushes them to <https://api.nuget.org/v3/index.json> using the
   `NUGET_API_KEY` repository secret.

To enable the publish step, configure on GitHub:

- **Settings → Secrets and variables → Actions** → add `NUGET_API_KEY`
  (generate at <https://www.nuget.org/account/apikeys>).
- *(Optional, recommended)* **Settings → Environments** → create an
  environment called `nuget` and add a required reviewer to gate the publish
  job behind a manual approval. The workflow already references this
  environment.

## License

[MIT](LICENSE) — Copyright (c) 2026 Ewerton Guimarães.
