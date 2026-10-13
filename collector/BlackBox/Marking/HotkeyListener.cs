using System.Runtime.InteropServices;
using BlackBox.Core;

namespace BlackBox.Marking;

/// <summary>
/// 전용 스레드에서 RegisterHotKey(Ctrl+Shift+F12, hWnd=NULL=스레드 큐) + GetMessage 루프.
/// 등록 실패(키 선점, 디버거 attach 등)해도 콘솔 'm' 입력 마킹은 항상 동작한다.
/// </summary>
public sealed class HotkeyListener : IDisposable
{
    private const int HotkeyId = 0xB10C;
    private const uint ModControl = 0x0002, ModShift = 0x0004, ModNoRepeat = 0x4000;
    private const uint VkF12 = 0x7B;
    private const uint WmHotkey = 0x0312, WmQuit = 0x0012;

    private Thread? _thread;
    private uint _threadId;

    public SourceStatus Start(Action onHotkey)
    {
        var ready = new TaskCompletionSource<SourceStatus>();
        _thread = new Thread(() =>
        {
            if (!RegisterHotKey(IntPtr.Zero, HotkeyId, ModControl | ModShift | ModNoRepeat, VkF12))
            {
                ready.SetResult(SourceStatus.Unavailable("hotkey",
                    $"RegisterHotKey(Ctrl+Shift+F12) 실패, GetLastError={Marshal.GetLastWin32Error()} — 콘솔 'm' 입력으로 마킹 가능"));
                return;
            }
            _threadId = GetCurrentThreadId();
            ready.SetResult(SourceStatus.Ok("hotkey", "Ctrl+Shift+F12 등록됨"));

            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                if (msg.message == WmHotkey && (int)msg.wParam == HotkeyId)
                    onHotkey();
            }
            UnregisterHotKey(IntPtr.Zero, HotkeyId);
        })
        { IsBackground = true, Name = "hotkey-listener" };
        _thread.Start();
        return ready.Task.Result;
    }

    public void Dispose()
    {
        if (_threadId != 0)
            PostThreadMessage(_threadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
        _thread?.Join(1000);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out Msg lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
