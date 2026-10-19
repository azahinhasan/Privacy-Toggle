using System.Text.Json.Serialization;
using System.Windows.Input;

namespace PrivacyToggle.Models;

/// <summary>
/// A key combination such as Ctrl+Alt+M. Modifiers are "Ctrl", "Alt", "Shift", "Win";
/// Key is a WPF <see cref="System.Windows.Input.Key"/> name.
/// </summary>
public sealed class HotkeySetting
{
    public List<string> Modifiers { get; set; } = new();
    public string Key { get; set; } = "";

    public static HotkeySetting Default => new() { Modifiers = new() { "Ctrl", "Alt" }, Key = "M" };

    [JsonIgnore] public bool Ctrl => Has("Ctrl");
    [JsonIgnore] public bool Alt => Has("Alt");
    [JsonIgnore] public bool Shift => Has("Shift");
    [JsonIgnore] public bool Win => Has("Win");

    [JsonIgnore]
    public Key WpfKey => Enum.TryParse<Key>(Key, true, out var k) ? k : System.Windows.Input.Key.None;

    [JsonIgnore]
    public int VirtualKey => WpfKey == System.Windows.Input.Key.None ? 0 : KeyInterop.VirtualKeyFromKey(WpfKey);

    [JsonIgnore]
    public bool IsValid => VirtualKey != 0 && (Ctrl || Alt || Shift || Win);

    private bool Has(string m) => Modifiers.Any(x => string.Equals(x, m, StringComparison.OrdinalIgnoreCase));

    public static HotkeySetting From(ModifierKeys mods, Key key)
    {
        var h = new HotkeySetting { Key = key.ToString() };
        if (mods.HasFlag(ModifierKeys.Control)) h.Modifiers.Add("Ctrl");
        if (mods.HasFlag(ModifierKeys.Alt)) h.Modifiers.Add("Alt");
        if (mods.HasFlag(ModifierKeys.Shift)) h.Modifiers.Add("Shift");
        if (mods.HasFlag(ModifierKeys.Windows)) h.Modifiers.Add("Win");
        return h;
    }

    public bool SameAs(HotkeySetting other) => ToString() == other.ToString();

    public override string ToString()
    {
        var parts = new List<string>();
        if (Ctrl) parts.Add("Ctrl");
        if (Alt) parts.Add("Alt");
        if (Shift) parts.Add("Shift");
        if (Win) parts.Add("Win");
        parts.Add(KeyDisplayName(WpfKey));
        return string.Join("+", parts);
    }

    public static string KeyDisplayName(Key key) => key switch
    {
        System.Windows.Input.Key.None => "?",
        >= System.Windows.Input.Key.D0 and <= System.Windows.Input.Key.D9 => ((char)('0' + (key - System.Windows.Input.Key.D0))).ToString(),
        >= System.Windows.Input.Key.NumPad0 and <= System.Windows.Input.Key.NumPad9 => "Num" + (key - System.Windows.Input.Key.NumPad0),
        System.Windows.Input.Key.OemTilde => "`",
        System.Windows.Input.Key.OemMinus => "-",
        System.Windows.Input.Key.OemPlus => "=",
        System.Windows.Input.Key.OemOpenBrackets => "[",
        System.Windows.Input.Key.OemCloseBrackets => "]",
        System.Windows.Input.Key.OemPipe => "\\",
        System.Windows.Input.Key.OemSemicolon => ";",
        System.Windows.Input.Key.OemQuotes => "'",
        System.Windows.Input.Key.OemComma => ",",
        System.Windows.Input.Key.OemPeriod => ".",
        System.Windows.Input.Key.OemQuestion => "/",
        System.Windows.Input.Key.Return => "Enter",
        System.Windows.Input.Key.Back => "Backspace",
        System.Windows.Input.Key.Capital => "CapsLock",
        System.Windows.Input.Key.Next => "PageDown",
        System.Windows.Input.Key.Prior => "PageUp",
        System.Windows.Input.Key.Snapshot => "PrintScreen",
        _ => key.ToString(),
    };
}
