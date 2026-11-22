# Profiles and settings

Part of the [architecture reference](../ARCHITECTURE.md). Paths are relative to `src/Sluice.Core/Profiles/` unless
stated.

Two kinds of saved state, kept apart:

| | Profile | App settings |
|---|---|---|
| What | tunnels, chains, rules, subscriptions, routing options | window/app behaviour of the desktop app |
| Class | `Profile` (`Profile.cs`) | `AppSettings` (`src/Sluice.Desktop/Services/AppSettings.cs`) |
| File | a `*.sluice` file (the desktop app keeps them in its profiles folder) | `settings.json` in the settings folder |
| Shared by | desktop app and CLI | desktop app only |

## The model: `Profile.cs`

```
Profile
 ├─ Options: ProfileOptions
 │    ├─ Resolve: ResolveOptions (ViaProxy, BlockNonATypes, ExclusionList, OnlyFromListMode, …)
 │    ├─ PasswordEncryption: PasswordEncryptionMode
 │    ├─ Udp: UdpMode (Bypass | BlockQuic | BlockAll | Tunnel)
 │    └─ ConnectionLoopDetection, LeakPreventionMode, ProcessServices, HandleDirectConnections, …
 ├─ Tunnels: List<Tunnel>            (id, type, address, port, credentials, Settings, Enabled, SubscriptionId)
 ├─ Chains: List<TunnelChain>        (type, hops → tunnel ids)
 ├─ Rules: List<Rule>                (name, enabled, applications, targets, ports, RuleAction)
 ├─ Subscriptions: List<Subscription>
 └─ MasterPassword                   (memory only, never saved)
```

Rules and chains refer to tunnels **by id**; ids survive subscription refreshes (see
[tunnels.md](tunnels.md#share-links-and-subscriptions)). Some option names come from Proxifier and are kept so
existing `.sluice` files still read, even where Sluice does not act on them yet.

## `.sluice`: `SluiceProfileSerializer.cs`

Sluice's own format: readable JSON (`format: "sluice-profile"`, `version: 1`), camelCase properties, enum values in
lower case (`"socks5"`, `"vless"`). It stores every tunnel type including `Settings`.

- **Secrets are encrypted** with the profile's `PasswordEncryptionMode` (`ProxifierPasswordCodec`, below): the
  tunnel password and the settings listed in `SluiceProfileSerializer.SecretSettings` (`password`, `passphrase`,
  `privatekey`, `uuid`, `auth`, `psk`, `token`, `obfspassword`). `passwordHash` lets a master password be checked before
  any secret is decrypted.
- `Save` writes a temp file and renames it over the target.
- A newer `version` is refused with a clear message; a profile without a `Default` rule gets one.
- **`ProfileFile`** loads and saves `.sluice` only: a file that isn't a JSON object (a Proxifier `.ppx`, for example)
  fails with `ProfileFile.NotASluiceProfile`, "Not a Sluice profile. Sluice opens .sluice files only." Proxifier
  `.ppx` import and export were dropped before 1.0.0 ([DECISIONS.md](../DECISIONS.md), #133).

## Password encryption

`PasswordEncryption.cs` defines `PasswordEncryptionMode` (`Disabled`, `Basic`, `CurrentUser`, `MasterPassword`),
`IPasswordCodec` and the `MasterPasswordProvider` callback (the desktop app shows `PasswordDialog` through
`Dialogs.AskMasterPassword`; the CLI prompts on the console). `ProxifierPasswordCodec.cs` uses Proxifier 4's scheme (hence its name;
its doc comment has the byte layout), unchanged so existing `.sluice` files keep opening:

| Mode | Stored as | Who can read it |
|---|---|---|
| `Disabled` | plain text | anyone with the file |
| `Basic` (default) | AES-256-CBC with a fixed key | anyone with the file and Sluice (obfuscation only) |
| `CurrentUser` | Windows DPAPI (`Dpapi.cs`) | this Windows account; not available on Linux |
| `MasterPassword` | AES-256-CBC keyed from the master password | whoever knows it; asked up to 3 times on load |

`SluiceProfileSerializer.OpenCodec` checks the stored hash first: a wrong master password or a
different Windows account fails with `CryptographicFailure` before any value is decrypted. Moving DPAPI behind a
platform interface is part of [REQUIRED.md §1](../REQUIRED.md#1-move-windows-only-code-out-of-core).

## App settings and the settings folder

The settings folder is `<ApplicationData>/Sluice` (`AppSettings.Folder`): `%APPDATA%\Sluice` on Windows,
`~/.config/Sluice` on Linux. It holds:

| Path | Written by |
|---|---|
| `settings.json` | `AppSettings.Save`: last profile path, local SOCKS port (1080), start capture on launch, minimise to tray, file log on/off (on by default), its detail and folder, tunnel idle minutes, tunnel notifications, the network options (`Network`, below) |
| `Profiles/` | every profile the app offers, `<name>.sluice` (`AppSettings.ProfilesFolder`, `ProfileStore`, #134) |
| `Logs/` | the file log (`FileLog`), default `AppSettings.LogDirectory` |
| `fake-dns.json` | the capture's fake-IP mappings (`FakeIpPool.DefaultStatePath`) |

A missing or corrupt `settings.json` starts with defaults rather than refusing to launch.

**Network options belong to the app, not the profile (#148).** Name resolution (through the tunnel, the names resolved
locally, invert, leak prevention, other query types), connections (loop detection, direct connections, system
services) and the UDP mode live in `AppSettings.Network` (`NetworkSettings`), so opening another profile never changes
them. `EngineHost` copies them into `Profile.Options` whenever a profile is loaded, created or used, and
`ApplyNetworkSettings` does the same after a change on the Settings page. The engine and `SluiceProfileSerializer`
read `Profile.Options` as before, so the saved profile file carries the app's values and `sluice-cli` running it
behaves as the app did. `Network` is null until the first profile Sluice opens (or the first change on the page): that
profile's values carry over once (a new profile looks names up through the tunnel, #167), and from then on the app's win over a file's. Password protection stays in each
profile.

## Editing a profile in the app

View models change the `Profile` object in place and then call `EngineHost.CommitProfileEdit()`, which reloads the
engine (`RoutingEngine.Reload`, restarting capture if the UDP mode changed) and **autosaves** to the profile's file when
it has one. Learned SSH host keys are saved the same way.
