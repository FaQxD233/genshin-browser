using System.Windows.Input;

namespace GenshinBrowser.Models;

/// <summary>一组热键：主键 + 修饰键。</summary>
public readonly record struct HotkeyBinding(Key Key, ModifierKeys Modifiers);
