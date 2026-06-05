using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MicaWPF.Controls;

namespace FlyShelf.Windows
{
    public partial class EmojiPickerWindow : MicaWindow
    {
        public class EmojiItem { public string Emoji { get; set; } = ""; public string Name { get; set; } = ""; public string Category { get; set; } = ""; }

        private List<EmojiItem> _allEmojis = new();
        private string _currentCategory = "😊 Smileys";
        private bool _isPinned = false; // Default: unpinned
        private IntPtr _targetWindow = IntPtr.Zero; // Window that was focused before emoji picker opened

        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
        [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT { public uint type; public INPUTUNION u; }
        [StructLayout(LayoutKind.Explicit)]
        private struct INPUTUNION { [FieldOffset(0)] public KEYBDINPUT ki; }
        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
        private const uint INPUT_KEYBOARD = 1;
        private const uint KEYEVENTF_KEYUP = 0x0002;
        private const ushort VK_CONTROL = 0x11;
        private const ushort VK_V = 0x56;

        /// <summary>Pass the handle of the previously focused window so we can auto-paste emojis into it.</summary>
        public EmojiPickerWindow(IntPtr targetWindow = default)
        {
            _targetWindow = targetWindow;
            InitializeComponent();
            FlyShelf.Classes.NativeMethods.ApplyWindowBackdropAndBackground(this);
            LoadEmojis();
            BuildCategoryTabs();
            FilterEmojis();
            UpdatePinVisual();
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            // Suppress red DWM window border and re-apply theme with valid hwnd
            try
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                if (hwnd != IntPtr.Zero)
                {
                    int colorNone = FlyShelf.Classes.NativeMethods.DWMWA_COLOR_DARK_GRAY;
                    FlyShelf.Classes.NativeMethods.DwmSetWindowAttribute(hwnd, FlyShelf.Classes.NativeMethods.DWMWA_BORDER_COLOR, ref colorNone, sizeof(int));
                }
            }
            catch { }
            FlyShelf.Classes.NativeMethods.ApplyWindowBackdropAndBackground(this);
        }

        // ═══ PIN / UNPIN ═══
        private void Pin_Click(object sender, RoutedEventArgs e)
        {
            _isPinned = !_isPinned;
            Topmost = _isPinned;
            UpdatePinVisual();
        }

