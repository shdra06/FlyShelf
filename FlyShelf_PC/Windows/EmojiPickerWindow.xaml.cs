using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MicaWPF.Controls;
using FlyShelf.Classes;
using FlyShelf.Helpers;

namespace FlyShelf.Windows
{
    public partial class EmojiPickerWindow : MicaWindow
    {
        public class EmojiItem { public string Emoji { get; set; } = ""; public string Name { get; set; } = ""; public string Category { get; set; } = ""; }

        private List<EmojiItem> _allEmojis = new();
        private string _currentCategory = "😊 Smileys";
        private bool _isPinned = false; // Default: unpinned
        private IntPtr _targetWindow = IntPtr.Zero; // Window that was focused before emoji picker opened

        // P/Invoke declarations centralized in NativeMethods.cs
        private const uint INPUT_KEYBOARD = NativeMethods.INPUT_KEYBOARD;
        private const uint KEYEVENTF_KEYUP = (uint)NativeMethods.KEYEVENTF_KEYUP;
        private const ushort VK_CONTROL = (ushort)NativeMethods.VK_CONTROL;
        private const ushort VK_V = (ushort)NativeMethods.VK_V;

        /// <summary>Pass the handle of the previously focused window so we can auto-paste emojis into it.</summary>
        public EmojiPickerWindow(IntPtr targetWindow = default)
        {
            _targetWindow = targetWindow;
            InitializeComponent();
            FlyShelf.Classes.SmoothScrollFeature.Attach(this);
            this.Closed += (s, e) => FlyShelf.Classes.SmoothScrollFeature.Detach(this);
            FlyShelf.Classes.NativeMethods.ApplyWindowBackdropAndBackground(this);
            LoadEmojis();
            BuildCategoryTabs();
            FilterEmojis();
            UpdatePinVisual();
            this.PreviewKeyDown += (s, e) => { if (e.Key == System.Windows.Input.Key.Escape) this.Close(); };
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
            catch { } // Best-effort: failure is acceptable
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
                PinBtn.Foreground = FindResource("ThemeAccent") as System.Windows.Media.Brush
                    ?? new System.Windows.Media.SolidColorBrush(FlyShelf.Helpers.ThemeColors.IndigoMid);
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
                {
                    // Use theme accent color for active category tab
                    var accentBrush = FindResource("ThemeAccent") as System.Windows.Media.SolidColorBrush;
                    var accentColor = accentBrush?.Color ?? FlyShelf.Helpers.ThemeColors.IndigoMid;
                    btn.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x20, accentColor.R, accentColor.G, accentColor.B));
                }
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
                filtered = _allEmojis.Where(e => e.Emoji.Contains(search, StringComparison.OrdinalIgnoreCase) || e.Name.Contains(search, StringComparison.OrdinalIgnoreCase) || e.Category.Contains(search, StringComparison.OrdinalIgnoreCase));
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
                        uint targetThread = NativeMethods.GetWindowThreadProcessId(_targetWindow, out _);
                        uint currentThread = NativeMethods.GetCurrentThreadId();
                        if (targetThread != currentThread)
                            NativeMethods.AttachThreadInput(currentThread, targetThread, true);

                        NativeMethods.SetForegroundWindow(_targetWindow);

                        if (targetThread != currentThread)
                            NativeMethods.AttachThreadInput(currentThread, targetThread, false);

                        // Brief delay to let the target window gain focus, then Ctrl+V
                        System.Threading.Tasks.Task.Delay(50).ContinueWith(_ =>
                        {
                            Dispatcher.Invoke(() =>
                            {
                                // Simulate Ctrl+V via SendInput
                                var inputs = new NativeMethods.INPUT[]
                                {
                                    new NativeMethods.INPUT { type = INPUT_KEYBOARD, u = new NativeMethods.INPUTUNION { ki = new NativeMethods.KEYBDINPUT { wVk = VK_CONTROL } } },
                                    new NativeMethods.INPUT { type = INPUT_KEYBOARD, u = new NativeMethods.INPUTUNION { ki = new NativeMethods.KEYBDINPUT { wVk = VK_V } } },
                                    new NativeMethods.INPUT { type = INPUT_KEYBOARD, u = new NativeMethods.INPUTUNION { ki = new NativeMethods.KEYBDINPUT { wVk = VK_V, dwFlags = KEYEVENTF_KEYUP } } },
                                    new NativeMethods.INPUT { type = INPUT_KEYBOARD, u = new NativeMethods.INPUTUNION { ki = new NativeMethods.KEYBDINPUT { wVk = VK_CONTROL, dwFlags = KEYEVENTF_KEYUP } } },
                                };
                                NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.INPUT>());

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
                catch { } // Best-effort: failure is acceptable
            }
        }

        private void EmojiSearchBox_TextChanged(object sender, TextChangedEventArgs e) => FilterEmojis();
        private void Header_MouseDown(object sender, MouseButtonEventArgs e) { if (e.LeftButton == MouseButtonState.Pressed) try { DragMove(); } catch { } /* Best-effort: failure is acceptable */ }
        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        private void Window_Deactivated(object sender, EventArgs e)
        {
            // Only auto-close when NOT pinned; pinned emoji picker stays open
            if (!_isPinned)
            {
                try { if (IsLoaded) Close(); } catch { } // Best-effort: failure is acceptable
            }
        }
    }
}
