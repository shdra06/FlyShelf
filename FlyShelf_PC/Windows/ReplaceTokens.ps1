$path = "e:\exeapps\FlyShelf\FlyShelf_PC\Windows\HubWindow.xaml"
$c = [System.IO.File]::ReadAllText($path)

$c = $c -replace 'FontSize="9"', 'FontSize="{StaticResource TypeMicro}"'
$c = $c -replace 'FontSize="10"', 'FontSize="{StaticResource TypeCaption}"'
$c = $c -replace 'FontSize="11"', 'FontSize="{StaticResource TypeSmall}"'
$c = $c -replace 'FontSize="12"', 'FontSize="{StaticResource TypeBody}"'
$c = $c -replace 'FontSize="13"', 'FontSize="{StaticResource TypeSubtitle}"'
$c = $c -replace 'FontSize="14"', 'FontSize="{StaticResource TypeSubtitle}"'
$c = $c -replace 'FontSize="16"', 'FontSize="{StaticResource TypeTitle}"'
$c = $c -replace 'FontSize="28"', 'FontSize="{StaticResource TypeDisplay}"'

$c = $c -replace 'CornerRadius="8"', 'CornerRadius="{StaticResource RadiusSM}"'
$c = $c -replace 'CornerRadius="12"', 'CornerRadius="{StaticResource RadiusMD}"'
$c = $c -replace 'CornerRadius="16"', 'CornerRadius="{StaticResource RadiusLG}"'

$c = $c -replace 'Duration="0:0:0\.08"', 'Duration="{StaticResource Motion.Instant}"'
$c = $c -replace 'Duration="0:0:0\.12"', 'Duration="{StaticResource Motion.Fast}"'
$c = $c -replace 'Duration="0:0:0\.15"', 'Duration="{StaticResource Motion.Normal}"'
$c = $c -replace 'Duration="0:0:0\.18"', 'Duration="{StaticResource Motion.Normal}"'
$c = $c -replace 'Duration="0:0:0\.2"', 'Duration="{StaticResource Motion.Entrance}"'
$c = $c -replace 'Duration="0:0:0\.20"', 'Duration="{StaticResource Motion.Entrance}"'
$c = $c -replace 'Duration="0:0:0\.22"', 'Duration="{StaticResource Motion.Entrance}"'
$c = $c -replace 'Duration="0:0:0\.3"', 'Duration="{StaticResource Motion.Slow}"'
$c = $c -replace 'Duration="0:0:0\.30"', 'Duration="{StaticResource Motion.Slow}"'

$c = $c -replace 'Foreground="#F1F5F9"', 'Foreground="{DynamicResource ThemeTextPrimary}"'
$c = $c -replace 'Foreground="#94A3B8"', 'Foreground="{DynamicResource ThemeTextSecondary}"'
$c = $c -replace 'Foreground="#8B92A0"', 'Foreground="{DynamicResource ThemeTextSecondary}"'
$c = $c -replace 'Foreground="#64748B"', 'Foreground="{DynamicResource ThemeTextMuted}"'

[System.IO.File]::WriteAllText($path, $c, [System.Text.Encoding]::UTF8)
