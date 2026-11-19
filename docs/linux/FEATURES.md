# Features: Linux

Ideas not built yet for the **Linux** area (roughly by value) and its performance work. The other areas:
[Core](../core/FEATURES.md), [Windows](../windows/FEATURES.md), [UI](../ui/FEATURES.md). Work that also needs another
area says so; it gets a parent issue with one sub-issue per area ([CONTRIBUTING.md →
Issues](../CONTRIBUTING.md#issues)).

## Performance

- **Process lookup on the Linux packet thread** holds up other packets too. Move it to a worker and give verdicts
  out of order (NFQUEUE allows that).
