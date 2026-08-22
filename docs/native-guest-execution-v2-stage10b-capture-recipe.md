# Stage 10B — Windows-native capture recipe (run this on the Windows desktop)

Goal: capture ONE bad Cocoon run (first frame → post-first-frame stall) with the Path-B
LoadStartModule + equeue tracing on, so the causal chain (stalled module → equeue → producer)
can be read off a single log. The run must be **Windows-native** (RTX 4080 + Vulkan); WSL/Linux
does not reproduce it. The instrumentation is already committed on `gpt-dlsym`
(`SHARPEMU_LOG_LOADSTART`, Stage 10) — just build current HEAD.

## 0. Build (clean, Windows-native, current gpt-dlsym HEAD)

Build the emulator on Windows as you normally do (Release, `win-x64`) from the current
`gpt-dlsym` tip — it already contains the `SHARPEMU_LOG_LOADSTART` trace. Do a **clean** build so
Windows `obj/` is not mixed with the Linux build state (`git clean`/clean rebuild per your
workflow). Then reuse the build with `--no-build` for repeated launches.

## 1. Environment (the gated config that exhibits the Stage-7/10 stall)

Set exactly these before launching (PowerShell shown; values are literal):

```powershell
$env:SHARPEMU_NATIVE_GUEST_V2            = "1"    # V2 native-worker guest execution
$env:SHARPEMU_EXPERIMENT_0X1E_RENTED_WORKER = "1" # Stage-7 rented-worker routing (needed to hit the stall)
$env:SHARPEMU_NATIVE_WORKER_MAX          = "128"  # pool size used in Stage 9
$env:SHARPEMU_LOG_LOADSTART              = "1"    # Path-B module_start lifecycle (enter/begin/complete/skip)
$env:SHARPEMU_LOG_EQUEUE                 = "1"    # equeue wait/wake/produce trace (handle, gth, ret, ident/filter)
$env:SHARPEMU_LOG_GUEST_THREADS          = "1"    # guest-thread run-state in the stall watchdog dump (producer state)
```

Do **not** set `SHARPEMU_DISABLE_NATIVE_GUEST_WORKERS`. Do not add any other routing/behavior
flags — these are logging + the already-documented gates only.

## 2. Launch and capture

```powershell
.\SharpEmu.exe "<repo>\real-tests\Cocoon\PPSA08766-app0\eboot.bin" 2>&1 |
  Tee-Object -FilePath "<repo>\real-tests\Cocoon\PPSA08766-app0\run_stage10b_bad.log"
```

- **Exact log filename I want:** `real-tests/Cocoon/PPSA08766-app0/run_stage10b_bad.log`
  (a BAD run: first frame reached, then stall). `2>&1` matters — the emulator logs to stderr.
- A **BAD** run = the Vulkan `presented first frame` line appears, then draws freeze / no
  sustained ~30 fps, and you do **not** see all three `sceKernelLoadStartModule started` INFO
  lines (`PSNCore` → `PSNCommon` → `SaveData`). Let it run ~30–60 s past the first frame so the
  `[LOADER][WARN] No import progress for 20s …` watchdog + `[LOADER][ERROR] Stall guest-thread:`
  dump fire, then close it. That bounds the log and gives the producer-state snapshot.
- The stall is **non-deterministic** — some runs reach gameplay instead. If a run sustains ~30 fps,
  it is a GOOD run; just relaunch until one stalls. (Repeat launches can use `--no-build`.)

Optional but very useful for the good-vs-bad diff: also capture one GOOD run the same way to
`run_stage10b_good.log`.

### Fallback if the stall will not reproduce

Stage 9 sometimes needed the dedicated-primary patch to pin the exact post-first-frame config.
Apply it **experimentally only**, then revert (do not commit it):

```
git apply docs/native-guest-execution-v2-stage5c-dedicated-primary-worker.patch
# clean rebuild, run, capture
git apply -R docs/native-guest-execution-v2-stage5c-dedicated-primary-worker.patch
```

## 3. Hand the log back

Drop `run_stage10b_bad.log` (and `run_stage10b_good.log` if captured) under
`real-tests/Cocoon/PPSA08766-app0/`. I will run the analyzer and read off the causal chain — no
fix will be attempted before that.

## 4. What I will run on the returned log (read-only)

```bash
python3 scripts/analyze_stage10b_capture.py real-tests/Cocoon/PPSA08766-app0/run_stage10b_bad.log \
  [--good real-tests/Cocoon/PPSA08766-app0/run_stage10b_good.log]
```

It joins the LoadStartModule lifecycle (`guest_thread`) to the equeue trace (`gth`) and reports:

1. **Stalled module** — the `module_start.begin` with no `module_start.complete` (module name,
   `guest_thread`, `init`).
2. **Exact equeue** it blocked on — `handle`, `timeout`, `waiter`, `depth`, `registrations`,
   caller RIP (`ret`).
3. **Producer verdict** for that handle:
   - no producer/real-wake after the block → **CASE A/B** (producer starvation / never-generated);
   - a real `trigger*`/`enqueue`/`wake` targets the handle yet the waiter stayed blocked →
     **CASE C** (possible lost wake — then, and only then, an equeue fix is warranted, gated behind
     a synthetic regression).
4. **Candidate producer states** from the stall watchdog dump (`Stall guest-thread` handles/names/
   `state=`/`block=`).
5. With `--good`: the **first module_start divergence** between the good and bad sequences.

Per the milestone this is diagnosis only — no runtime behavior changes until the BAD log proves the
exact case.
