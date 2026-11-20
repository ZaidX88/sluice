# Features: Linux

Ideas not built yet for the **Linux** area (roughly by value) and its performance work. The other areas:
[Core](../core/FEATURES.md), [Windows](../windows/FEATURES.md), [UI](../ui/FEATURES.md). Work that also needs another
area says so; it gets a parent issue with one sub-issue per area ([CONTRIBUTING.md →
Issues](../CONTRIBUTING.md#issues)).

## Security

- **A small privileged helper instead of capabilities on the app.** Since
  [GHSA-qvhh-fmwh-wx3j](https://github.com/ZaidX88/sluice/security/advisories/GHSA-qvhh-fmwh-wx3j), a native launcher
  (`packaging/linux/launcher/`) holds the capture capabilities and starts the app with a fixed environment. The app
  itself still runs with them, and with it everything it loads: the .NET runtime, Avalonia, Skia, fonts, GTK for the
  file dialogs, D-Bus. Each is a library a local user can feed input to (settings, fonts, themes, D-Bus messages) in a
  process that can read any file and trace any process.
  - **Idea:** a small root-owned helper that does only the privileged steps (the nftables rules, the NFQUEUE socket,
    the group switch, the owner lookups through `/proc`), with a narrow interface to the app over a socket only Sluice
    can reach. The desktop app and the CLI then run with no capabilities at all, and the launcher has nothing to pass on.
  - **Cost:** a new process and protocol, and the capture's packet path (NFQUEUE verdicts) crosses it, so it needs a
    design that keeps throughput (for example the helper owns the queue and hands decisions to the app in batches).
    Too big for v1.0.0.
  - **Needs:** Linux (the helper, the packaging) and Core (the capture's split between deciding and the privileged
    I/O, like Windows' `UdpNat` split in #59). A parent issue with both parts when it is planned.

## Performance

- ~~**Process lookup on the Linux packet thread** holds up other packets too~~ **done** (issue #83): the NFQUEUE
  thread only receives; 4 worker threads (`VerdictWorkers`) decide and give verdicts out of order, through a bounded
  hand-off that accepts a packet unmarked when it is full (fail-open), so the receive loop never blocks.
