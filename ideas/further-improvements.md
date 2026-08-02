# Further improvements

Things worth doing, and things deliberately not done. Written down so the reasoning
survives the conversation it came from.

---

## Input latency: the keyboard hook

### What the problem was

A `WH_KEYBOARD_LL` callback runs on **the thread that installed the hook**, and until
that callback returns, the keystroke has not reached the focused application. Windows
allows it `LowLevelHooksTimeout` — 300 ms by default — before giving up and silently
unhooking.

Shubbak installed the hook on its own message loop. That put every keystroke the user
typed, in every application on the machine, behind whatever the loop was doing:
applying a layout, talking to the shell over COM, writing a log line.

The failure mode is unusually cruel. Nothing about the symptom points at a window
manager — it was reported as *"the keyboard feels sluggish"*, and it was sluggish in
Notepad, in the browser, everywhere. No test would ever have said that.

### What was done

**1. A dedicated thread for the hook.** It installs the hook, pumps messages, and does
nothing else. The ring buffer was already the handoff to the consumer, so only the
producer moved. The thread is named `Shubbak keyboard hook` so it is identifiable in a
hang dump, and runs at `AboveNormal` — a late keystroke is one the application has not
received, but starving the rest of the system to service a hook would be its own bug.

**2. Log writing moved off the caller.** With a file open it was a locked, `AutoFlush`
disk write per line, plus a console write that can stall for tens of milliseconds when
the terminal is busy. The thread producing most of those lines was the message loop. A
background writer now drains a bounded queue and flushes once per drain.

**3. Entries are formatted on the writer thread**, not the caller — one string
allocation per line removed from the message loop.

**4. `GCLatencyMode.SustainedLowLatency`.** A collection suspends *every* thread in the
process, including the hook thread. This does not eliminate pauses; it shortens them.

**5. `LogInterpolatedStringHandler`.** See below.

**6. No settling delay.** An earlier attempt deferred new windows by 150 ms to let
shell flyouts come and go. It was removed: it did not work (the Win+Space flyout
outlives 150 ms) and it was *visible* — windows were noticeably unmanaged, then
managed. Neither GlazeWM nor komorebi does anything like it.

### Guards against regression

- `InputLatencyTests` — asserts the hook is on a named, dedicated thread; that start
  and stop are clean; that only one source can be active; and that draining an idle
  source is cheap.
- `LogTests.WritingDoesNotBlockTheCallingThread`
- `LogAllocationTests` — disabled log calls format nothing.

All deliberately loose. They are not benchmarks and must not fail on a busy build
agent; they exist to catch the hook moving back onto a shared thread, or work creeping
into the callback.

---

## Logging on the hot path

### Is there logging in the hook callback?

**No, and there must not be.** `KeyboardSource.Callback` does four `GetAsyncKeyState`
reads, one packed-int dictionary lookup, and a wait-free ring enqueue. No allocation,
no locks, no I/O. Anything added there is paid by every keystroke on the machine.

Logging happens one step later, on the message loop, when `DrainKeyboard` picks the
event up. That is the right place: the keystroke has already been passed on or
swallowed by then, so the user is no longer waiting.

### Is the logging important?

Yes, and it has earned its keep repeatedly. Two examples from this project:

- The Win+Space language switcher was identified only because the log recorded
  `managed 0x1D0076 "Input Flyout" (explorer) [Shell_InputSwitchTopLevelWindow]`. The
  window is destroyed within 100 ms, so nothing can be pointed at it to ask what it
  was. The **class** was added to that line specifically for this.
- `startup recovery: revived 0 of 6` disproved a theory about which component was
  resurrecting the Settings window, in one line.

### Can it be cheaper without losing diagnostics?

Partly, and the limit is interesting.

`Log.Debug(category, $"...")` looks free when debug logging is off. It is not: the
interpolated string is built by the caller, before the call. The fix is an
[`[InterpolatedStringHandler]`](../src/Shubbak.Core/Diagnostics/LogInterpolatedStringHandler.cs),
which lets the compiler skip every append until the level has been checked. Guarding
by hand with `Log.IsEnabled` does the same and is easy to forget; the handler cannot
be.

**The limit is the ring buffer.** Shubbak keeps recent entries in memory *one level
more verbose than the sink*, so `shubbak diagnose` can explain something that has
already happened without the user having had the foresight to enable logging first —
which is the single most common reason bug reports are unactionable.

So at the default `Information` level, `Log.Debug` messages are **still built**,
because the ring wants them. Only `Trace` is genuinely free there.

That is the right trade, and `LogAllocationTests.ADebugCallIsStillBuiltForTheRingAtInformation`
pins it deliberately — anyone tempted to optimise it away should fail that test first
and read this paragraph.

### If it ever needs to be cheaper still

Options, roughly in order of preference:

1. **Move the noisiest messages from Debug to Trace.** Free, and puts them outside the
   ring at the default level. The per-keystroke binding message is the obvious
   candidate.
2. **Store deferred entries in the ring** — keep the arguments and format only when
   `diagnose` runs. Boxing the arguments allocates too, so this is only a win for
   messages with no holes or with pre-boxed values. Probably not worth it.
3. **Make the ring level configurable** so someone chasing latency can turn it off
   entirely, accepting a poorer diagnostic report.

---

## The komorebi approach: a separate hotkey process

komorebi runs **no keyboard hook at all**. Keybindings are handled by `whkd`, a
separate process. Nothing the window manager does can affect typing — not a full GC
pause, not a deadlock, not a crash.

GlazeWM, by contrast, installs its hook on the dispatcher's event-loop thread
(`packages/wm-platform/src/platform_impl/windows/keyboard_hook.rs`), which is the
design Shubbak started with. **Shubbak's dedicated thread is already stricter than
GlazeWM's.**

### What is left on the table

| | Shubbak (dedicated thread) | komorebi (separate process) |
|---|---|---|
| Message loop blocks | no effect | no effect |
| GC pause | **suspends the hook thread** | no effect |
| WM deadlock or crash | hook dies with it | unaffected |
| Thread starvation | possible | separate process |

The GC row is the only one that bites in practice, and `SustainedLowLatency` shortens
those pauses rather than removing them.

### Assessment: not yet

It costs a second executable, an IPC hop on the hot path, and a much worse setup
story — the reason komorebi users have to learn about `whkd` at all.

**The evidence that would justify it** is occasional hitches that survive everything
above. If that happens, the narrower fix comes first:

> Move binding *resolution* off the hook thread entirely, so the callback compares a
> packed int against an immutable frozen table and does nothing else. Today it calls a
> delegate into `BindingTable.IsBound`, which reads mutable state that config reload
> can swap underneath it.

That is a much smaller change than a second process, and it removes the last piece of
non-trivial work from the callback.

---

## Unrelated ideas worth keeping

- **Offline `shubbak diagnose`.** It currently requires the running daemon, which is
  backwards: a report is most wanted when the window manager has died. It should fall
  back to environment, binary identity, config-as-parsed and the on-disk log, clearly
  marked as having no live state.
- **`VisualStyle.Opacity` is dead.** Declared, never read. Either wire it up or remove
  it.
- **Scroll handlers are modelled but unreachable.** `VisualNode.OnScrollUp` and
  `OnScrollDown` exist; the bar's window procedure never handles `WM_MOUSEWHEEL`.
  Scrolling the workspace list to switch workspaces is the obvious use.
