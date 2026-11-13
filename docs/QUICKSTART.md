# Sluice on Linux: quick start

Five minutes from download to your first app going through a tunnel.
For details and troubleshooting, see [INSTALL.md](INSTALL.md).

## 1. Install

```bash
tar -xzf sluice-linux-x64.tar.gz
cd sluice-linux-x64
sudo ./install.sh
```

Then open **Sluice** from the applications menu (or run `sluice`).

## 2. Add your tunnel

Go to **Tunnels**, then either:

- **Import**: paste a share link (`vless://`, `vmess://`, `trojan://`, `ss://`, `hysteria2://`, `tuic://`,
  `ssh://`, `socks5://`, …) or switch to **Subscription URL** and paste the link your provider gave you. Or:
- **Add tunnel**: choose the protocol and fill in the server, port and login.

Select the tunnel and press **Check**. All three steps should get a ✓.

## 3. Choose what goes through it

Go to **Rules**. Rules are checked from top to bottom, and the first one that matches decides.
**Default** catches everything else and goes Direct at first.

Pick one of these:

| You want… | Do this |
|---|---|
| **One app through the tunnel** (e.g. Firefox) | **Add rule** → Applications: `firefox` → Action: your tunnel |
| **Some websites through the tunnel** | **Add rule** → Target hosts: `*.example.com; example.org` → Action: your tunnel |
| **Everything through the tunnel** | Double-click **Default** → Action: your tunnel. Add a rule above it for your local network: Target hosts `192.168.*.*; 10.*.*.*` → Action: Direct |
| **Block an app or site** | **Add rule** → Action: Block |

App names on Linux have no `.exe`: use `firefox`, `chrome`, `curl`, or a full path like `/usr/bin/curl`.

## 4. Start capturing

Press the **play button** at the bottom. The status changes to *Capture is on*.

Open the app or website, then look at **Connections**: each connection shows the rule that matched and its route
(Direct or through your tunnel). **Log** shows everything Sluice decided.

Press the button again to stop. Sluice then removes all its network rules.

## 5. Save

Open the **profile menu** at the top right (your profile's name) and choose **Save** or **Save as…**. That saves your
tunnels and rules. Sluice reopens the last profile when it starts.

---

### Good to know

- **No capture needed?** Point an app's proxy setting at the SOCKS5 address shown at the bottom right
  (`127.0.0.1:<port>`); its traffic then follows your rules too.
- **Hostnames through the tunnel:** under **Settings → Name resolution**, turn on *Resolve hostnames through the
  tunnel*, so sites are looked up by your tunnel's server instead of your local network.
- **UDP** (calls, games, QUIC) can't go through tunnels on Linux yet: it's blocked for tunneled apps, and browsers
  fall back to normal HTTPS. Give call and game apps a **Direct** rule.
- **Headless server?** `sluice-cli myprofile.sluice --intercept` does the same without a window.
- **Uninstall:** `sudo /opt/sluice/uninstall.sh`
