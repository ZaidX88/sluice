# Profiles and settings

Part of the [architecture reference](../ARCHITECTURE.md). Paths are relative to `src/Sluice.Core/Profiles/` unless
stated.

Two kinds of saved state, kept apart:

| | Profile | App settings |
|---|---|---|
| What | tunnels, chains, rules, subscriptions, routing options | window/app behaviour of the desktop app |
| Class | `Profile` (`Profile.cs`) | `AppSettings` (`src/Sluice.Desktop/Services/AppSettings.cs`) |
| File | any `*.sluice` the user picks (or `*.ppx` imported) | `settings.json` in the settings folder |
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
[tunnels.md](tunnels.md#share-links-and-subscriptions)). Many option names come from Proxifier and are kept for `.ppx`
round-trips even where Sluice does not act on them yet.

## `.sluice`: `SluiceProfileSerializer.cs`

Sluice's own format: readable JSON (`format: "sluice-profile"`, `version: 1`), camelCase properties, enum values in
lower case (`"socks5"`, `"vless"`). It stores every tunnel type including `Settings`.

- **Secrets are encrypted** with the profile's `PasswordEncryptionMode`, the same modes and codec as Proxifier: the
  tunnel password and the settings listed in `SluiceProfileSerializer.SecretSettings` (`password`, `passphrase`,
  `privatekey`, `uuid`, `auth`, `psk`, `token`, `obfspassword`). `passwordHash` lets a master password be checked before
  any secret is decrypted.
- `Save` writes a temp file and renames it over the target.
- A newer `version` is refused with a clear message; a profile without a `Default` rule gets one.
- **`ProfileFile`** picks the format by content (`<` = XML = `.ppx`) on load and by extension on save.

## Proxifier `.ppx`: `PpxSerializer.cs`

Proxifier 4 profiles (`ProxifierProfile version="102"`) are **import and export only**, kept byte-compatible
(a settled decision, [DECISIONS.md](../../claude/DECISIONS.md)).

- **Import:** the desktop app converts an opened `.ppx` into a new `.sluice` file in the profiles folder and works on
  that copy (`EngineHost.LoadProfile`); the original is never written back, because autosaving into `.ppx` would lose
  tunnels it cannot store.
- **Export:** `PpxSerializer.ToXml` writes only the simple types (SOCKS4/5, HTTP(S)). Encrypted tunnels are left out,
  rules that used them become Direct and chains lose those hops; every loss is reported as a warning.

## Password encryption

`PasswordEncryption.cs` defines `PasswordEncryptionMode` (`Disabled`, `Basic`, `CurrentUser`, `MasterPassword`),
`IPasswordCodec` and the `MasterPasswordProvider` callback (the desktop app shows `PasswordDialog` through
`Dialogs.AskMasterPassword`; the CLI prompts on the console). `ProxifierPasswordCodec.cs` reproduces Proxifier 4's scheme exactly
(its doc comment has the byte layout):

| Mode | Stored as | Who can read it |
|---|---|---|
| `Disabled` | plain text | anyone with the file |
| `Basic` (default) | AES-256-CBC with a fixed key | anyone with the file and Sluice (obfuscation only) |
| `CurrentUser` | Windows DPAPI (`Dpapi.cs`) | this Windows account; not available on Linux |
| `MasterPassword` | AES-256-CBC keyed from the master password | whoever knows it; asked up to 3 times on load |

`OpenCodec` (in `PpxSerializer`, shared by both formats) checks the stored hash first: a wrong master password or a
different Windows account fails with `CryptographicFailure` before any value is decrypted. Moving DPAPI behind a
platform interface is part of [REQUIRED.md §1](../REQUIRED.md#1-move-windows-only-code-out-of-core).

## App settings and the settings folder

The settings folder is `<ApplicationData>/Sluice` (`AppSettings.Folder`): `%APPDATA%\Sluice` on Windows,
`~/.config/Sluice` on Linux. It holds:

| Path | Written by |
|---|---|
| `settings.json` | `AppSettings.Save`: last profile path, local SOCKS port (1080), start capture on launch, minimise to tray, file log on/off and folder, tunnel idle minutes, tunnel notifications |
| `Profiles/` | `.ppx` imports converted to `.sluice` (`AppSettings.ProfilesFolder`) |
| `Logs/` | the optional file log (`FileLog`), default `AppSettings.LogDirectory` |
| `fake-dns.json` | the capture's fake-IP mappings (`FakeIpPool.DefaultStatePath`) |

A missing or corrupt `settings.json` starts with defaults rather than refusing to launch.

## Editing a profile in the app

View models change the `Profile` object in place and then call `EngineHost.CommitProfileEdit()`, which reloads the
engine (`RoutingEngine.Reload`, restarting capture if the UDP mode changed) and **autosaves** to the profile's file when
it has one. Learned SSH host keys are saved the same way.
