# Changelog

All notable changes to **RockwellTagReader** are documented here.
The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.1.0] - 2026-05-07

### Added
- Initial release.
- `RockwellPlcClient` with lazy-connect, semaphore-serialized requests, retry-on-transient,
  reconnect with backoff, and per-request timeout.
- `ReadAsync(string)` for single-tag reads.
- `ReadManyAsync(IReadOnlyList<string>)` returning decoded `TagReadResult`.
- `ReadRawAsync(IReadOnlyList<string>)` returning raw `MspRawResult` (typeCode + bytes).
- Multiple Service Packet (MSP) batching that automatically splits tag lists into
  multiple CIP requests to fit the ~504-byte unconnected-send budget.
- Logix tag syntax support: `Tag`, `Struct.Member`, `Array[N]`, atomic bit access `Tag.N`.
- `RockwellPlcClientOptions` with sensible defaults and CSV route path
  (e.g. `"1,0"` for backplane 1, slot 0).
- Optional `ILogger` integration (Microsoft.Extensions.Logging.Abstractions).
- Multi-target: `net6.0`, `net7.0`, `net8.0`, `net9.0`, `net10.0`.
