using System;
using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace Lumi.Services;

/// <summary>
/// Registers a system-wide hotkey on Windows and fires an event when pressed.
/// Uses RegisterHotKey + Win32Properties WndProc hook.
/// </summary>
public sealed class GlobalHotkeyService : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const int HOTKEY_ID = 0x4C4D; // "LM"

    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_WIN = 0x0008;
    private const uint MOD_NOREPEAT = 0x4000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private bool _registered;
    private IntPtr _hwnd;
    private Window? _window;

    public event Action? HotkeyPressed;

    /// <summary>Install the WndProc hook on the window. Call once after the window is shown.</summary>
    public void Attach(Window window)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        _window = window;
        Win32Properties.AddWndProcHookCallback(window, WndProcHook);
    }

    /// <summary>Register (or re-register) the global hotkey.</summary>
    public bool Register(string hotkeyString)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return false;

        Unregister();

        if (string.IsNullOrWhiteSpace(hotkeyString) || _window is null)
            return false;

        if (!TryParseHotkey(hotkeyString, out var modifiers, out var vk))
            return false;

        var handle = _window.TryGetPlatformHandle();
        if (handle is null) return false;

        _hwnd = handle.Handle;
        _registered = RegisterHotKey(_hwnd, HOTKEY_ID, modifiers | MOD_NOREPEAT, vk);
        return _registered;
    }

    public void Unregister()
    {
        if (_registered && _hwnd != IntPtr.Zero)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                UnregisterHotKey(_hwnd, HOTKEY_ID);
            _registered = false;
        }
    }

    private IntPtr WndProcHook(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HOTKEY_ID)
        {
            HotkeyPressed?.Invoke();
            handled = true;
        }
        return IntPtr.Zero;
    }

    /// <summary>Parse a hotkey string like "Ctrl+Alt+Space" into Win32 modifiers and VK code.</summary>
    public static bool TryParseHotkey(string hotkeyString, out uint modifiers, out uint vk)
    {
        modifiers = 0;
        vk = 0;

        var parts = hotkeyString.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2) return false; // Need at least one modifier + key

        for (int i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i].ToUpperInvariant())
            {
                case "CTRL" or "CONTROL": modifiers |= MOD_CONTROL; break;
                case "ALT": modifiers |= MOD_ALT; break;
                case "SHIFT": modifiers |= MOD_SHIFT; break;
                case "WIN": modifiers |= MOD_WIN; break;
                default: return false;
            }
        }

        if (modifiers == 0) return false;

        vk = KeyNameToVk(parts[^1]);
        return vk != 0;
    }

    private static uint KeyNameToVk(string keyName)
    {
        // Single letter
        if (keyName.Length == 1 && char.IsAsciiLetter(keyName[0]))
            return (uint)char.ToUpperInvariant(keyName[0]);

        // Single digit
        if (keyName.Length == 1 && char.IsDigit(keyName[0]))
            return (uint)keyName[0];

        return keyName.ToUpperInvariant() switch
        {
            "SPACE" => 0x20,
            "ENTER" or "RETURN" => 0x0D,
            "TAB" => 0x09,
            "ESCAPE" or "ESC" => 0x1B,
            "BACKSPACE" or "BACK" => 0x08,
            "INSERT" or "INS" => 0x2D,
            "DELETE" or "DEL" => 0x2E,
            "HOME" => 0x24,
            "END" => 0x23,
            "PAGEUP" or "PGUP" => 0x21,
            "PAGEDOWN" or "PGDN" => 0x22,
            "UP" => 0x26,
            "DOWN" => 0x28,
            "LEFT" => 0x25,
            "RIGHT" => 0x27,
            "F1" => 0x70, "F2" => 0x71, "F3" => 0x72, "F4" => 0x73,
            "F5" => 0x74, "F6" => 0x75, "F7" => 0x76, "F8" => 0x77,
            "F9" => 0x78, "F10" => 0x79, "F11" => 0x7A, "F12" => 0x7B,
            "OEMTILDE" => 0xC0,
            "OEMMINUS" => 0xBD,
            "OEMPLUS" => 0xBB,
            "OEMPERIOD" => 0xBE,
            "OEMCOMMA" => 0xBC,
            _ => 0
        };
    }

    public void Dispose()
    {
        Unregister();
    }
}
