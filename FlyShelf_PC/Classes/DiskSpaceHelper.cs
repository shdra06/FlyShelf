using System;
using System.IO;

namespace FlyShelf.Classes
{
    /// <summary>
    /// Utility for checking available disk space before file writes.
    /// Prevents data loss from write failures on nearly-full drives.
    /// </summary>
    internal static class DiskSpaceHelper
    {
        /// <summary>
        /// Checks whether the drive containing <paramref name="filePath"/> has at least
        /// <paramref name="requiredBytes"/> of free space available.
        /// Returns true if sufficient space is available or if the check cannot be performed
        /// (e.g. network path, permission error) — never blocks a write on a failed check.
        /// </summary>
        /// <param name="filePath">The target file path whose drive will be checked.</param>
        /// <param name="requiredBytes">Minimum required free bytes (default: 10 MB).</param>
        /// <returns>True if the drive has sufficient space or the check fails gracefully.</returns>
        public static bool HasSufficientDiskSpace(string filePath, long requiredBytes = 10_000_000)
        {
            try
            {
                if (string.IsNullOrEmpty(filePath))
                    return true; // Can't check — don't block

                string? root = Path.GetPathRoot(filePath);
                if (string.IsNullOrEmpty(root))
                    return true; // UNC or relative path — can't check, don't block

                var drive = new DriveInfo(root);
                if (!drive.IsReady)
                    return true; // Drive not ready (e.g. removable media) — don't block

                return drive.AvailableFreeSpace > requiredBytes;
            }
            catch
            {
                // If we can't determine disk space (permissions, invalid path, etc.),
                // don't prevent the write — let it fail naturally with a proper IO error
                return true;
            }
        }
    }
}