        private void UpdatePinVisual()
        {
            if (_isPinned)
            {
                PinIcon.Symbol = Wpf.Ui.Controls.SymbolRegular.Pin24;
                PinBtn.ToolTip = "Pinned (always on top) — click to unpin";
                PinBtn.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x63, 0x66, 0xF1)); // indigo
            }
            else
            {
                PinIcon.Symbol = Wpf.Ui.Controls.SymbolRegular.PinOff24;
                PinBtn.ToolTip = "Unpinned — click to pin on top";
                PinBtn.Foreground = (System.Windows.Media.Brush)FindResource("MicaWPF.Brushes.TextFillColorTertiary");
            }
        }

        private void LoadEmojis()
        {
            var cats = new Dictionary<string, (string label, string[] emojis)>
            {
                ["😊 Smileys"] = ("😊 Smileys", new[] {
                    "😀","😃","😄","😁","😆","😅","🤣","😂","🙂","🙃","😉","😊","😇","🥰","😍","🤩","😘","😗","😚","😙",
                    "🥲","😋","😛","😜","🤪","😝","🤑","🤗","🤭","🫢","🫣","🤫","🤔","🫡","🤐","🤨","😐","😑","😶","🫥",
                    "😏","😒","🙄","😬","🤥","😌","😔","😪","🤤","😴","😷","🤒","🤕","🤢","🤮","🥵","🥶","🥴","😵","🤯",
                    "🤠","🥳","🥸","😎","🤓","🧐","😕","🫤","😟","🙁","☹️","😮","😯","😲","😳","🥺","🥹","😦","😧","😨",
                    "😰","😥","😢","😭","😱","😖","😣","😞","😓","😩","😫","🥱","😤","😡","😠","🤬","😈","👿","💀","☠️",
                    "💩","🤡","👹","👺","👻","👽","👾","🤖","😺","😸","😹","😻","😼","😽","🙀","😿","😾"
                }),
                ["👋 Hands"] = ("👋 Hands", new[] {
                    "👋","🤚","🖐️","✋","🖖","🫱","🫲","🫳","🫴","👌","🤌","🤏","✌️","🤞","🫰","🤟","🤘","🤙","👈","👉",
                    "👆","🖕","👇","☝️","🫵","👍","👎","✊","👊","🤛","🤜","👏","🙌","🫶","👐","🤲","🤝","🙏","✍️","💅",
                    "🤳","💪","🦾","🦿","🦵","🦶","👂","🦻","👃","🧠","🫀","🫁","🦷","🦴","👀","👁️","👅","👄","🫦"
                }),
                ["👤 People"] = ("👤 People", new[] {
                    "👶","🧒","👦","👧","🧑","👱","👨","🧔","👩","🧓","👴","👵","🙍","🙎","🙅","🙆","💁","🙋","🧏","🙇",
                    "🤦","🤷","👮","🕵️","💂","🥷","👷","🫅","🤴","👸","👳","👲","🧕","🤵","👰","🤰","🫃","🫄","🤱","👼",
                    "🎅","🤶","🦸","🦹","🧙","🧚","🧛","🧜","🧝","🧞","🧟","🧌","💆","💇","🚶","🧍","🧎","🏃","💃","🕺"
                }),
                ["❤️ Hearts"] = ("❤️ Hearts", new[] {
                    "❤️","🧡","💛","💚","💙","💜","🖤","🤍","🤎","💔","❤️‍🔥","❤️‍🩹","❣️","💕","💞","💓","💗","💖","💘","💝",
                    "💟","♥️","💋","💌","💐","🌹","🥀","🌺","🌻","🌼","🌷","🪷","🌸","💮","🏵️","🪻"
                }),
                ["🐶 Animals"] = ("🐶 Animals", new[] {
                    "🐶","🐱","🐭","🐹","🐰","🦊","🐻","🐼","🐻‍❄️","🐨","🐯","🦁","🐮","🐷","🐽","🐸","🐵","🙈","🙉","🙊",
                    "🐒","🐔","🐧","🐦","🐤","🐣","🐥","🦆","🦅","🦉","🦇","🐺","🐗","🐴","🦄","🫎","🐝","🪱","🐛","🦋",
                    "🐌","🐞","🐜","🪰","🪲","🪳","🦟","🦗","🕷️","🦂","🐢","🐍","🦎","🦖","🦕","🐙","🦑","🦐","🦞","🦀",
                    "🐡","🐠","🐟","🐬","🐳","🐋","🦈","🪸","🐊","🐅","🐆","🦓","🫏","🦍","🦧","🐘","🦛","🦏","🐪","🐫"
                }),
                ["🍕 Food"] = ("🍕 Food", new[] {
                    "🍇","🍈","🍉","🍊","🍋","🍌","🍍","🥭","🍎","🍏","🍐","🍑","🍒","🍓","🫐","🥝","🍅","🫒","🥥","🥑",
                    "🍆","🥔","🥕","🌽","🌶️","🫑","🥒","🥬","🥦","🧄","🧅","🍄","🥜","🫘","🌰","🍞","🥐","🥖","🫓","🥨",
                    "🥯","🥞","🧇","🧀","🍖","🍗","🥩","🥓","🍔","🍟","🍕","🌭","🥪","🌮","🌯","🫔","🥙","🧆","🥚","🍳",
                    "🥘","🍲","🫕","🥣","🥗","🍿","🧈","🧂","🥫","🍱","🍘","🍙","🍚","🍛","🍜","🍝","🍠","🍢","🍣","🍤",
                    "🍥","🥮","🍡","🥟","🥠","🥡","🦀","🦞","🦐","🦑","🦪","🍦","🍧","🍨","🍩","🍪","🎂","🍰","🧁","🥧","🍫","🍬","🍭","🍮","🍯"
                }),
                ["⚽ Activities"] = ("⚽ Activities", new[] {
                    "⚽","🏀","🏈","⚾","🥎","🎾","🏐","🏉","🥏","🎱","🪀","🏓","🏸","🏒","🏑","🥍","🏏","🪃","🥅","⛳",
                    "🪁","🏹","🎣","🤿","🥊","🥋","🎽","🛹","🛼","🛷","⛸️","🥌","🎿","⛷️","🏂","🪂","🏋️","🤸","🤼","🤽",
                    "🤾","🤺","⛹️","🏊","🚣","🧗","🚵","🚴","🏆","🥇","🥈","🥉","🏅","🎖️","🏵️","🎗️","🎫","🎟️","🎪","🎭",
                    "🎨","🎬","🎤","🎧","🎼","🎹","🥁","🪘","🎷","🎺","🪗","🎸","🪕","🎻","🪈","🎲","♟️","🎯","🎳","🎮","🕹️","🎰"
                }),
                ["🚗 Travel"] = ("🚗 Travel", new[] {
                    "🚗","🚕","🚙","🚌","🚎","🏎️","🚓","🚑","🚒","🚐","🛻","🚚","🚛","🚜","🏍️","🛵","🚲","🛴","🛹","🛼",
                    "🚁","🛸","🚀","🛩️","✈️","🛫","🛬","🪂","💺","🚢","⛵","🚤","🛥️","🛳️","⛴️","🚂","🚃","🚄","🚅","🚆",
                    "🚇","🚈","🚉","🚊","🚝","🚞","🚋","🚃","🏠","🏡","🏢","🏣","🏤","🏥","🏦","🏨","🏩","🏪","🏫","🏬",
                    "🏭","🏯","🏰","💒","🗼","🗽","⛪","🕌","🛕","🕍","⛩️","🕋","⛲","⛺","🌁","🌃","🏙️","🌄","🌅","🌆","🌇","🌉","🗻","🌋","🏔️","⛰️"
                }),
                ["💡 Objects"] = ("💡 Objects", new[] {
                    "⌚","📱","📲","💻","⌨️","🖥️","🖨️","🖱️","🖲️","🕹️","🗜️","💽","💾","💿","📀","📼","📷","📸","📹","🎥",
                    "📽️","🎞️","📞","☎️","📟","📠","📺","📻","🎙️","🎚️","🎛️","🧭","⏱️","⏲️","⏰","🕰️","⌛","⏳","📡","🔋",
                    "🪫","🔌","💡","🔦","🕯️","🪔","🧯","🛢️","💸","💵","💴","💶","💷","🪙","💰","💳","💎","⚖️","🪜","🧰",
                    "🪛","🔧","🔨","⚒️","🛠️","⛏️","🪚","🔩","⚙️","🪤","🧱","⛓️","🧲","🔫","💣","🧨","🪓","🔪","🗡️","⚔️",
                    "🛡️","🚬","⚰️","🪦","⚱️","🏺","🔮","📿","🧿","🪬","💈","⚗️","🔭","🔬","🕳️","🩹","🩺","🩻","🩼","💊","💉","🩸","🧬","🦠","🧫","🧪"
                }),
                ["🔣 Symbols"] = ("🔣 Symbols", new[] {
                    "❤️","🔴","🟠","🟡","🟢","🔵","🟣","⚫","⚪","🟤","🔶","🔷","🔸","🔹","🔺","🔻","💠","🔘","🔳","🔲",
                    "✅","☑️","✔️","❌","❎","➕","➖","➗","✖️","♾️","‼️","⁉️","❓","❔","❕","❗","〰️","💱","💲","⚕️",
                    "♻️","⚜️","🔱","📛","🔰","⭕","✳️","❇️","🔆","🔅","〽️","⚠️","🚸","🔅","♈","♉","♊","♋","♌","♍",
                    "♎","♏","♐","♑","♒","♓","⛎","🔀","🔁","🔂","▶️","⏩","⏭️","⏯️","◀️","⏪","⏮️","🔼","⏫","🔽","⏬",
                    "⏸️","⏹️","⏺️","⏏️","🎦","🔅","📶","📳","📴","🏁","🚩","🎌","🏴","🏳️","🏳️‍🌈","🏳️‍⚧️","🏴‍☠️"
                }),
                ["🚩 Flags"] = ("🚩 Flags", new[] {
                    "🇺🇸","🇬🇧","🇫🇷","🇩🇪","🇮🇹","🇪🇸","🇵🇹","🇧🇷","🇨🇦","🇦🇺","🇯🇵","🇰🇷","🇨🇳","🇮🇳","🇷🇺","🇲🇽","🇦🇷","🇨🇴","🇹🇷","🇸🇦",
                    "🇦🇪","🇮🇩","🇹🇭","🇻🇳","🇵🇭","🇲🇾","🇸🇬","🇳🇬","🇿🇦","🇪🇬","🇰🇪","🇬🇭","🇪🇹","🇵🇰","🇧🇩","🇱🇰","🇳🇵","🇮🇱","🇵🇸","🇮🇶",
                    "🇮🇷","🇦🇫","🇺🇦","🇵🇱","🇳🇱","🇧🇪","🇨🇭","🇦🇹","🇸🇪","🇳🇴","🇩🇰","🇫🇮","🇮🇪","🇬🇷","🇨🇿","🇷🇴","🇭🇺","🇧🇬","🇭🇷","🇷🇸"
                })
            };

            foreach (var kvp in cats)
            {
                foreach (var e in kvp.Value.emojis)
                    _allEmojis.Add(new EmojiItem { Emoji = e, Name = e, Category = kvp.Key });
            }
        }

        private void BuildCategoryTabs()
        {
            CategoryTabs.Children.Clear();
            var categories = _allEmojis.Select(e => e.Category).Distinct().ToList();
            foreach (var cat in categories)
            {
                var emojiText = new Emoji.Wpf.TextBlock { Text = cat.Split(' ')[0], FontSize = 18, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                var btn = new System.Windows.Controls.Button
                {
                    Content = emojiText,
                    Width = 34, Height = 34,
                    Margin = new Thickness(0, 0, 3, 0),
                    Padding = new Thickness(0),
                    Background = System.Windows.Media.Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Cursor = Cursors.Hand,
                    ToolTip = cat,
                    Tag = cat
                };
                btn.Click += CategoryTab_Click;
                if (cat == _currentCategory)
                    btn.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x20, 0x63, 0x66, 0xF1));
                CategoryTabs.Children.Add(btn);
            }
        }

        private void CategoryTab_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button btn && btn.Tag is string cat)
            {
                _currentCategory = cat;
                EmojiSearchBox.Text = "";
                BuildCategoryTabs();
                FilterEmojis();
            }
        }

        private void FilterEmojis()
        {
            string search = EmojiSearchBox?.Text?.Trim().ToLowerInvariant() ?? "";
            EmojiSearchPlaceholder.Visibility = string.IsNullOrEmpty(search) ? Visibility.Visible : Visibility.Collapsed;

            IEnumerable<EmojiItem> filtered;
            if (!string.IsNullOrEmpty(search))
                filtered = _allEmojis.Where(e => e.Emoji.Contains(search) || e.Name.ToLowerInvariant().Contains(search) || e.Category.ToLowerInvariant().Contains(search));
            else
                filtered = _allEmojis.Where(e => e.Category == _currentCategory);

            EmojiGrid.ItemsSource = filtered.ToList();
        }

        private void Emoji_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button btn && btn.Tag is string emoji && !string.IsNullOrEmpty(emoji))
            {
                try
                {
                    FlyShelf.Classes.ClipboardHelper.SafeSetText(emoji);

                    // Auto-paste into the target window (like Windows emoji picker)
                    if (_targetWindow != IntPtr.Zero)
                    {
                        // Use AttachThreadInput trick to reliably steal focus
                        uint targetThread = GetWindowThreadProcessId(_targetWindow, out _);
                        uint currentThread = GetCurrentThreadId();
                        if (targetThread != currentThread)
                            AttachThreadInput(currentThread, targetThread, true);

                        SetForegroundWindow(_targetWindow);

                        if (targetThread != currentThread)
                            AttachThreadInput(currentThread, targetThread, false);

                        // Brief delay to let the target window gain focus, then Ctrl+V
                        System.Threading.Tasks.Task.Delay(50).ContinueWith(_ =>
                        {
                            Dispatcher.Invoke(() =>
                            {
                                // Simulate Ctrl+V via SendInput
                                var inputs = new INPUT[]
                                {
                                    new INPUT { type = INPUT_KEYBOARD, u = new INPUTUNION { ki = new KEYBDINPUT { wVk = VK_CONTROL } } },
                                    new INPUT { type = INPUT_KEYBOARD, u = new INPUTUNION { ki = new KEYBDINPUT { wVk = VK_V } } },
                                    new INPUT { type = INPUT_KEYBOARD, u = new INPUTUNION { ki = new KEYBDINPUT { wVk = VK_V, dwFlags = KEYEVENTF_KEYUP } } },
                                    new INPUT { type = INPUT_KEYBOARD, u = new INPUTUNION { ki = new KEYBDINPUT { wVk = VK_CONTROL, dwFlags = KEYEVENTF_KEYUP } } },
                                };
                                SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());

                                // Bring emoji picker back to front if pinned
                                if (_isPinned || IsLoaded)
                                {
                                    Activate();
                                }
                            });
                        });
                    }

                    ToastWindow.ShowToast($"Pasted {emoji}");
                }
                catch { }
            }
        }

        private void EmojiSearchBox_TextChanged(object sender, TextChangedEventArgs e) => FilterEmojis();
        private void Header_MouseDown(object sender, MouseButtonEventArgs e) { if (e.LeftButton == MouseButtonState.Pressed) try { DragMove(); } catch { } }
        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        private void Window_Deactivated(object sender, EventArgs e)
        {
            // Only auto-close when NOT pinned; pinned emoji picker stays open
            if (!_isPinned)
            {
                try { if (IsLoaded) Close(); } catch { }
            }
        }
    }
}
