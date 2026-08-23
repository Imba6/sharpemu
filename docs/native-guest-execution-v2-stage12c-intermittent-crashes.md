# Stage 12C — run_fix was a lucky pre-17687b7 run past TWO intermittent guest AVs; no single missing delta

Status: **forensic analysis only, no production runtime change** (branch `gpt-dlsym`, not pushed).
28cab08 + run_fix's exact flags is 0/5, crashing repeatably at ~6 s. Root: run_fix reached gameplay by
**nondeterministically avoiding two intermittent guest access violations** that the reconstructed
baseline hits — not by any additional reconstructable dirty state.

## 1. 28cab08 + flags — five-run failure table

| run | first frame | crash RIP | module | family |
|---|---|---|---|---|
| 1 | yes | `0xC0000005 @ 0x8096BF27E` | **libresonanceaudio.prx** (`0x809668000..0x809702630`) | FMOD-plugin-uninit |
| 2 | yes | `@ 0x8096BF27E` | libresonanceaudio.prx | FMOD-plugin-uninit |
| 3 | yes | `@ 0x8096BF27E` | libresonanceaudio.prx | FMOD-plugin-uninit |
| 5 | yes | `@ 0x8096BF27E` | libresonanceaudio.prx | FMOD-plugin-uninit |
| 4 | **no** | `@ 0x8015A0759` | **`ayuoL6Vjz2k`** (eboot/Unity il2cpp) | Unity-engine AV |

All are `NATIVE EXCEPTION 0xC0000005` (access violation) on the main guest thread (`managed=2`,
`guest=0x0`). No import-loop guard, no backend-FAILED, no "No import progress".

## 2. Common crash family?

**Two families, one dominant.** 4/5 = the **libresonanceaudio** crash at `0x8096BF27E`; 1/5 = the
**Unity il2cpp** AV at `0x8015A0759` (module `ayuoL6Vjz2k`). Both are intermittent guest AVs, not the
same fault.

## 3. Exact ~6-second failure fingerprint (dominant family)

- RIP `0x8096BF27E`, module **libresonanceaudio.prx**, `Code 0xC0000005`, main thread, bad guest
  pointer `RBX=0x10371F4A0`, `RAX=3`, `RCX=RDX=0` (a `[null+0x10]` hash-bucket read).
- **This is the exact crash `17687b7` documents:** *"In Cocoon (PPSA08766) this is libresonanceaudio.prx:
  FMOD enters its code uninitialized, which surfaces **intermittently** as the guest crash at RIP
  **0x8096BF27E** ([null+0x10] in a hash lookup) and otherwise stalls the audio-gated frame loop at
  ~0.5fps."* Cause: FMOD discovers the plugin by dlsym'ing `FMODGetPluginDescriptionList` and calls its
  function pointers directly, so the plugin is never `LoadStartModule`'d and its DT_INIT (which sets up
  a string hash-map registry) never runs → NULL buckets pointer → AV.

## 4. Comparison to current-HEAD AV family

**Partial overlap.** HEAD's default crashes are `0x8015A027C` (module `ayuoL6Vjz2k`, `ayuoL6Vjz2k+0x6E040C`);
28cab08 run 4 is `0x8015A0759` (`ayuoL6Vjz2k+0x6E08E9`) — **same module, ~0x4DD apart = the same
Unity-engine AV family.** It is present at 28cab08, which **predates `2ff1d2a`**, so `2ff1d2a` (the
pthread-mutex change) is **NOT** its cause — it is a **long-lived pre-existing intermittent Unity AV
still present at HEAD**. HEAD does **not** show the `0x8096BF27E` libresonanceaudio crash because HEAD's
history includes `17687b7` (which boot-inits the plugin). So: 17687b7 removed the libresonanceaudio
crash; the `0x8015A0xxx` Unity AV survived into HEAD.

## 5. First divergence vs run_fix

run_fix and 28cab08 are code-identical here (both pre-17687b7: **11** boot module initializers, neither
boot-inits libresonanceaudio). The divergence is **not a code difference** — it is that **run_fix
executed libresonanceaudio's code without the intermittent NULL-bucket fault and never hit the Unity
AV**, i.e. it won the intermittent-crash lottery, while 28cab08+flags lost it 5/5.

## 6. Additional dirty-delta candidates & evidence

- **17687b7 (libresonanceaudio boot-init):** did run_fix have it? **NO** — run_fix's 11 boot
  initializers do **not** include libresonanceaudio (the fix promotes it to a 12th). So run_fix was
  **pre-17687b7** and simply did not trigger the intermittent crash that run. Evidence: the boot-module
  list, and the memory note that the fixed state has "12 boot module initializers including
  libresonanceaudio."
