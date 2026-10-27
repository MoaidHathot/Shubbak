using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Shubbak.Native;
using Shubbak.Ipc;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Shubbak.InputProbe;

/// <summary>
/// Tags only synthetic F24 events and times SendInput to an actual foreground
/// window's message handler. The production daemon is in another process.
/// </summary>
internal sealed class InputRun : IDisposable
{
    private const ulong Marker = 0x53484B5000000000;
    private static InputRun? s_current;
    private readonly long[] _sent;
    private readonly long[] _received;
    private readonly double[] _sendCalls;
    private readonly int _warmup;
    private readonly int _intervalUs;
    private readonly int _burst;
    private readonly string _label;
    private readonly bool _bound;
    private readonly ushort _key;
    private readonly string? _instance;
    private readonly string? _rawOutput;
    private readonly CancellationTokenSource _signalStop = new();
    private IpcClient? _signals;
    private Task? _signalLoop;
    private int _leakedBound;
    private bool _disposed;
    private volatile bool _stopping;
    private HWND _window;
    private Exception? _failure;
    private int _receivedCount, _duplicates, _outOfOrder, _wrongEdges, _lastIndex = -1;
    private long _allocatedBefore, _allocatedAfter;
    private double _seconds;
    private int _sendCount;

    private InputRun(string[] args)
    {
        int samples = Program.Number(args, "--samples", 20_000, 2, 1_000_000);
        _warmup = Program.Number(args, "--warmup", 1000, 0, 100_000);
        if ((samples & 1) != 0 || (_warmup & 1) != 0) throw new ArgumentException("Sample counts must contain complete down/up pairs.");
        _sent = new long[samples + _warmup];
        _received = new long[_sent.Length];
        _sendCalls = new double[_sent.Length / 2];
        _intervalUs = Program.Number(args, "--interval-us", 2000, 0, 1_000_000);
        _burst = Program.Number(args, "--burst", 1, 1, 64);
        _label = Program.Text(args, "--label") ?? "input-delivery";
        _bound = args.Contains("--bound", StringComparer.Ordinal);
        _key = _bound ? (ushort)0x86 : (ushort)0x87;
        _instance = Program.Text(args, "--instance");
        _rawOutput = Program.Text(args, "--raw-output");
        if (_rawOutput is not null && File.Exists(_rawOutput)) throw new IOException("Raw output already exists.");
        if (_bound && string.IsNullOrEmpty(_instance)) throw new ArgumentException("Bound measurement needs --instance.");
    }

    internal static int Run(string[] args)
    {
        using var run = new InputRun(args);
        return run.Run();
    }

