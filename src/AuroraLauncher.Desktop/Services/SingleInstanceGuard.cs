using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using AuroraCore.Services;

namespace AuroraLauncher.Desktop.Services;

/// <summary>
/// Round-74: keeps one launcher per data root alive, and brings the existing
/// window forward when the user double-clicks the EXE again.
///
/// Two mechanisms, on purpose:
///  - a named <see cref="Mutex"/> answers "am I the first?" at startup;
///  - a named <see cref="EventWaitHandle"/> lets later processes *ask* the first
///    one to show itself. A mutex alone can only refuse to start, which looks
///    like "double-click does nothing".
/// </summary>
public static class SingleInstanceGuard
{
    private static Mutex? _mutex;
    private static EventWaitHandle? _activateEvent;
    private static EventWaitHandle? _ackEvent;
    private static Thread? _listener;

    /// <summary>True when this process owns the instance. The listener runs on
    /// a background thread until the process exits.</summary>
    public static bool TryAcquire(string rootDirectory, Func<bool> onActivate)
    {
        try
        {
            _mutex = new Mutex(initiallyOwned: true, SingleInstancePolicy.MutexName(rootDirectory), out var createdNew);
            if (!createdNew)
            {
                _mutex.Dispose();
                _mutex = null;
                return false;
            }

            _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset,
                SingleInstancePolicy.ActivationEventName(rootDirectory));
            _ackEvent = new EventWaitHandle(false, EventResetMode.AutoReset,
                SingleInstancePolicy.ActivationAckEventName(rootDirectory));
            _listener = new Thread(() =>
            {
                while (true)
                {
                    try { _activateEvent.WaitOne(); }
                    catch { return; }

                    // Round-76: the ack means "a window is on screen now", NOT
                    // "a process exists". Round 75 acknowledged even when showing
                    // failed, so the second instance exited while nothing appeared
                    // — the "double-click does nothing" report, again.
                    var shown = false;
                    try { shown = onActivate(); }
                    catch (Exception ex)
                    {
                        AuroraCore.Utils.Logger.Instance.Warn("App", "唤起窗口失败: " + ex.Message);
                    }
                    if (shown)
                    {
                        try { _ackEvent.Set(); } catch { }
                    }
                    else
                    {
                        AuroraCore.Utils.Logger.Instance.Warn("App",
                            "唤起窗口失败：没有可显示的窗口，本次不回应答（调用方会自己开新窗口）。");
                    }
                }
            })
            { IsBackground = true, Name = "aurora-activate-listener" };
            _listener.Start();
            return true;
        }
        catch (Exception ex)
        {
            // A failed mutex must never block startup: rather two windows than none.
            AuroraCore.Utils.Logger.Instance.Warn("App", "单实例检查不可用，继续启动: " + ex.Message);
            return true;
        }
    }

    /// <summary>Called by the SECOND process: wake the first one and wait for its
    /// acknowledgement. Returns false when nobody answered, which means the caller
    /// should start normally instead of exiting into a void (round-74 bug: a
    /// windowless process kept the mutex, so every double-click vanished).</summary>
    public static bool SignalExisting(string rootDirectory, TimeSpan waitForAck)
    {
        var signalled = false;
        try
        {
            if (EventWaitHandle.TryOpenExisting(SingleInstancePolicy.ActivationEventName(rootDirectory), out var handle))
            {
                using (handle) { handle.Set(); }
                signalled = true;
            }
        }
        catch { /* fall through to the window-handle fallback */ }

        if (signalled && WaitForAck(rootDirectory, waitForAck))
        {
            AuroraCore.Utils.Logger.Instance.Info("App",
                SingleInstancePolicy.SecondInstanceNote(rootDirectory) + "（已发送唤醒信号并收到应答）");
            return true;
        }

        if (signalled)
        {
            AuroraCore.Utils.Logger.Instance.Warn("App",
                SingleInstancePolicy.StaleInstanceNote(rootDirectory));
            return false;
        }

        // Fallback: the first instance may predate the event (older build) or the
        // event name may be inaccessible. Bring its main window up directly.
        try
        {
            foreach (var process in Process.GetProcessesByName("AuroraLauncher"))
            {
                if (process.Id == Environment.ProcessId) continue;
                var handle = process.MainWindowHandle;
                if (handle == IntPtr.Zero) continue;
                ShowWindow(handle, SwRestore);
                SetForegroundWindow(handle);
                AuroraCore.Utils.Logger.Instance.Info("App", SingleInstancePolicy.SecondInstanceNote(rootDirectory) + "（已用窗口句柄唤起）");
                return true;
            }
        }
        catch { /* nothing else to try */ }

        AuroraCore.Utils.Logger.Instance.Warn("App", SingleInstancePolicy.StaleInstanceNote(rootDirectory));
        return false;
    }

    /// <summary>Waits for the ack, tolerating the race where the ack arrives just
    /// before we start waiting (the handle stays signalled until read).</summary>
    private static bool WaitForAck(string rootDirectory, TimeSpan waitForAck)
    {
        try
        {
            if (!EventWaitHandle.TryOpenExisting(SingleInstancePolicy.ActivationAckEventName(rootDirectory), out var ack))
                return false;
            using (ack)
            {
                return ack.WaitOne(waitForAck);
            }
        }
        catch { return false; }
    }

    /// <summary>Round-76: hand the mutex back the moment the app is done, instead of
    /// leaving it to process teardown — a lingering process must not block the next
    /// double-click.</summary>
    public static void Release()
    {
        try
        {
            _listener = null;
            _activateEvent?.Dispose();
            _activateEvent = null;
            _ackEvent?.Dispose();
            _ackEvent = null;
            if (_mutex != null)
            {
                try { _mutex.ReleaseMutex(); } catch { /* not owned by this thread */ }
                _mutex.Dispose();
                _mutex = null;
            }
        }
        catch { }
    }

    public const int SwRestore = 9;

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    public static readonly IntPtr HwndTopmost = new(-1);
    public static readonly IntPtr HwndNotTopmost = new(-2);
    public const uint SwpNoSize = 0x0001, SwpNoMove = 0x0002, SwpNoActivate = 0x0010;
}
