# Contributing to WTF

Thanks for looking under the hood. This page gets you from clone to a running
nested compositor, tells you what a good PR looks like here, and lists where
help is most wanted.

## Dev setup

```sh
git clone https://github.com/Neftedollar/WTF.git && cd WTF
sudo bash scripts/install-deps.sh   # build deps + .NET 10 SDK (into ~/.dotnet if missing)
bash scripts/build.sh               # vendored wlroots + scenefx + C shim + F# host
bash scripts/run.sh                 # run WTF nested in a window inside your session
```

`scripts/run.sh` is the core dev loop: WTF opens as a regular window inside
your existing desktop, so you can crash it freely. `Super+Shift+q` (or closing
the window) exits.

To test the full install path (what users get), `bash scripts/install.sh`
builds a self-contained publish and installs it atomically under `/usr/local`
— safe to run over a live WTF session.

## Tests & smoke

```sh
dotnet test WTF.slnx                # the F# brain: 786 xUnit + FsCheck tests
bash scripts/smoke-headless.sh      # boot the INSTALLED compositor headless,
                                    # drive it over IPC, check clean teardown
```

The property tests (FsCheck) are the backbone: layouts, the `Stack` zipper,
the reducer, and the protocol are checked against invariants, not examples.
If you change layout/workspace/focus semantics, add or extend a property.

Reproduce a CI failure for any distro locally (exactly what CI runs):

```sh
docker run --rm --device /dev/dri -v "$PWD:/w" -w /w \
  archlinux:latest bash scripts/ci-inside.sh
# images: debian:trixie ubuntu:24.04 fedora:latest archlinux:latest opensuse/tumbleweed
```

(The `--device /dev/dri` pass-through gives the container a DRM node for
Mesa's software GL; on a machine without one, `modprobe vgem` first.)

## Code layout

```
compositor/wtf-shim.c    the C body: wlroots 0.19 + scenefx compositor shim
compositor/wtf.h         the narrow C ABI — flat data only, no wlroots types
compositor/wtf-panel.c   layer-shell client library used by the bar/omnibox
src/WTF.Core/            the brain: Rect/Stack/Layout/World, Command/Reducer,
                         config DSL, palette, JSON protocol (pure F#, no I/O)
src/WTF.Host/            the process: P/Invoke bridge, chords, config loading,
                         IPC socket, wallpapers, session persistence
src/WTF.Config/          config.fsx compilation (FSharp.Compiler.Service)
src/WTF.TypeProviders/   the machine-aware Apps/Layouts/Xkb Type Providers
src/WTF.Desktop/         D-Bus services: notifications, battery, network, MPRIS
src/WTF.Agent/           the opt-in LLM driver behind `wtfctl ask`
src/WTF.Client/          shared client code (socket, fuzzy match, panel render)
src/WTF.Bar/             status bar     src/WTF.Omnibox/  launcher
src/wtfctl/              the CLI        src/WTF.Plugins/  layout-plugin loader
tests/                   one test project per src project
scripts/                 build / install / smoke / session tooling
packaging/               .desktop, portal config, PKGBUILD, rpm spec, patches
docs/                    user documentation (keep it in sync with behavior!)
```

Rule of thumb: window-management *decisions* belong in `WTF.Core` (pure,
testable); pixels, protocols, and hardware belong in the C shim; `WTF.Host`
only translates between them. The full rationale:
[docs/architecture.md](docs/architecture.md).

## PR expectations

- **Tests green**: `dotnet test WTF.slnx` passes; if you touched the shim,
  install scripts, or anything a user boots, `scripts/smoke-headless.sh` too.
  CI runs both, plus the 5-distro install matrix.
- **Observability is part of the fix** (house rule): a change isn't done until
  its failure modes are visible in the session log. Errors get a log line with
  enough context to debug from a user's bug report; nothing fails silently;
  never log secrets. If you fixed a crash, the log should show *why* it can't
  happen again.
- **Degrade, don't die**: user input (config, wallpapers, socket commands) is
  untrusted. A bad value is logged and falls back; it never takes the session
  down. Match the existing patterns (config last-good fallback, wallpaper
  fallback, xkb fallback).
- **Docs move with behavior**: if a PR changes anything a user can observe,
  update the matching page under `docs/` in the same PR.
- **Keep the C ABI narrow**: only flat, blittable data crosses `wtf.h`. A PR
  that passes a wlroots struct to F# will be asked to flatten it.

## Where help is wanted

- **Multi-monitor** — the top item. Tiling currently targets a single primary
  output (outputs attach/detach safely, but workspaces don't span or move
  across monitors). The brain's `World` model needs a per-output dimension and
  the shim needs per-output arrange paths.
- **Packaging** — AUR (a `PKGBUILD` draft is in `packaging/arch/`), Fedora
  COPR (`packaging/rpm/wtf-wm.spec`), and anything Nix/openSUSE. The release
  workflow already produces a stage tree that packagers can consume.
- **Docs** — friction reports from your first hour are as valuable as PRs.
  If a docs page led you astray, that's a bug; file it.
- **Interactive polish** — `Super+drag` move/resize of floating windows
  (today a grab can only start from a client's own move/resize request), more
  layouts (see `examples/SpiralLayout/` for a worked layout-plugin example),
  bar/omnibox theming.

Unsure whether an idea fits? Open an issue before writing code — cheap to
discuss, expensive to rewrite.

## License

MIT. By contributing you agree your contributions are licensed the same way.
