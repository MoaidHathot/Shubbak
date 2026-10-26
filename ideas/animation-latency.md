# Intermittent animation latency

Measured on 2026-10-26, starting with the 0.11.0 daemon and a clean `main`.
The reported case was removing one of two tiled windows: the survivor sometimes
expanded sluggishly, then later attempts were fast again.

## Focus hand-off

The running daemon targeted 60 Hz. Its frame interval was median 17.37 ms,
p99 53.50 ms; focus hand-off was median 12.14 ms, p99 96.56 ms, maximum
151.65 ms. A recorded removal spent 121.8 ms in focus, and the motion report
counted seven of fourteen expected frames over 214 ms. These are observations of
that run, not performance guarantees. In particular, the old motion timestamps
included work before the first frame was actually posted.

`ApplyLayout` calls `WindowActions.Focus` on the frame-driving thread. The latter
attached that thread to the foreign foreground's input queue before asking for
activation. A new controlled test window, in a separate process, acknowledges
when its UI thread starts a bounded two-second pause. The existing same-process
focus-sink tests could not exercise the attachment branch.

Temporary phase instrumentation of the old implementation measured:

| Busy foreground, old path | Time |
|---|---:|
| AttachThreadInput(true) | 0.42 ms |
| SetForegroundWindow | 2012.62 ms |
| AttachThreadInput(false) | 0.37 ms |

Removing the attachment, while retaining the existing permission fallback, gave:

| Separate input queues | Request time | Eventual foreground |
|---|---:|---|
| Busy foreground | 0.62 ms | requested window |
| Busy target | 0.70 ms | requested window after it resumed |

The regression asserts that the request does not wait out the foreign thread's
pause and that the requested window really receives focus. It also checks repeated
cross-process transitions and zero managed allocation in warmed focus requests.
The temporary instrumentation is not shipped. No thread, queue, polling or privilege
grant was added to production; the keyboard callback is untouched.

Reproduce with the user's daemon stopped:

```powershell
dotnet test tests/Shubbak.Native.Tests -c Release --filter FullyQualifiedName~FocusLatencyTests --logger "console;verbosity=detailed"
```

The whole Release solution passed before changes (15 test projects). Native and
end-to-end tests serialize desktop access and isolate their daemons' state from
the user's session.
