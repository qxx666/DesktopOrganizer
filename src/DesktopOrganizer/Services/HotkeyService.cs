using System;
using System.Windows.Interop;

namespace DesktopOrganizer.Services;

/// <summary>
/// 全局快捷键。用一个不可见的消息窗口接收 WM_HOTKEY。
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private const int HotkeyId = 0x0D01;

    /// <summary>WS_EX_TOOLWINDOW，避免消息窗口出现在任务栏 / Alt+Tab。</summary>
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    private HwndSource? _source;
    private bool _registered;

    public event Action? Pressed;

    public HotkeyService()
    {
        var parameters = new HwndSourceParameters("DesktopOrganizerHotkeyWindow")
        {
            Width = 0,
            Height = 0,
            PositionX = 0,
            PositionY = 0,
            WindowStyle = 0,
            ExtendedWindowStyle = WS_EX_TOOLWINDOW,
        };

        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            Pressed?.Invoke();
            handled = true;
        }

        return IntPtr.Zero;
    }

    /// <summary>注册快捷键。返回是否成功（可能被其它程序占用）。</summary>
    public bool Register(string gesture)
    {
        Unregister();

        if (_source == null || _source.Handle == IntPtr.Zero)
        {
            return false;
        }

        if (!TryParseGesture(gesture, out var modifiers, out var virtualKey))
        {
            return false;
        }

        try
        {
            _registered = NativeMethods.RegisterHotKey(
                _source.Handle, HotkeyId, modifiers | NativeMethods.MOD_NOREPEAT, virtualKey);
        }
        catch
        {
            _registered = false;
        }

        return _registered;
    }

    public void Unregister()
    {
        if (!_registered || _source == null || _source.Handle == IntPtr.Zero)
        {
            return;
        }

        try
        {
            NativeMethods.UnregisterHotKey(_source.Handle, HotkeyId);
        }
        catch
        {
            // ignore
        }

        _registered = false;
    }

    /// <summary>解析 "Ctrl+Alt+D" / "Win+Shift+F3" 这类快捷键字符串。</summary>
    public static bool TryParseGesture(string gesture, out uint modifiers, out uint virtualKey)
    {
        modifiers = 0;
        virtualKey = 0;

        if (string.IsNullOrWhiteSpace(gesture))
        {
            return false;
        }

        var parts = gesture.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return false;
        }

        foreach (var part in parts)
        {
            var token = part.ToUpperInvariant();

            switch (token)
            {
                case "CTRL":
                case "CONTROL":
                    modifiers |= NativeMethods.MOD_CONTROL;
                    break;

                case "ALT":
                    modifiers |= NativeMethods.MOD_ALT;
                    break;

                case "SHIFT":
                    modifiers |= NativeMethods.MOD_SHIFT;
                    break;

                case "WIN":
                case "WINDOWS":
                case "META":
                    modifiers |= NativeMethods.MOD_WIN;
                    break;

                default:
                    if (token.Length == 1 && token[0] >= 'A' && token[0] <= 'Z')
                    {
                        virtualKey = token[0];
                    }
                    else if (token.Length == 1 && token[0] >= '0' && token[0] <= '9')
                    {
                        virtualKey = token[0];
                    }
                    else if (token.StartsWith('F') && int.TryParse(token[1..], out var fn) && fn >= 1 && fn <= 24)
                    {
                        virtualKey = (uint)(0x70 + fn - 1);
                    }
                    else if (token == "SPACE")
                    {
                        virtualKey = 0x20;
                    }
                    else
                    {
                        return false;
                    }

                    break;
            }
        }

        // 必须有一个修饰键 + 一个主键，避免霸占普通按键
        return virtualKey != 0 && modifiers != 0;
    }

    public void Dispose()
    {
        Unregister();

        if (_source != null)
        {
            _source.RemoveHook(WndProc);
            _source.Dispose();
            _source = null;
        }
    }
}
