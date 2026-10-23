# AGENTS.md

## What this repo is

A single-purpose Tizen TPK: a headless dotnet **service-application** that
learns HDMI input names over CEC and serves them as JSON on
`http://+:8787/` for a custom launcher. No UI, no deps, one HTTP endpoint.

## Invariants

- **Keep the TV identity stable.** `package="EdidServi5"` /
  `appid="EdidServi5.NameSvc"` must not change for a TV where it is already
  installed (renaming creates a second package and orphans the installed
  app-context rule). The label (`CecService`) is cosmetic; the ids are not.
- **Do not add a `XF86Home` rule.** One key has one winner across apps; a
  Home rule here displaces the launcher carrier's rule and Home falls back
  to Samsung's popup (learned the hard way — see README *History*).
- **Keep the CEC listener passive.** The micom accepts writes but never
  transmits foreign frames; write probes are dead code and noise.
- **Do not re-add WebView.** `new WebView()` crashes natively in dotnet
  ui-apps on this firmware; this repo is a headless service on purpose.
- **No EDID/V4L2 code.** EDID on this hardware only returns the TV's own
  "SAMSUNG" tables; the CEC bus is the sole name source.
- **No signing secrets in the repo.** Sign with your own profile or let
  Apps2Samsung sign the unsigned build.

## Build and verify

```sh
~/.dotnet-x64/dotnet build -c Release
~/tizen-studio/tools/ide/bin/tizen package -t tpk -s q90t-sdk \
    -- bin/Release/tizen60/EdidServi5-1.0.0.tpk
```

On the TV: launch via long-press Exit (3s), then
`curl http://<tv>:8787/` from the LAN and read the trace at
`/opt/usr/home/owner/apps_rw/EdidServi5/data/e.status` (uid-901 shell).

## Reference

QN55Q90TAFXZC / T-NKMAKUC-REL-202505100505 / Tizen 5.5.0 / armv7 / kernel
4.1.10 — the device this was developed and verified on. Related repos:
samsung-tizen-homekey (the launcher's Home-key carrier), CleanFrame.
