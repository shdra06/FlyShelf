using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Collections.Generic;

namespace AdvanceClip.Classes
{
    public static class ShellExplorerHelper
    {
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr ILCreateFromPath([MarshalAs(UnmanagedType.LPWStr)] string pszPath);

        [DllImport("shell32.dll")]
        private static extern void ILFree(IntPtr pidl);

        [DllImport("shell32.dll")]
        private static extern int SHOpenFolderAndSelectItems(
            IntPtr pidlFolder,
            uint cidl,
            IntPtr[] apidl,
            uint dwFlags);

        public static void OpenFolderAndSelectFiles(string parentFolder, string[] fileNames)
        {
            if (string.IsNullOrEmpty(parentFolder) || !Directory.Exists(parentFolder)) return;

            IntPtr folderPidl = ILCreateFromPath(parentFolder);
            if (folderPidl == IntPtr.Zero) return;

            try
            {
                var pidlList = new List<IntPtr>();
                foreach (var name in fileNames)
                {
                    string fullPath = Path.Combine(parentFolder, name);
                    IntPtr filePidl = ILCreateFromPath(fullPath);
                    if (filePidl != IntPtr.Zero)
                    {
                        pidlList.Add(filePidl);
                    }
                }

                if (pidlList.Count > 0)
                {
                    SHOpenFolderAndSelectItems(folderPidl, (uint)pidlList.Count, pidlList.ToArray(), 0);
                }
                else
                {
                    // Fallback to opening parent folder
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = parentFolder,
                        UseShellExecute = true
                    });
                }

                foreach (var pidl in pidlList)
                {
                    ILFree(pidl);
                }
            }
            catch (Exception ex)
            {
                Logger.LogAction("SHELL_EXPLORER_ERROR", ex.Message);
            }
            finally
            {
                ILFree(folderPidl);
            }
        }

        public static void OpenFilesAndSelect(string[] filePaths)
        {
            if (filePaths == null || filePaths.Length == 0) return;

            try
            {
                // Group files by parent directory
                var groups = filePaths
                    .Where(f => !string.IsNullOrEmpty(f))
                    .GroupBy(f => {
                        try
                        {
                            if (Directory.Exists(f))
                            {
                                return Path.GetDirectoryName(f.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                            }
                            return Path.GetDirectoryName(f);
                        }
                        catch
                        {
                            return null;
                        }
                    })
                    .Where(g => !string.IsNullOrEmpty(g.Key))
                    .ToList();

                foreach (var group in groups)
                {
                    string parentDir = group.Key!;
                    var fileNames = group.Select(f => Path.GetFileName(f)).ToArray();
                    OpenFolderAndSelectFiles(parentDir, fileNames);
                }
            }
            catch (Exception ex)
            {
                Logger.LogAction("SHELL_EXPLORER_ERROR", ex.Message);
            }
        }
    }
}
