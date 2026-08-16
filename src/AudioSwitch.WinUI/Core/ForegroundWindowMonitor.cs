using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;

namespace AudioSwitch_WinUI;

/// <summary>
/// Receives foreground-window changes without polling. The WinEvent hook is
/// installed out of context, so it does not require a native helper DLL.
/// </summary>
public sealed class ForegroundWindowMonitor : IDisposable
{
    private const uint EventSystemForeground = 0x0003;
    private const uint WineventOutOfContext = 0x0000;
    private const uint WineventSkipOwnProcess = 0x0002;
    private const int ObjectIdWindow = 0;
    private const int ChildIdSelf = 0;

    private readonly DispatcherQueue dispatcherQueue;
    private readonly Action<int?> foregroundChanged;
    private readonly Action<string> log;
    private readonly WinEventDelegate callback;
    private nint hook;
    private bool disposed;

    public ForegroundWindowMonitor(
        DispatcherQueue dispatcherQueue,
        Action<int?> foregroundChanged,
        Action<string> log)
    {
        this.dispatcherQueue = dispatcherQueue;
        this.foregroundChanged = foregroundChanged;
        this.log = log;
        callback = WinEventProc;
    }

    public bool IsRunning => hook != 0;

    public bool Start()
    {
        if (disposed) throw new ObjectDisposedException(nameof(ForegroundWindowMonitor));
        if (hook != 0) return true;

        hook = SetWinEventHook(
            EventSystemForeground,
            EventSystemForeground,
            0,
            callback,
            0,
            0,
            WineventOutOfContext | WineventSkipOwnProcess);

        if (hook == 0)
        {
            log($"Foreground callback registration failed: Win32 error {Marshal.GetLastWin32Error()}.");
            return false;
        }

        log("Foreground callback enabled: SetWinEventHook(EVENT_SYSTEM_FOREGROUND).");
        return true;
    }

    public void Stop()
    {
        if (hook == 0) return;
        UnhookWinEvent(hook);
        hook = 0;
        log("Foreground callback disabled.");
    }

    private void WinEventProc(
        nint hookHandle,
        uint eventType,
        nint windowHandle,
        int objectId,
        int childId,
        uint eventThreadId,
        uint eventTime)
    {
        if (eventType != EventSystemForeground ||
            windowHandle == 0 ||
            objectId != ObjectIdWindow ||
            childId != ChildIdSelf)
        {
            return;
        }

        int? processId = null;
        if (GetWindowThreadProcessId(windowHandle, out uint nativeProcessId) != 0 && nativeProcessId <= int.MaxValue)
        {
            processId = (int)nativeProcessId;
        }

        dispatcherQueue.TryEnqueue(() => foregroundChanged(processId));
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Stop();
        GC.KeepAlive(callback);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWinEventHook(
        uint eventMin,
        uint eventMax,
        nint hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc,
        uint idProcess,
        uint idThread,
        uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(nint winEventHook);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint windowHandle, out uint processId);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void WinEventDelegate(
        nint hookHandle,
        uint eventType,
        nint windowHandle,
        int objectId,
        int childId,
        uint eventThreadId,
        uint eventTime);
}
