# rigPlay SimHub plugin

The PC side of rigPlay: a SimHub plugin with its own page in SimHub's left menu. It will discover and pair
rigPlay tablets, push the SimHub dashboard they show, and play their CarPlay audio on this PC (epic #3).
This is the skeleton: the page, settings persistence and logging.

```
plugin/
  RigPlay/          the plugin (net48), builds RigPlay.dll
  RigPlay.Tests/    xunit tests (net8.0) for the plugin's pure logic
  lib/              SimHub 9.12.6 assemblies the plugin compiles against (see lib/README.md)
  scripts/          make-icon.py, which draws RigPlay/Resources/icon.png
```

## Build and test

Needs the .NET 8 SDK; no Windows, Visual Studio or SimHub install.

```bash
dotnet test plugin/RigPlay.Tests
dotnet build plugin/RigPlay -c Release   # -> plugin/RigPlay/bin/Release/net48/RigPlay.dll
```

The plugin targets .NET Framework 4.8 through the `Microsoft.NETFramework.ReferenceAssemblies` package and
references WPF as plain assemblies, without `UseWPF` or XAML: the page is built in C# and picks up SimHub's own
controls and styles at runtime. That keeps the build cross-platform. The files the tests compile
(`ProtocolDefaults.cs`, `RigPlaySettings.cs`, `Theme.cs` and everything under `Core/`, `Protocol/` and `Net/`)
must stay free of SimHub and WPF types; SimHub-facing glue lives in `RigPlay.cs`, `PluginBridge.cs` and the page.

## Tablet server

The plugin implements [`docs/protocol.md`](../docs/protocol.md); `RigPlay.Tests/ProtocolFixturesTests.cs` checks the
codec against every file in [`protocol/fixtures/`](../protocol/fixtures/).

| Part | File | What it does |
|---|---|---|
| Messages | `Protocol/Messages.cs` | Typed messages and `MessageCodec` (decode, validate, encode). |
| Audio header | `Protocol/AudioHeader.cs` | The 12-byte datagram header codec (§10.2). |
| Beacon | `Net/DiscoveryBeacon.cs` | UDP 23710 every second, to each interface's directed broadcast and 255.255.255.255. |
| Control server | `Net/ControlServer.cs`, `Net/ClientSession.cs` | TCP 23711 (configurable), newline JSON, hello/welcome, heartbeats, 5 s watchdog, one session per tablet, `shutdown` on exit, LAN peers only. |
| Pairing | `Pairing/PairingService.cs` | PIN shown on the page (6 digits, 120 s, single use, 3 attempts, 5 starts/min), token issue and resume. Only the token's SHA-256 is stored (`PairedTablets[].TokenHash`). |
| Host | `Core/RigPlayHost.cs` | Runs the above inside SimHub without depending on it; the page and the SimHub glue read it. |

A port that cannot be bound is shown in the page's Status section and logged; the plugin keeps running. To poke the
server by hand: `nc <pc> 23711`, then type
`{"type":"hello","tabletId":"nc","name":"nc","appVersion":"0","protocol":1}` and Enter; the plugin answers `welcome`.

## Install

Copy `RigPlay.dll` into SimHub's install folder (`C:\Program Files (x86)\SimHub\`), restart SimHub and accept
the "new plugin found" prompt. **rigPlay** then appears in the left menu. Settings are stored in
`PluginsData\Common\RigPlay.RigPlaySettings.json`; log lines are prefixed `[rigPlay]` in SimHub's log.
