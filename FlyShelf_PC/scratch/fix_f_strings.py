file_path = r"e:\exeapps\FlyShelf\FlyShelf_PC\ViewModels\ClipboardItem.Actions.cs"

with open(file_path, "r", encoding="utf-8") as f:
    content = f.read()

# Safe literal replacements
replacements = {
    'f"Successfully extracted {result.Text.Length} chars of text."': '$"Successfully extracted {result.Text.Length} chars of text."',
    'f"Failed to run background OCR: {ex.Message}"': '$"Failed to run background OCR: {ex.Message}"',
    'f"Scan failed: {ex.Message}"': '$"Scan failed: {ex.Message}"'
}

replaced_count = 0
for target, replacement in replacements.items():
    if target in content:
        content = content.replace(target, replacement)
        print(f"Replaced: {target} -> {replacement}")
        replaced_count += 1
    else:
        # Check if line endings are different in target (e.g. carriage returns inside target)
        print(f"Target not found literally: {target}")

with open(file_path, "w", encoding="utf-8") as f:
    f.write(content)

print(f"Finished. Replaced {replaced_count} targets.")
