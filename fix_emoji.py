import re
import sys
import os

files = [
r'e:\exeapps\FlyShelf\FlyShelf_PC\MainWindow.xaml',
r'e:\exeapps\FlyShelf\FlyShelf_PC\MainWindow.xaml.cs',
r'e:\exeapps\FlyShelf\FlyShelf_PC\MainWindow.EventHandlers.cs',
r'e:\exeapps\FlyShelf\FlyShelf_PC\MainWindow.PdfMerge.cs',
r'e:\exeapps\FlyShelf\FlyShelf_PC\MainWindow.Search.cs',
r'e:\exeapps\FlyShelf\FlyShelf_PC\MainWindow.Interactions.cs',
r'e:\exeapps\FlyShelf\FlyShelf_PC\MainWindow.SendToDevice.cs',
r'e:\exeapps\FlyShelf\FlyShelf_PC\MainWindow.WndProc.cs',
r'e:\exeapps\FlyShelf\FlyShelf_PC\Windows\TransferManagerWindow.xaml',
r'e:\exeapps\FlyShelf\FlyShelf_PC\Windows\TransferManagerWindow.xaml.cs',
r'e:\exeapps\FlyShelf\FlyShelf_PC\Windows\NetworkLogsWindow.xaml',
r'e:\exeapps\FlyShelf\FlyShelf_PC\Windows\NetworkLogsWindow.xaml.cs',
r'e:\exeapps\FlyShelf\FlyShelf_PC\Windows\TableEditorWindow.xaml',
r'e:\exeapps\FlyShelf\FlyShelf_PC\Windows\TableEditorWindow.xaml.cs',
r'e:\exeapps\FlyShelf\FlyShelf_PC\Windows\TableEditorWindow.IO.cs',
r'e:\exeapps\FlyShelf\FlyShelf_PC\Windows\QuickLookWindow.Ocr.cs',
r'e:\exeapps\FlyShelf\FlyShelf_PC\Windows\QuickLookWindow.Image.cs',
r'e:\exeapps\FlyShelf\FlyShelf_PC\Windows\QuickLookWindow.Pdf.cs',
r'e:\exeapps\FlyShelf\FlyShelf_PC\Windows\NoteExpandWindow.xaml',
r'e:\exeapps\FlyShelf\FlyShelf_PC\Windows\NoteExpandWindow.xaml.cs',
r'e:\exeapps\FlyShelf\FlyShelf_PC\Windows\NotesAIWindow.xaml',
r'e:\exeapps\FlyShelf\FlyShelf_PC\Windows\NotesAIWindow.xaml.cs',
r'e:\exeapps\FlyShelf\FlyShelf_PC\Windows\NotesAIDiffWindow.xaml',
r'e:\exeapps\FlyShelf\FlyShelf_PC\Windows\PasswordWindow.xaml',
r'e:\exeapps\FlyShelf\FlyShelf_PC\Windows\PasswordWindow.xaml.cs',
r'e:\exeapps\FlyShelf\FlyShelf_PC\Windows\ReminderCreateWindow.xaml',
r'e:\exeapps\FlyShelf\FlyShelf_PC\Windows\ReminderCreateWindow.xaml.cs',
r'e:\exeapps\FlyShelf\FlyShelf_PC\Windows\ReminderHistoryWindow.xaml.cs',
r'e:\exeapps\FlyShelf\FlyShelf_PC\Windows\OnboardingWindow.xaml',
r'e:\exeapps\FlyShelf\FlyShelf_PC\Windows\OnboardingWindow.xaml.cs',
r'e:\exeapps\FlyShelf\FlyShelf_PC\Windows\AiSetupPopup.xaml',
r'e:\exeapps\FlyShelf\FlyShelf_PC\Windows\TimerWindow.xaml',
r'e:\exeapps\FlyShelf\FlyShelf_PC\Windows\DragPreviewWindow.cs',
r'e:\exeapps\FlyShelf\FlyShelf_PC\Windows\PageReorderWindow.xaml.cs',
r'e:\exeapps\FlyShelf\FlyShelf_PC\Windows\PdfMergeWindow.xaml.cs',
r'e:\exeapps\FlyShelf\FlyShelf_PC\Classes\Licensing\UpgradePrompt.cs',
r'e:\exeapps\FlyShelf\FlyShelf_PC\ViewModels\ClipboardItem.cs',
r'e:\exeapps\FlyShelf\FlyShelf_PC\ViewModels\ClipboardItem.Convert.cs',
r'e:\exeapps\FlyShelf\FlyShelf_PC\ViewModels\ClipboardItem.Ocr.cs',
r'e:\exeapps\FlyShelf\FlyShelf_PC\ViewModels\ClipboardItem.QrCode.cs',
r'e:\exeapps\FlyShelf\FlyShelf_PC\ViewModels\ClipboardItem.TableExtract.cs',
r'e:\exeapps\FlyShelf\FlyShelf_PC\ViewModels\ClipboardItem.Actions.cs',
r'e:\exeapps\FlyShelf\FlyShelf_PC\ViewModels\ClipboardItem.Constructors.cs',
r'e:\exeapps\FlyShelf\FlyShelf_PC\ViewModels\FlyShelfViewModel.cs',
r'e:\exeapps\FlyShelf\FlyShelf_PC\ViewModels\FlyShelfViewModel.Persistence.cs',
r'e:\exeapps\FlyShelf\FlyShelf_PC\Classes\Networking\NetworkSyncServer.cs',
r'e:\exeapps\FlyShelf\FlyShelf_PC\Classes\Networking\NetworkSyncServer.Handlers.cs',
r'e:\exeapps\FlyShelf\FlyShelf_PC\Classes\Networking\NetworkSyncServer.Handlers.Routing.cs',
r'e:\exeapps\FlyShelf\FlyShelf_PC\Classes\Networking\NetworkSyncServer.FileTransfer.cs',
r'e:\exeapps\FlyShelf\FlyShelf_PC\Classes\Networking\LanTransferSession.cs',
r'e:\exeapps\FlyShelf\FlyShelf_PC\Classes\Networking\NetworkFileQueue.cs',
r'e:\exeapps\FlyShelf\FlyShelf_PC\Classes\Notes\NoteModels.cs',
r'e:\exeapps\FlyShelf\FlyShelf_PC\Classes\Notes\NoteTemplateDefinitions.cs',
r'e:\exeapps\FlyShelf\FlyShelf_PC\Classes\Productivity\TodoManager.cs',
r'e:\exeapps\FlyShelf\FlyShelf_PC\App.SafeMode.cs',
r'e:\exeapps\FlyShelf\FlyShelf_PC\App.xaml.cs',
r'e:\exeapps\FlyShelf\FlyShelf_PC\Resources\Styles\HubClipboardItemTemplate.xaml'
]

