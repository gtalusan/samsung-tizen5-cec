# samsung-tizen5-cec

A headless **HDMI input-name service** for Samsung Tizen 5.x TVs (verified on
the 2020 Q90T, Tizen 5.5, armv7, kernel 4.1.10). It learns the *real names* of
devices on each HDMI port — the same names Samsung's own source bar shows
("RX-V4A", "PlayStation 4") — and serves them as JSON over HTTP for a custom
launcher such as [CleanFrame](https://github.com/gtalusan/CleanFrame).

```
{"hdmi1":"SAMSUNG","hdmi2":"SAMSUNG","hdmi3":"RX-V4A","hdmi4":"SAMSUNG",
 "devices":{"a5":{"name":"RX-V4A","port":3},"a8":{"name":"PlayStation 4","port":3}},
 "cec":["88504752582d563441", …]}
```

`GET http://<tv>:8787/` serves the live map. Port labels prefer the CEC name
of the shallowest device on the port (physical-address depth), falling back to
the EDID monitor name.

## How the names are obtained

There is **no public API** for input-source names on this platform (verified:
the TizenFX TV .NET surface has no input classes; `tizen.tvinputdevice` is
key-registration only in all versions 4.0–10.0). Two kernel-level routes work:

### CEC (the only name source)

Ports with no CEC name yet serve `null`; the launcher falls back to plain
"HDMI 1–4" labels. (EDID was explored first and dropped: the V4L2 control on
`/dev/video28` only returns the TV's own per-port TX tables — always
`SAMSUNG`. See *History*.)

`/dev/micom-cec` (mode 0666, SMACK label `*`) is a raw CEC passthrough to the
micom, the TV's own CEC engine. Reading it delivers CEC frames prefixed with a
length byte (`0x80|len`). The service listens passively and decodes:

- `<Set OSD Name>` (`0x47`) — device name by initiator address
- `<Report Physical Address>` (`0x84`) — maps a device to its HDMI port (PA
  top nibble) and its depth behind a receiver

Names arrive when **the TV itself asks**: during source switches, hotplugs
and device power-ons, the TV's discovery dialogue flows through this queue.
The micom **accepts writes but never transmits foreign frames** — sending
`<Give OSD Name>` yourself does nothing. Passive-only is the design.

Verified capture from a real bus: Yamaha RX-V4A (CEC addr 5, PA 3.0.0.0,
HDMI3), PlayStation 4 (addr 8, PA 3.3.0.0), an Apple device (addr 4,
PA 3.2.3.1) — the depth rule correctly labels HDMI3 as "RX-V4A".

## Building, signing, installing

```sh
~/.dotnet-x64/dotnet build -c Release          # .NET Core 3.1 SDK, x64
~/tizen-studio/tools/ide/bin/tizen package -t tpk -s q90t-sdk \
    -- bin/Release/tizen60/EdidServi5-1.0.0.tpk
```

- TFM `tizen60`, `Tizen.NET.Sdk` 1.1.9 + `Tizen.NET` 6.0.0.14995
- Sign with a `q90t-sdk`-style profile (author cert + the stock Tizen
  distributor cert); or leave unsigned and let Apps2Samsung sign it
- Install via **Apps2Samsung** (USB). Reinstalling the same appid after an
  Apps2Samsung-signed install hits `-11 Author certificate not match`
  (its author cert rotates per session) — uninstall from the TV's app
  management first, then install
- The service is `nodisplay="true"`: it never appears in the app grid

## Starting the service

The **only** launch trigger this platform honors is an app-context **key
rule**: **long-press (3s)** the key mapped to `XF86Exit`. The physical key
depends on the remote; on the 2020 OneRemote it is long-press Back.

Everything else was tested and **does not work** on Tizen 5.5:

| Mechanism | Result |
|---|---|
| Multi-target key rule (Home → launcher + service) | installer keeps the *last* target only |
| `source selector="app"` rule (launch on CleanFrame launch) | silently dropped at install |
| `on-boot="true"` | ignored for sideloaded apps |
| Rule-owner launch (making the service the carrier) | the carrier app is never executed |
| `Tizen.WebView` in a dotnet app (one-app launcher) | native crash at `new WebView()` |

Once launched, the service-application runs headless in the background while
ui-apps (the launcher) are foreground — that is what service-applications
are for, and it survives app switches.

## Runtime state

- Trace: `/opt/usr/home/owner/apps_rw/EdidServi5/data/e.status`
- Persistence: `…/cec-names.txt` (lines `N<addr>|<name>`, `P<addr>|<port>|<depth>`),
  loaded at startup so relaunches start warm

## History — the expensive lessons

1. **The "TV reboot wall" was a packing bug, not firmware.** The first EDID
   probes put `v4l2_ext_control`'s pointer at offset 16 (natural alignment);
   the kernel's packed struct reads offset 12. The dotnet probe left bytes
   12–15 uninitialized → the kernel read heap garbage as a pointer → the
   driver dereferenced it → panic → TV reboot. The native probe zeroed its
   buffers → the kernel read NULL → graceful `-EINVAL`, which looked like
   "EDID unsupported". Kernel-side evidence came from the T-KNMAKUC OSRC:
   `tztv-hdmi-sw/tztv_hdmi_switch_init.c` dereferences `ctrl->string` from a
   kernel copy (`check_array_args` backport) at packed offsets.
2. **Samsung's hub names come from CEC, not EDID** — established by dumping
   all four ports' EDIDs (all `SAMSUNG`) while the hub showed "RX-V4A".
3. **The micom never transmits foreign CEC frames** — 34 accepted writes
   (including mandatory-reply opcodes) produced zero replies; the captures
   that worked were the TV's own discovery dialogue.
4. **One key has one winner across apps** — registering a second app's
   `XF86Home` rule displaced the launcher's carrier rule and Home fell back
   to Samsung's popup. Never register Home from a companion app.
5. **`Tizen.WebView` crashes natively in dotnet ui-apps** on this firmware
   (the web engine's subprocess cannot spawn in a sideloaded app's SMACK
   context) — the one-app launcher architecture is dead; WGT launcher +
   headless service is the working split.
6. **HttpListener pitfalls**: `BeginGetContext(null, null)` after the first
   request silently stalls all later requests (re-arm with a named callback);
   the micom CEC driver's `read()` blocks forever and ignores `O_NONBLOCK`,
   so the listener must come up before any CEC activity.

## License

MIT — see LICENSE.
