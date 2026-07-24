using System;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Diagnostics;

namespace FlyShelf.Classes
{
    public static class RuntimeHost
    {
        public static string ExecutionDir { get; private set; }

        public static void Initialize()
        {
            string basePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FlyShelf", "RuntimeCore");
            ExecutionDir = basePath;

            string currentVer = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";

            string verFile = Path.Combine(ExecutionDir, "version.txt");
            try
            {
                if (File.ReadAllText(verFile).Trim() == currentVer)
                    return; // Already extracted
            }
            catch { /* File doesn't exist or can't be read — proceed with extraction */ }

            // Version changed or clean install. Rebuild payload directories natively.
            try { if (Directory.Exists(ExecutionDir)) Directory.Delete(ExecutionDir, true); } catch { } // Best-effort: failure is acceptable
            Directory.CreateDirectory(ExecutionDir);

            ExtractResource("FlyShelf.WebClient.zip", Path.Combine(ExecutionDir, "Resources", "WebClient"));

            try { File.WriteAllText(verFile, currentVer); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"RuntimeHost version write failed: {ex.Message}"); }
        }

        private static void ExtractResource(string resourceName, string outDir)
        {
            if (!Directory.Exists(outDir)) Directory.CreateDirectory(outDir);

            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName))
            {
                if (stream == null) return;
                
                string tempZip = Path.Combine(ExecutionDir, resourceName);
                using (var fs = new FileStream(tempZip, FileMode.Create))
                {
                    stream.CopyTo(fs);
                }

                using (var archive = ZipFile.OpenRead(tempZip))
                {
                    foreach (var entry in archive.Entries)
                    {
                        string destinationPath = Path.GetFullPath(Path.Combine(outDir, entry.FullName));
                        if (destinationPath.StartsWith(Path.GetFullPath(outDir), StringComparison.OrdinalIgnoreCase))
                        {
                            if (string.IsNullOrEmpty(entry.Name) || entry.FullName.EndsWith('/')) 
                            {
                                Directory.CreateDirectory(destinationPath);
                            }
                            else
                            {
                                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath));
                                entry.ExtractToFile(destinationPath, true);
                            }
                        }
                    }
                }
                try { File.Delete(tempZip); } catch { } // Best-effort: failure is acceptable
            }
        }
    }
}
