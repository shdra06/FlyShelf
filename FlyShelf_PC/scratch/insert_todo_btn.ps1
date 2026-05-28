$path = "e:\exeapps\FlyShelf\FlyShelf_PC\MainWindow.xaml"
$content = [System.IO.File]::ReadAllText($path)

$pattern = '(?s)(<ui:Button\s+x:Name="NotesToggleBtn".*?</ui:Button>)'
if ($content -match $pattern) {
    $btnMatch = $Matches[0]
    
    # Get the leading indentation of the match in the original text (find the spaces before the tag)
    $index = $content.IndexOf($btnMatch)
    $startOfLine = $content.LastIndexOf("`n", $index)
    if ($startOfLine -lt 0) { $startOfLine = 0 }
    $leadingSpaces = $content.Substring($startOfLine + 1, $index - $startOfLine - 1)
    
    # Just in case, trim it to only whitespace
    if ($leadingSpaces -match '^\s*$') {
        $indent = $leadingSpaces
    } else {
        $indent = "                      "
    }
    
    $todoBtn = @"
`r`n`r`n${indent}<!-- To-Do List Toggle -->
${indent}<ui:Button x:Name="TodoToggleBtn" Style="{StaticResource PremiumHeaderButtonStyle}" Click="TodoToggle_Click" ToolTip="To-Do List" Margin="0,0,6,0">
${indent}    <ui:Button.Resources>
${indent}        <SolidColorBrush x:Key="ButtonBackgroundPointerOver" Color="#288B5CF6"/>
${indent}        <SolidColorBrush x:Key="ButtonBorderBrushPointerOver" Color="#608B5CF6"/>
${indent}        <SolidColorBrush x:Key="ButtonForegroundPointerOver" Color="#C4B5FD"/>
${indent}    </ui:Button.Resources>
${indent}    <ui:Button.Icon><ui:SymbolIcon Symbol="CheckboxChecked24" /></ui:Button.Icon>
${indent}</ui:Button>
"@

    $newContent = $content.Replace($btnMatch, $btnMatch + $todoBtn)
    [System.IO.File]::WriteAllText($path, $newContent)
    Write-Host "SUCCESS REGEX"
} else {
    Write-Host "PATTERN NOT FOUND"
}
