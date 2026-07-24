import re
import xml.etree.ElementTree as ET

path = r'e:\exeapps\FlyShelf\FlyShelf_PC\MainWindow.xaml'
with open(path, 'r', encoding='utf-8') as f:
    content = f.read()

# FontSizes
sizes = {
    '9': 'TypeMicro',
    '10': 'TypeCaption',
    '10.5': 'TypeCaption',
    '11': 'TypeSmall',
    '11.5': 'TypeSmall',
    '12': 'TypeBody',
    '12.5': 'TypeBody',
    '13': 'TypeSubtitle',
    '14': 'TypeSubtitle',
    '15': 'TypeSubtitle',
    '16': 'TypeTitle'
}

for s, token in sizes.items():
    content = re.sub(rf'FontSize="{s}"', f'FontSize="{{StaticResource {token}}}"', content)

# CornerRadii
radii = {
    '8': 'RadiusSM',
    '12': 'RadiusMD',
    '16': 'RadiusLG'
}

for r, token in radii.items():
    content = re.sub(rf'CornerRadius="{r}"', f'CornerRadius="{{StaticResource {token}}}"', content)

# Durations
durations = {
    '0:0:0.12': 'Motion.Fast',
    '0:0:0.15': 'Motion.Normal',
    '0:0:0.18': 'Motion.Normal',
    '0:0:0.2': 'Motion.Entrance',
    '0:0:0.3': 'Motion.Slow',
    '0:0:0.6': 'Motion.Slow'
}

for d, token in durations.items():
    content = re.sub(rf'Duration="{d}"', f'Duration="{{StaticResource {token}}}"', content)

# Easing
content = re.sub(r'<CubicEase EasingMode="EaseOut"\s*/>', '<StaticResource ResourceKey="Motion.EaseOut" />', content)
content = re.sub(r'<CubicEase EasingMode="EaseIn"\s*/>', '<StaticResource ResourceKey="Motion.EaseIn" />', content)
content = re.sub(r'<CubicEase EasingMode="EaseInOut"\s*/>', '<StaticResource ResourceKey="Motion.EaseInOut" />', content)
content = re.sub(r'<SineEase EasingMode="EaseInOut"\s*/>', '<StaticResource ResourceKey="Motion.EaseInOut" />', content)

# Backgrounds
content = re.sub(r'Background="#(?:12|16|18)FFFFFF"', 'Background="{DynamicResource ThemeOverlayBg}"', content)
content = re.sub(r'Background="#(?:20|2A)FFFFFF"', 'Background="{DynamicResource ThemeOverlayBgHover}"', content)

with open(path, 'w', encoding='utf-8') as f:
    f.write(content)

print('Replacements done. Validating...')
try:
    ET.parse(path)
    print('VALID')
except Exception as e:
    print('INVALID:', e)
