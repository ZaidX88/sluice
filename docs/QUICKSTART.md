# Sluice: quick start

Five minutes from install to your first app going through a tunnel. Works the same on Windows and Linux.

## 1. Install

- **Linux:** unpack the release and run `sudo ./install.sh`, which installs everything else Sluice needs. See [linux/INSTALL.md](linux/INSTALL.md).
- **Windows:** no installer yet. Build and run it as described in [windows/BUILD.md](windows/BUILD.md); capture needs "Run as administrator".

Then open **Sluice** (Linux: the applications menu or `sluice`).

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

## 5. Save

Open the **profile menu** at the top right (your profile's name) and choose **Save** or **Save as…**. Sluice reopens the
last profile when it starts.

---

### Good to know

- **No capture needed?** Point an app's proxy setting at the SOCKS5 address shown at the bottom right
  (`127.0.0.1:<port>`); its traffic then follows your rules too.
- **Hostnames through the tunnel:** **Settings → Name resolution** → *Resolve hostnames through the tunnel*, so sites are
  looked up by your tunnel's server instead of your local network.
- **UDP** (calls, games, QUIC) goes through tunnels on Windows. On Linux it isn't supported yet: UDP of tunneled apps is
  blocked and browsers fall back to normal HTTPS. Give call and game apps a **Direct** rule there.
- **Headless?** `Sluice.Cli myprofile.sluice --intercept` (Linux: `sluice-cli`) does the same without a window.
