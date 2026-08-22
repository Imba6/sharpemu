# Native Guest Execution V2 — Stage 6: worker-thread 0x1E storm / UCO correctness

Status: root cause **proven** (branch `gpt-dlsym`, not pushed). This document is the
diagnosis; the fix and its validation follow in later sections/commits.

## The question

Under `SHARPEMU_NATIVE_GUEST_V2=1` ordinary guest pthreads (Unity `Job.Worker` etc.) run
their guest code on native workers (raw OS threads, GC-safe). Yet Cocoon still hits a
`__fastfail` "attempted to call a UnmanagedCallersOnly method from managed code" (UCO) on
those worker threads during IL2CPP/Boehm GC stop-the-world (`0x1E`) delivery. Why, if
top-level worker execution is already off CLR-managed stacks?

## Root cause (proven, not assumed)

**A *running* worker delivers a queued 0x1E INLINE at an import boundary, from inside the
worker's still-active managed import-dispatch reverse-P/Invoke frame** — producing the
guest→managed→guest sandwich that trips the CLR's reverse-P/Invoke UCO check.

Delivery path for a running worker:
- Worker runs guest on its native base (preemptive GC mode — safe).
- Guest calls an HLE import → reverse-P/Invoke into the managed import dispatch
  (`DirectExecutionBackend.Imports.cs`); the CLR flips the worker to **cooperative** GC
  mode for the managed handler.
- After the export, still inside that managed frame,
  `DirectExecutionBackend.Imports.cs:607-612` (and mirror `:1441-1446`) sees
  `_pendingGuestExceptionCount != 0` and calls **`DeliverPendingGuestExceptionAtSafePoint`**
  (`DirectExecutionBackend.cs:5320`) → `TryCallGuestFunction`
  → `ExecuteGuestThreadEntry(reentrant: true)` → `ShouldRunGuestOnNativeWorker` returns
  false for reentrant (`NativeWorker.cs:49-50`) → **inline `CallNativeEntry`**
  (`DirectExecutionBackend.cs:6440`). The guest 0x1E handler now runs **above the live
  managed import frame** on a cooperative-mode, CLR-attached thread.
- The handler does its GC-suspend ack (`sceKernelSignalSema` then waits on
  `sceKernelWaitEventFlag`) — each is another reverse-P/Invoke. That inner reverse-P/Invoke
  prolog asserts "entering managed from native/preemptive", but the thread is already
  cooperative with an unwind-info-less guest frame between two managed layers → the CLR
  raises the fatal UCO via `__fastfail` (int 29h, bypassing VEH).

This is the exact worker analog of the primary host-park bug fixed in Stage 5.

### Flight-recorder proof

Reproduced with `SHARPEMU_UCO_FLIGHT=1` + `SHARPEMU_LOG_GUEST_EXCEPTIONS=1` (Cocoon reached
~31 fps, then crashed). The `[UCOFR]` flush immediately before the `Fatal error.` line
contains one thread distinct from all the others:

```
mtid=106 guest='kernel exception 0x1E safe point' nest=0 seq=376490
  ring=[ I:sceKernelWaitEventFlag@0x800CBE038  I:pthread_mutex_lock  I:pthread_mutex_unlock … ]
… native_run_enter name='Job.Worker 11' … name='Job.Worker 7' …
Fatal error.
Invalid Program: attempted to call a UnmanagedCallersOnly method from managed code.
```

- `guest='kernel exception 0x1E safe point'` is the label
  `DeliverPendingGuestExceptionAtSafePoint` passes to `TryCallGuestFunction`
  (`DirectExecutionBackend.cs:5386`) — i.e. the **running-worker safe-point** delivery, mid
  handler, calling imports (reverse-P/Invokes).
- Concurrent `Job.Worker` reverse-P/Invokes (`native_run_enter`) race the GC → `__fastfail`.

In the same run the **parked** delivery path succeeded **711/711** (`delivery_exit …
success=True`, `elapsed_ms` ~28–31 s each = they wait out the GC suspend). Only **12**
safe-point deliveries occurred, and one coincided with the crash. So:
- **Parked / Route C delivery is safe.** `TryRaiseGuestException`'s parked-cooperative
  branch (`DirectExecutionBackend.cs:5088-5276`) runs `DeliverException` on the thread's own
  `GuestExecutionRunner` from a **clean managed base with no active outer import** — the
  calli-to-guest transition puts the thread in preemptive before the handler's inner
  reverse-P/Invoke, so the UCO assertion holds.
- **Running-worker safe-point delivery is the crash** — inline, above a live import frame,
  thread already cooperative.

## Blocked vs running

- **Blocked cooperative worker:** the collector's `TryRaiseGuestException` finds it parked →
  Route C on its idle `GuestExecutionRunner` (clean base) → safe.
- **Running worker:** `TryRaiseGuestException` finds it Running → queues (`Route B`,
  `_pendingGuestExceptions`) → delivered later at the next import boundary via the inline
  safe-point path → **unsafe**. This is timing-dependent (a worker must be *running*, not
  parked, when 0x1E arrives), which is why the crash is non-deterministic (~50% of runs).

## The fix principle

Never run the guest 0x1E handler inline from inside an active managed import
reverse-P/Invoke frame. Instead, at the running-worker safe point:
1. capture the interrupted guest continuation (`CaptureImportBoundaryContinuation`),
2. **yield out** of the import back to the native-worker loop (reuse
   `TryYieldGuestThreadToHostStub` — the exact mechanism a cooperative block already uses to
   unwind the reverse-P/Invoke), surfacing a new `GuestNativeCallExitReason.ExceptionPending`,
3. deliver the handler from `RunGuestThread` on the thread's own `GuestExecutionRunner`
   (clean managed base, no active outer import — the proven-safe Route C shape), letting the
   handler's own resume-handshake block drive through `ResumeBlockedNestedGuestCallback`,
4. resume the interrupted continuation as a fresh native-worker slice.

The nested/reentrant boundary (`_nestedGuestCallbackDepth > 0`) and the no-sentinel case
keep the existing inline delivery as a fallback (they cannot cleanly unwind to a worker
loop). Single-owner is preserved: everything stays on the thread's one `GuestExecutionRunner`
/ `ExecutorActive` token.

## Parallel work

Three read-only sub-agents (crash-state classification; NativeWorker CLR-attach/GC-mode
audit; safe-delivery-machinery feasibility) + a live `SHARPEMU_UCO_FLIGHT` +
`SHARPEMU_LOG_GUEST_EXCEPTIONS` repro. The CLR-attach audit established why the nested
inline shape faults while top-level cooperative execution does not; the machinery audit
confirmed the block path already implements every primitive the fix reuses (yield-out,
continuation capture, same-worker top-level re-entry); the repro captured the decisive
`'kernel exception 0x1E safe point'` frame at the crash. None edited the tree.
