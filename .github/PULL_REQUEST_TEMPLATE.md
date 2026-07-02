## What & why

<!-- One or two sentences. Link the issue if there is one: Fixes #NN -->

## How it was verified

<!-- Check what applies and say briefly how. -->

- [ ] `dotnet test WTF.slnx` green
- [ ] `bash scripts/smoke-headless.sh` green (required for shim / install / boot-path changes)
- [ ] Ran it nested (`scripts/run.sh`) or as a live session and exercised the change

## House rules checklist

- [ ] **Observability**: new failure modes produce a useful session-log line
      (context to debug from a bug report; no secrets logged; nothing fails silently)
- [ ] **Degrade, don't die**: bad user input (config, files, socket commands)
      logs and falls back — it cannot take the session down
- [ ] **Docs**: user-visible behavior changes update the matching `docs/` page
- [ ] **C ABI stays flat**: nothing but blittable data crosses `compositor/wtf.h`
      (skip if you didn't touch the boundary)

## Notes for the reviewer

<!-- Anything non-obvious: tradeoffs, rejected alternatives, follow-ups. -->
