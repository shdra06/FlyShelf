path = r"e:\exeapps\FlyShelf\FlyShelf_PC\Windows\HubWindow.Settings.cs"
with open(path, "r", encoding="utf-8") as f:
    content = f.read()

# Locate the start of BuyPremium_Click and end before 'Device Send' comment
start_idx = content.find("private void BuyPremium_Click")
end_idx = content.find("Device Send")

if start_idx != -1 and end_idx != -1:
    # Go backwards to find the start of the comment line
    comment_line_start = content.rfind("//", 0, end_idx)
    if comment_line_start != -1:
        method_block = content[start_idx:comment_line_start]
        clean_block = """private void BuyPremium_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            FlyShelf.Classes.UpgradePrompt.OpenSecureCheckout(this);
        }

        """
        content = content.replace(method_block, clean_block)
        with open(path, "w", encoding="utf-8") as f:
            f.write(content)
        print("Cleanup successful!")
    else:
        print("Comment start not found!")
else:
    print("Method or marker not found!")
