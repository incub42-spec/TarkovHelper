using System.Windows.Input;

namespace TarkovHelper.Services;

/// <summary>
/// Перевод между клавишами WPF, виртуальными кодами Windows (нужны для
/// RegisterHotKey) и понятными пользователю названиями.
/// </summary>
public static class HotkeyNames
{
    /// <summary>Виртуальный код клавиши для RegisterHotKey.</summary>
    public static uint ToVirtualKey(Key key) => (uint)KeyInterop.VirtualKeyFromKey(key);

    public static Key ToKey(uint virtualKey) => KeyInterop.KeyFromVirtualKey((int)virtualKey);

    /// <summary>
    /// Кнопка мыши? Такие коды нельзя отдать в RegisterHotKey — они ловятся
    /// через Raw Input. Левая и правая кнопки не поддерживаются намеренно:
    /// они заняты стрельбой и прицеливанием.
    /// </summary>
    public static bool IsMouseButton(uint virtualKey) =>
        virtualKey is Interop.NativeMethods.VK_MBUTTON
            or Interop.NativeMethods.VK_XBUTTON1
            or Interop.NativeMethods.VK_XBUTTON2;

    /// <summary>Код кнопки мыши по нажатию в WPF; 0 — кнопка не поддерживается.</summary>
    public static uint FromMouseButton(MouseButton button) => button switch
    {
        MouseButton.Middle => Interop.NativeMethods.VK_MBUTTON,
        MouseButton.XButton1 => Interop.NativeMethods.VK_XBUTTON1,
        MouseButton.XButton2 => Interop.NativeMethods.VK_XBUTTON2,
        _ => 0,
    };

    /// <summary>Название клавиши для интерфейса, например «F9» или «Num 5».</summary>
    public static string Describe(uint virtualKey)
    {
        switch (virtualKey)
        {
            case Interop.NativeMethods.VK_MBUTTON: return "Колёсико мыши";
            case Interop.NativeMethods.VK_XBUTTON1: return "Боковая мыши 1";
            case Interop.NativeMethods.VK_XBUTTON2: return "Боковая мыши 2";
        }

        var key = ToKey(virtualKey);
        return key switch
        {
            Key.None => $"код {virtualKey}",
            >= Key.NumPad0 and <= Key.NumPad9 => "Num " + (key - Key.NumPad0),
            Key.Oem3 => "Ё / ~",
            Key.OemMinus => "-",
            Key.OemPlus => "=",
            Key.OemOpenBrackets => "[",
            Key.OemCloseBrackets => "]",
            Key.OemQuestion => "/",
            Key.OemPipe => "\\",
            Key.OemComma => ",",
            Key.OemPeriod => ".",
            Key.OemSemicolon => ";",
            Key.OemQuotes => "'",
            Key.Space => "Пробел",
            Key.Insert => "Insert",
            Key.Delete => "Delete",
            Key.Home => "Home",
            Key.End => "End",
            Key.PageUp => "PageUp",
            Key.PageDown => "PageDown",
            _ => key.ToString(),
        };
    }

    /// <summary>
    /// Клавиши, которые нельзя назначить: модификаторы сами по себе бесполезны,
    /// а системные (Esc, Tab, Enter, Win) сломают управление игрой и Windows.
    /// </summary>
    public static bool IsForbidden(Key key) => key is
        Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or
        Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or
        Key.Escape or Key.Tab or Key.Enter or Key.System or
        Key.CapsLock or Key.NumLock or Key.None;
}