- **Other post-28cab08 commits (291de64 VEH, 2ff1d2a mutex, memory zero+revert):** no evidence any were
  present in run_fix (they postdate it), and the surviving Unity AV predates 2ff1d2a, so none explains
  run_fix's success.
- **Net:** there is **no additional reconstructable dirty delta** that made run_fix succeed. The
  Stage-12B reconstruction (ff66b25 + dirty 28cab08 + flags) was the correct *code* state; the gap is
  **nondeterminism**, not missing source.

## 7. Build / generated-artifact findings

No evidence implicates stale artifacts or a generated-Aerolib mismatch: both intermittent crashes are
guest-memory AVs whose fix (17687b7) is a source change, and run_fix's boot-module set matches 28cab08.
Not pursued further — the intermittent-crash explanation is sufficient.

## 8. Best reconstructed historical GOOD code state

**There is no pre-17687b7 commit that reliably reaches gameplay** — the dominant libresonanceaudio
crash is intermittent and unfixed before 17687b7. The best *reproducible* GOOD baseline is therefore
**`17687b7`** itself (a real commit that already contains 28cab08's GC-suspend fix **and** removes the
`0x8096BF27E` crash). Memory corroborates: at the 17687b7 era Cocoon "hits sustained 30fps gameplay;
new blocker = pre-existing worker-thread UCO/AV crash mid-gameplay" — i.e. the surviving `0x8015A0xxx`
Unity AV.

## 9–11. Confidence / is nondeterminism plausible

- Dominant 28cab08 crash = the libresonanceaudio `0x8096BF27E` bug (17687b7's own text names the RIP):
  **very high.**
- run_fix was pre-17687b7 and reached gameplay by avoiding the intermittent crash: **high** (boot-module
  list + "surfaces intermittently" wording).
- The `0x8015A0xxx` Unity AV is a long-lived pre-existing bug (predates 2ff1d2a, present at HEAD):
  **high.**
- **Nondeterminism (F) is now well-supported, not a fallback** — 17687b7 explicitly calls the crash
  intermittent, and run_fix's boot state is code-identical to the 5/5-failing 28cab08.

## 12. Next single Windows experiment

**Check out `17687b7` (not a dirty reconstruction — it already includes 28cab08), clean Windows build,
run 3–5× with `SHARPEMU_LOG_SEMA=1 SHARPEMU_LOG_GUEST_EXCEPTIONS=1`** (pre-V2 default),
→ `run_stage12_17687b7_N.log`:

```powershell
git clone E:\claude_src\virtualps5 E:\claude_src\virtualps5-hist17687
cd E:\claude_src\virtualps5-hist17687
git checkout 17687b7
dotnet build src\SharpEmu.CLI\SharpEmu.CLI.csproj -c Release -r win-x64
for ($i=1; $i -le 5; $i++) {
  $env:SHARPEMU_LOG_SEMA="1"; $env:SHARPEMU_LOG_GUEST_EXCEPTIONS="1"
  & "E:\claude_src\virtualps5-hist17687\artifacts\bin\Release\net10.0\win-x64\SharpEmu.exe" `
    "E:\claude_src\virtualps5\real-tests\Cocoon\PPSA08766-app0\eboot.bin" 2>&1 |
    Tee-Object -FilePath "E:\claude_src\virtualps5\real-tests\Cocoon\PPSA08766-app0\run_stage12_17687b7_$i.log"
}
```

Expected/decision:
- **Reaches gameplay in most runs** (the `0x8096BF27E` crash is gone; boot-module list should show 12
  including libresonanceaudio) → **17687b7 is the reproducible GOOD baseline.** The milestone then
  becomes the surviving **`0x8015A0xxx` Unity il2cpp AV** (characterize `ayuoL6Vjz2k`'s fault: bad
  pointer, owning thread, most-recent GC suspend) — the real long-lived blocker, present at HEAD too.
- **Still 0/5** → the surviving Unity AV is more frequent than expected; go straight to characterizing
  `0x8015A0xxx`.

## 13–14. Tooling / commits

No analyzer change needed (it classifies these AV runs as "did NOT reach gameplay"). Commit: this doc.

## 15. git status

Branch `gpt-dlsym`. New: this doc. Captures git-ignored (not committed).

## NOTHING WAS PUSHED BY THIS SESSION.

## Previous `origin/gpt-dlsym` advances were the user's manual checkpoint pushes.