mapping = {
    '📋': 'Clipboard24',
    '📁': 'Folder24',
    '📂': 'Folder24',
    '🔒': 'LockClosed24',
    '🔓': 'LockOpen24',
    '🔑': 'Key24',
    '🔄': 'ArrowSync24',
    '🔍': 'Search24',
    '📤': 'ArrowUpload24',
    '📥': 'ArrowDownload24',
    '🗑': 'Delete24',
    '💾': 'Save24',
    '🔔': 'Alert24',
    '⚡': 'Flash24',
    '✨': 'Sparkle24',
    '📡': 'Wifi124',
    '🌐': 'Globe24',
    '🖼': 'Image24',
    '📄': 'Document24',
    '📊': 'DataBarVertical24',
    '🔗': 'Link24',
    '💻': 'Desktop24',
    '📱': 'Phone24',
    '🖥': 'Desktop24',
    '🎵': 'MusicNote124',
    '⏳': 'Clock24',
    '⏱': 'Timer24',
    '🧹': 'Broom24',
    '😊': 'Heart24', 
    '⏸': 'Pause24',
    '▶': 'Play24',
    '⚠': 'Warning24',
    '☁': 'Cloud24',
    '🔥': 'Flame24',
    '⬇': 'ArrowDownload24',
    '❌': 'Dismiss24',
    '✅': 'Checkmark24',
    '👁': 'Eye24',
    '🔌': 'PlugConnected24',
    '📅': 'Calendar24',
    '⏰': 'Clock24',
    '🎬': 'Video24',
    '📦': 'Box24',
    '🧠': 'Brain24',
    '🤖': 'Bot24',
    '💎': 'Diamond24',
    '💧': 'Drop24',
    '✏': 'Edit24',
    '✂': 'Cut24',
    '📝': 'DocumentEdit24',
    '🛒': 'Cart24',
    '☀': 'WeatherSunny24',
    '📕': 'Book24',
    '🎯': 'Target24',
    '⌨': 'Keyboard24',
    '🖱': 'Mouse24'
}

