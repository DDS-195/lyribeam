using System.ComponentModel;
using System.Runtime.InteropServices;
namespace LyricBridge;

internal static class ChineseText
{
    public static string Simplify(string text)
    {
        if (string.IsNullOrEmpty(text) || !text.Any(c => c >= '\u3400' && c <= '\u9FFF')) return text;
        // Preserve Japanese lines containing kana; this is character conversion, not translation.
        if (text.Any(c => c >= '\u3040' && c <= '\u30FF' || c >= '\uFF66' && c <= '\uFF9F')) return text;
        const uint simplified = 0x02000000;
        int size = LCMapStringEx("zh-CN", simplified, text, text.Length, null, 0, 0, 0, 0);
        if (size == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        var buffer = new char[size];
        int written = LCMapStringEx("zh-CN", simplified, text, text.Length, buffer, buffer.Length, 0, 0, 0);
        if (written == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        return new string(buffer, 0, written);
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int LCMapStringEx(string locale, uint flags, string source, int length, [Out] char[]? destination, int capacity, nint version, nint reserved, nint sort);
}