    private unsafe int Run()
    {
        MonitorSource.EnableDpiAwareness();
        const string ClassName = "ShubbakInputReceiver";
        fixed (char* name = ClassName)
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                lpfnWndProc = &WindowProc,
                lpszClassName = name,
            };
            if (PInvoke.RegisterClassEx(in wc) == 0) throw new InvalidOperationException("Could not register receiver.");
        }

        s_current = this;
        _window = PInvoke.CreateWindowEx(0, ClassName, "Shubbak input latency receiver",
            WINDOW_STYLE.WS_OVERLAPPEDWINDOW, 80, 80, 640, 360,
            HWND.Null, (SafeHandle?)null, (SafeHandle?)null, null);
        if (_window.IsNull) throw new InvalidOperationException("Could not create receiver.");

        using Process process = Process.GetCurrentProcess();
        TimeSpan cpu = process.TotalProcessorTime;
        var sender = new Thread(Inject) { Name = "Input probe injector", IsBackground = true };
        try
        {
            if (_bound) StartSignalReceiver();
            PInvoke.ShowWindow(_window, SHOW_WINDOW_CMD.SW_SHOWNOACTIVATE);
            if (!WindowActions.Focus((nint)_window.Value)) throw new InvalidOperationException("Receiver could not request foreground.");
            sender.Start();

            while (PInvoke.GetMessage(out MSG message, default, 0, 0))
            {
                if (message.message is PInvoke.WM_QUIT or PInvoke.WM_CLOSE) break;
                PInvoke.TranslateMessage(in message);
                PInvoke.DispatchMessage(in message);
            }

            _stopping = true;
            if (!sender.Join(TimeSpan.FromSeconds(10))) throw new TimeoutException("Injector did not stop.");
            StopSignals();
            if (_failure is not null) throw new InvalidOperationException("Input measurement aborted.", _failure);

            var delivery = new List<double>(_sent.Length - _warmup);
            int missing = 0;
            for (int i = 0; i < _sent.Length; i++)
            {
                if (_bound && (i & 1) != 0) continue;
                if (_received[i] == 0) { missing++; continue; }
                if (i >= _warmup) delivery.Add(Stopwatch.GetElapsedTime(_sent[i], _received[i]).TotalMilliseconds);
            }
            int warmCalls = Math.Min((_warmup + _burst * 2 - 1) / (_burst * 2), _sendCount);
            process.Refresh();
            var report = new ProbeReport
            {
                Label = _label, Measurement = _bound ? "key-to-signal-notification" : "window-message", Seconds = _seconds,
                EventsPerSecond = Expected / _seconds,
                Sent = Expected, Received = _receivedCount, Missing = missing,
                Duplicates = _duplicates, OutOfOrder = _outOfOrder, WrongEdges = _wrongEdges,
                LeakedBoundEvents = _leakedBound,
                AllocatedBytes = _allocatedAfter - _allocatedBefore,
                CpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds,
                PrivateMiB = process.PrivateMemorySize64 / 1048576.0,
                Delivery = Distribution.Of([.. delivery]),
                SendCall = Distribution.Of(_sendCalls.AsSpan(warmCalls, _sendCount - warmCalls).ToArray()),
            };
            if (_rawOutput is not null)
            {
                using var raw = new StreamWriter(new FileStream(_rawOutput, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { NewLine = "\n" };
                raw.WriteLine("index,sent_qpc,received_qpc,warmup");
                for (int i = 0; i < _sent.Length; i += _bound ? 2 : 1)
                    raw.WriteLine(FormattableString.Invariant($"{i},{_sent[i]},{_received[i]},{(i < _warmup ? 1 : 0)}"));
            }
            Program.Report(report);
            return missing == 0 && _duplicates == 0 && _outOfOrder == 0 && _wrongEdges == 0 && _leakedBound == 0 ? 0 : 2;
        }
        finally
        {
            _stopping = true;
            PInvoke.DestroyWindow(_window);
            _window = HWND.Null;
            s_current = null;
        }
    }

    private void StopSignals()
    {
        _signalStop.Cancel();
        if (_signals is not null) _signals.DisposeAsync().AsTask().GetAwaiter().GetResult();
        if (_signalLoop is not null && !_signalLoop.Wait(TimeSpan.FromSeconds(5)))
            _failure ??= new TimeoutException("Signal receiver did not stop.");
        _signals = null;
        _signalLoop = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopSignals();
        _signalStop.Dispose();
    }

    private int Expected => _bound ? _sent.Length / 2 : _sent.Length;

    private void StartSignalReceiver()
    {
        _signals = new IpcClient { PipeName = IpcProtocol.PipeNameForInstance(_instance!) };
        _signals.ConnectAsync(TimeSpan.FromSeconds(5), _signalStop.Token).GetAwaiter().GetResult();
        _signals.BeginSubscriptionAsync("signal", _signalStop.Token).GetAwaiter().GetResult();
        _signalLoop = Task.Run(async () =>
        {
            int index = 0;
            try
            {
                await foreach (IpcEvent message in _signals.ReadEventsAsync(_signalStop.Token).ConfigureAwait(false))
                {
                    long now = Stopwatch.GetTimestamp();
                    if (message.Topic != "signal" || SignalPayload.Parse(message.Data)?.IsFor("input-probe") != true) continue;
                    if (index >= _received.Length || Volatile.Read(ref _sent[index]) == 0)
                        throw new InvalidOperationException("Unexpected bound-key notification.");
                    _received[index] = now;
                    index += 2;
                    Interlocked.Increment(ref _receivedCount);
                }
            }
            catch (OperationCanceledException) when (_signalStop.IsCancellationRequested) { }
            catch (Exception ex) when (_signalStop.IsCancellationRequested && ex is IOException or ObjectDisposedException) { }
            catch (Exception ex)
            {
                _failure = ex;
                PInvoke.PostMessage(_window, PInvoke.WM_CLOSE, default, default);
            }
        });
    }

    private unsafe void Inject()
    {
        try
        {
            long focusDeadline = Stopwatch.GetTimestamp();
            while (PInvoke.GetForegroundWindow() != _window)
            {
                if (Stopwatch.GetElapsedTime(focusDeadline).TotalSeconds > 5) throw new TimeoutException("Receiver never became foreground.");
                Thread.Sleep(5);
            }
            // A held physical modifier would change what our synthetic key means.
            foreach (int key in new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C })
                if ((PInvoke.GetAsyncKeyState(key) & 0x8000) != 0) throw new InvalidOperationException("A modifier is held; measurement is not isolated.");

            using var timer = new Pacer();
            Span<INPUT> inputs = stackalloc INPUT[_burst * 2];
            long start = Stopwatch.GetTimestamp();
            for (int first = 0; first < _sent.Length;)
            {
                if (_stopping) throw new OperationCanceledException("Receiver was closed during injection.");
                if (PInvoke.GetForegroundWindow() != _window) throw new InvalidOperationException("Foreground changed during input measurement.");
                int count = Math.Min(inputs.Length, _sent.Length - first);
                for (int j = 0; j < count; j++)
                {
                    int index = first + j;
                    inputs[j] = new INPUT { type = INPUT_TYPE.INPUT_KEYBOARD };
                    inputs[j].Anonymous.ki.wVk = (VIRTUAL_KEY)_key;
                    inputs[j].Anonymous.ki.dwFlags = (index & 1) != 0 ? KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP : 0;
                    inputs[j].Anonymous.ki.dwExtraInfo = (nuint)(Marker | (uint)index);
                    Volatile.Write(ref _sent[index], Stopwatch.GetTimestamp());
                }

                long sentAt = Stopwatch.GetTimestamp();
                uint inserted = PInvoke.SendInput(inputs[..count], sizeof(INPUT));
                _sendCalls[_sendCount++] = Stopwatch.GetElapsedTime(sentAt).TotalMilliseconds;
                if (inserted != count) throw new InvalidOperationException($"SendInput accepted {inserted}/{count} events.");
                first += count;
                if (first / (_bound ? 2 : 1) - Volatile.Read(ref _receivedCount) > 1024) throw new InvalidOperationException("Receiver fell over 1024 events behind.");
                if (_intervalUs > 0) timer.Wait(_intervalUs);
            }

            long draining = Stopwatch.GetTimestamp();
            while (Volatile.Read(ref _receivedCount) < Expected && Stopwatch.GetElapsedTime(draining).TotalSeconds < 5)
                Thread.Sleep(1);
            _seconds = Stopwatch.GetElapsedTime(start).TotalSeconds;
        }
        catch (Exception ex)
        {
            _failure = ex;
        }
        finally
        {
            // Every batch contains full pairs; this also releases a partially accepted
            // final batch without injecting text or a command into another application.
            var release = new INPUT { type = INPUT_TYPE.INPUT_KEYBOARD };
            release.Anonymous.ki.wVk = (VIRTUAL_KEY)_key;
            release.Anonymous.ki.dwFlags = KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP;
            PInvoke.SendInput(new ReadOnlySpan<INPUT>(in release), sizeof(INPUT));
            PInvoke.PostMessage(_window, PInvoke.WM_CLOSE, default, default);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static LRESULT WindowProc(HWND hwnd, uint message, WPARAM wParam, LPARAM lParam)
    {
        if (s_current is { } run && message is PInvoke.WM_KEYDOWN or PInvoke.WM_KEYUP or PInvoke.WM_SYSKEYDOWN or PInvoke.WM_SYSKEYUP)
        {
            ulong tag = unchecked((ulong)PInvoke.GetMessageExtraInfo().Value);
            if ((tag & 0xFFFFFFFF00000000) == Marker && wParam.Value == run._key)
            {
                if (run._bound)
                {
                    Interlocked.Increment(ref run._leakedBound);
                    return new LRESULT(0);
                }
                uint index = (uint)tag;
                if (index < run._received.Length)
                {
                    long now = Stopwatch.GetTimestamp();
                    if (index == run._warmup) run._allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
                    if (run._received[index] != 0) run._duplicates++;
                    if (index != run._lastIndex + 1) run._outOfOrder++;
                    bool up = message is PInvoke.WM_KEYUP or PInvoke.WM_SYSKEYUP;
                    if (up != ((index & 1) != 0)) run._wrongEdges++;
                    run._lastIndex = (int)index;
                    run._received[index] = now;
                    if (index == run._received.Length - 1) run._allocatedAfter = GC.GetAllocatedBytesForCurrentThread();
                    Interlocked.Increment(ref run._receivedCount);
                }
                return new LRESULT(0);
            }
        }
        return PInvoke.DefWindowProc(hwnd, message, wParam, lParam);
    }
}