emoji_pattern = re.compile(r'[\U0001f300-\U0001f64f\U0001f680-\U0001f6ff\U0001f700-\U0001f77f\U0001f780-\U0001f7ff\U0001f800-\U0001f8ff\U0001f900-\U0001f9ff\U0001fa00-\U0001fa6f\U0001fa70-\U0001faff\U00002702-\U000027b0\U000024c2-\U0001f251\U00002600-\U000026ff\U00002300-\U000023ff]+')

def strip_emojis(s):
    res = ''
    for char in s:
        if emoji_pattern.match(char):
            if char in ['✕', '✓', '☑', '☐', '▾', '⫸', '─', '❝', '⟨', '⟩', '✦', '◀', '▶']:
                res += char
        else:
            res += char
    return res.replace('  ', ' ').strip()

total_replacements = {}

for f in files:
    if not os.path.exists(f):
        continue
        
    with open(f, 'r', encoding='utf-8') as file:
        content = file.read()
        
    lines = content.split('\n')
    new_lines = []
    replacements_in_file = 0
    
    for i, line in enumerate(lines):
        orig_line = line
        if line.strip().startswith('//') or line.strip().startswith('<!--'):
            new_lines.append(line)
            continue
            
        if '.xaml' in f and not f.endswith('.cs'):
            # Standalone emoji TextBlock
            m = re.search(r'<(?:emoji:)?TextBlock([^>]*)Text="([^"]+)"([^>]*)>', line)
            if m:
                text_val = m.group(2).strip()
                if emoji_pattern.match(text_val) and len(strip_emojis(text_val)) == 0:
                    emoji_char = text_val.replace('\ufe0f', '') 
                    sym = mapping.get(emoji_char, mapping.get(emoji_char[0], 'Star24')) if len(emoji_char)>0 else 'Star24'
                    line = re.sub(r'<(?:emoji:)?TextBlock', '<ui:SymbolIcon', line)
                    line = line.replace(f'Text="{m.group(2)}"', f'Symbol="{sym}"')
                    line = line.replace('</TextBlock>', '</ui:SymbolIcon>')
                    line = line.replace('</emoji:TextBlock>', '</ui:SymbolIcon>')
            
            # Other emojis in XAML properties
            def repl_prop(m2):
                val = m2.group(1)
                stripped = strip_emojis(val)
                return f'="{stripped}"'
            line = re.sub(r'="([^"]*[\U00010000-\U0010ffff\u2600-\u27bf][^"]*)"', repl_prop, line)
            line = re.sub(r'="([^"]*[\u2300-\u23ff][^"]*)"', repl_prop, line)
            
        else:
            # C# files
            if 'MakeCenteredEmoji(' in line or 'MakeEmojiIcon(' in line:
                def repl_make(m2):
                    emoji_val = m2.group(1).replace('\ufe0f', '')
                    clean = mapping.get(emoji_val, mapping.get(emoji_val[0] if emoji_val else '', 'Icon')).replace('24', '')
                    if clean == 'Icon': clean = 'Doc' 
                    return f'MakeCenteredEmoji("{clean}")'
                def repl_make2(m2):
                    emoji_val = m2.group(1).replace('\ufe0f', '')
                    clean = mapping.get(emoji_val, mapping.get(emoji_val[0] if emoji_val else '', 'Icon')).replace('24', '')
                    if clean == 'Icon': clean = 'Doc' 
                    return f'MakeEmojiIcon("{clean}"'
                line = re.sub(r'MakeCenteredEmoji\("([^"]+)"\)', repl_make, line)
                line = re.sub(r'MakeEmojiIcon\("([^"]+)"', repl_make2, line)
            
            # Remove from string literals
            def repl_str(m2):
                val = m2.group(1)
                stripped = strip_emojis(val)
                return f'"{stripped}"'
            line = re.sub(r'"([^"]*[\U00010000-\U0010ffff\u2600-\u27bf][^"]*)"', repl_str, line)
            line = re.sub(r'"([^"]*[\u2300-\u23ff][^"]*)"', repl_str, line)
            
        if line != orig_line:
            replacements_in_file += 1
            
        new_lines.append(line)
        
    if replacements_in_file > 0:
        total_replacements[f] = replacements_in_file
        with open(f, 'w', encoding='utf-8') as out_file:
            out_file.write('\n'.join(new_lines))

print('Done. Replacements:')
for k, v in total_replacements.items():
    print(f'{os.path.basename(k)}: {v}')
