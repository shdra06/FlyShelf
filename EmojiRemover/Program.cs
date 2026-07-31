using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Text;

class Program
{
    static void Main()
    {
        string[] files = {
            @"e:\exeapps\FlyShelf\FlyShelf_PC\Windows\HubWindow.xaml",
            @"e:\exeapps\FlyShelf\FlyShelf_PC\Windows\HubWindow.Settings.cs",
            @"e:\exeapps\FlyShelf\FlyShelf_PC\Windows\HubWindow.Networking.cs",
            @"e:\exeapps\FlyShelf\FlyShelf_PC\Windows\HubWindow.Advanced.cs",
            @"e:\exeapps\FlyShelf\FlyShelf_PC\Windows\HubWindow.Tabs.cs",
            @"e:\exeapps\FlyShelf\FlyShelf_PC\Windows\HubWindow.History.cs",
            @"e:\exeapps\FlyShelf\FlyShelf_PC\Windows\HubWindow.Interactions.cs",
            @"e:\exeapps\FlyShelf\FlyShelf_PC\Windows\HubWindow.Logs.cs",
            @"e:\exeapps\FlyShelf\FlyShelf_PC\Windows\HubWindow.SettingsHandlers.cs",
            @"e:\exeapps\FlyShelf\FlyShelf_PC\Windows\HubWindow.UIHandlers.cs"
        };

        // Variation selector
        string emojiPattern = @"\uFE0F";

        int totalReplacements = 0;

        foreach (var file in files)
        {
            if (!File.Exists(file)) continue;

            string content = File.ReadAllText(file, Encoding.UTF8);
            int replacementsInFile = 0;

            if (file.EndsWith(".xaml"))
            {
                string newContent = Regex.Replace(content, emojiPattern, "");
                if (newContent != content) {
                    replacementsInFile++;
                    content = newContent;
                }
            }
            else
            {
                // C# files
                string[] lines = content.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
                for (int i = 0; i < lines.Length; i++)
                {
                    if (lines[i].TrimStart().StartsWith("//")) continue;

                    string originalLine = lines[i];
                    
                    if (!Regex.IsMatch(originalLine, emojiPattern)) continue;
                    
                    string newLine = Regex.Replace(originalLine, emojiPattern, "");
                    
                    newLine = Regex.Replace(newLine, @"\s+!""", @"!""");
                    newLine = Regex.Replace(newLine, @"\s+\.""", @".""");
                    newLine = Regex.Replace(newLine, @"\s+\?", @"\?");
                    newLine = Regex.Replace(newLine, @"\s+""", @"""");
                    newLine = Regex.Replace(newLine, @"""\s+", @"""");
                    
                    if (originalLine != newLine)
                    {
                        replacementsInFile++;
                        lines[i] = newLine;
                    }
                }
                content = string.Join(Environment.NewLine, lines);
            }

            if (replacementsInFile > 0)
            {
                File.WriteAllText(file, content, Encoding.UTF8);
            }
            Console.WriteLine($"{Path.GetFileName(file)}: {replacementsInFile} replacements");
            totalReplacements += replacementsInFile;
        }

        Console.WriteLine($"Total: {totalReplacements}");
    }
}
