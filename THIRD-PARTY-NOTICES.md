# Third-party notices

Sluice is licensed under the GNU General Public License, version 3 ([LICENSE](LICENSE)). Its packages also contain
the third-party components below, each under its own licence. Versions are the ones the build pins
(`build/Dependencies.targets`, the `PackageReference`s in `src/*/*.csproj`); update this file when they change.

## Programs shipped next to Sluice

These run as separate programs or a separate driver, and are not part of Sluice's own code.

| Component | Version | Licence | Copyright | Source |
|---|---|---|---|---|
| **sing-box** (`sing-box`, `sing-box.exe`): the helper that runs VLESS, VMess, Trojan, Shadowsocks, Hysteria2, TUIC and SSH tunnels | 1.14.4 | GPL-3.0-or-later | Copyright (C) 2022 by nekohasekai | <https://github.com/SagerNet/sing-box/tree/v1.14.4> |
| **WinDivert** (`WinDivert.dll`, `WinDivert64.sys`, Windows only): packet capture | 2.2.2 | LGPL-3.0 or GPL-2.0, at your choice | (C) 2019 basil | <https://github.com/basil00/WinDivert/tree/v2.2.2> |

The build copies each one's own licence file next to it (`sing-box-LICENSE.txt`, `WinDivert-LICENSE.txt`), and the
packages ship those files. Both are distributed unmodified, as published by their authors at the release tags linked
above, where their complete source code is available.

## Libraries in the Sluice programs

| Component | Version | Licence | Copyright | Source |
|---|---|---|---|---|
| Avalonia, Avalonia.Desktop, Avalonia.Themes.Fluent, Avalonia.Skia, Avalonia.HarfBuzz, Avalonia.Win32, Avalonia.X11, Avalonia.FreeDesktop, Avalonia.FreeDesktop.AtSpi, Avalonia.Native, Avalonia.Remote.Protocol | 12.1.6 | MIT | Copyright 2013-2026 © The AvaloniaUI Project | <https://github.com/AvaloniaUI/Avalonia> |
| Avalonia.Controls.DataGrid | 12.1.2 | MIT | Copyright 2013-2026 © The AvaloniaUI Project | <https://github.com/AvaloniaUI/Avalonia.Controls.DataGrid> |
| Avalonia.Angle.Windows.Natives (ANGLE, Windows only) | 2.1.27548.20260419 | BSD-3-Clause | Copyright 2018 The ANGLE Project Authors | <https://github.com/AvaloniaUI/angle> |
| SkiaSharp (+ native assets, which contain Skia) | 3.119.4 | MIT (Skia: BSD-3-Clause) | Copyright (c) 2015-2016 Xamarin, Inc.; Copyright (c) 2017-2018 Microsoft Corporation; Skia: Copyright Google Inc. | <https://github.com/mono/SkiaSharp> |
| HarfBuzzSharp (+ native assets, which contain HarfBuzz) | 8.3.1.3 | MIT (HarfBuzz: "Old MIT") | Copyright (c) 2015-2016 Xamarin, Inc.; Copyright (c) 2017-2018 Microsoft Corporation; HarfBuzz: its authors | <https://github.com/mono/SkiaSharp> |
| MicroCom.Runtime | 0.11.6 | MIT | Copyright 2021 © Nikita Tsukanov | <https://github.com/kekekeks/MicroCom> |
| Tmds.DBus.Protocol | 0.94.1 | MIT | Copyright Tom Deseyn | <https://github.com/tmds/Tmds.DBus> |
| SSH.NET | 2026.0.0 | MIT | Copyright © Renci 2010-2026 | <https://github.com/sshnet/SSH.NET> |
| BouncyCastle.Cryptography | 2.7.0 | MIT | Copyright (c) 2000-2026 The Legion of the Bouncy Castle Inc. | <https://github.com/bcgit/bc-csharp> |
| Microsoft.Extensions.Logging.Abstractions | 8.0.3 | MIT | Copyright (c) .NET Foundation and Contributors | <https://github.com/dotnet/runtime> |
| Microsoft.Extensions.DependencyInjection.Abstractions | 8.0.2 | MIT | Copyright (c) .NET Foundation and Contributors | <https://github.com/dotnet/runtime> |
| JetBrains Mono font (embedded in the desktop app) | 2.304 | SIL Open Font License 1.1 | Copyright 2020 The JetBrains Mono Project Authors | <https://github.com/JetBrains/JetBrainsMono> |

Each NuGet package also carries its own licence and notice files (for example SkiaSharp's and HarfBuzzSharp's
third-party notices for Skia and HarfBuzz); they are the authoritative texts. The .NET runtime is not shipped with
Sluice: it is installed separately.

## Licence texts

- **GPL-3.0**: [LICENSE](LICENSE).
- **LGPL-3.0 and GPL-2.0** (WinDivert): `WinDivert-LICENSE.txt`, shipped next to WinDivert.
- **SIL Open Font License 1.1** (JetBrains Mono): [`src/Sluice.Desktop/Assets/Fonts/OFL.txt`](src/Sluice.Desktop/Assets/Fonts/OFL.txt),
  shipped in the packages as `OFL.txt`.

### MIT License

Applies to the components marked MIT above, with each one's copyright line from the tables.

```
Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated
documentation files (the "Software"), to deal in the Software without restriction, including without limitation the
rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit
persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the
Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE
WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR
COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
```

### BSD 3-Clause License (ANGLE)

```
Copyright 2018 The ANGLE Project Authors.
All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions
are met:

    Redistributions of source code must retain the above copyright
    notice, this list of conditions and the following disclaimer.

    Redistributions in binary form must reproduce the above
    copyright notice, this list of conditions and the following
    disclaimer in the documentation and/or other materials provided
    with the distribution.

    Neither the name of TransGaming Inc., Google Inc., 3DLabs Inc.
    Ltd., nor the names of their contributors may be used to endorse
    or promote products derived from this software without specific
    prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
"AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS
FOR A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE
COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT,
INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING,
BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT
LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN
ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE
POSSIBILITY OF SUCH DAMAGE.
```
