# Sluice: quick start

Five minutes from install to your first app going through a tunnel. Works the same on Windows and Linux.

## 1. Install

- **Windows:** run `Sluice-Setup-<version>-x64.exe` (it includes everything Sluice needs). Version 1.0.0 isn't
  code-signed yet, so SmartScreen may warn: **More info → Run anyway**. See [windows/INSTALL.md](windows/INSTALL.md).
- **Linux, Ubuntu 24.04 or later:** `sudo apt install ./sluice_<version>_amd64.deb` (with the `./`).
- **Linux, Debian, older Ubuntu and other distributions:** unpack `sluice-<version>-linux-x64.tar.gz` and run
  `sudo ./install.sh` in it, which installs everything else Sluice needs. Both Linux packages: [linux/INSTALL.md](linux/INSTALL.md).

Then open **Sluice** (Windows: the Start Menu; Linux: the applications menu or `sluice`). Capturing other apps' traffic
on Windows needs administrator rights: **Run as administrator**, or **Restart as administrator** in the banner.

## 2. Add your tunnel

Go to **Tunnels**, then either:

- **Import**: paste a share link (`vless://`, `vmess://`, `trojan://`, `ss://`, `hysteria2://`, `tuic://`, `ssh://`,
  `socks5://`, …) or switch to **Subscription URL** and paste the link your provider gave you; or
- **Add tunnel**: choose the protocol and fill in the server, port and login.

Select the tunnel and press **Check**. All three steps should get a ✓.

## 3. Choose what goes through it

Go to **Rules**. Rules are checked from top to bottom; the first one that matches decides. **Default** catches everything
else and goes Direct at first.

| You want… | Do this |
|---|---|
| **One app through the tunnel** | **Add rule** → Applications: `firefox` (Linux) or `firefox.exe` (Windows) → Action: your tunnel |
| **Some websites through the tunnel** | **Add rule** → Target hosts: `*.example.com; example.org` → Action: your tunnel |
| **Everything through the tunnel** | Double-click **Default** → Action: your tunnel. Add a rule above it for your local network: Target hosts `192.168.*.*; 10.*.*.*` → Action: Direct |
| **Block an app or site** | **Add rule** → Action: Block |

## 4. Start capturing

Press the **play button** at the bottom. The status changes to *Capture is on*.

Open the app or website, then look at **Connections**: each connection shows the rule that matched and its route.
**Log** shows everything Sluice decided. Press the button again to stop; Sluice removes its network rules.

## 5. Profiles

Changes are saved as you make them. To keep several setups, open the **profile menu** at the top right (your profile's
name): it lists your profiles, and a click switches to another one at once. **New profile…**, **Save as…**,
**Rename…** and **Delete…** manage them, and **Import profile file…** adds a `.sluice` file someone gave you. Sluice
reopens the last profile when it starts.

---

### Good to know

- **No capture needed?** Point an app's proxy setting at the SOCKS5 address shown at the bottom right
  (`127.0.0.1:<port>`); its traffic then follows your rules too.
- **Hostnames through the tunnel:** on by default (**Settings → Privacy** → *Look up website names*), so sites are
  looked up by your tunnel's server instead of your local network. Choose *On this PC* to use your local DNS instead.
- **UDP** (calls, games, QUIC) goes through tunnels that carry it (SOCKS5 and the sing-box protocols; not SSH or HTTP,
  so UDP of apps sent through those is blocked and browsers fall back to normal HTTPS). Apps with a **Direct** rule can
  use UDP as usual, also to sites whose names were looked up through the tunnel.
- **SSH with a private key:** paste the whole key into **Private key**, or use **Load key file…**; long RSA keys work
  too.
- **Headless?** `Sluice.Cli myprofile.sluice --intercept` (Linux: `sluice-cli`) does the same without a window.
