using System;
using System.Globalization;
using System.IO;

namespace FlyShelf.Classes
{
    public static class FormatHelper
    {
        public static string FormatSize(long bytes)
        {
            if (bytes >= 1024 * 1024)
                return $"{(double)bytes / (1024 * 1024):F1} MB";
            if (bytes >= 1024)
                return $"{(double)bytes / 1024:F0} KB";
            return $"{bytes} B";
        }

        public static string GetFileTypeFriendly(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return "File";
            string ext = System.IO.Path.GetExtension(fileName).ToLowerInvariant();
            return ext switch
            {
                ".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp" or ".gif" or ".svg" or ".ico" => "Image",
                ".pdf" => "PDF",
                ".md" or ".markdown" => "Markdown",
                ".docx" or ".doc" or ".rtf" or ".txt" or ".odt" => "Document",
                ".xlsx" or ".xls" or ".csv" or ".ods" => "Spreadsheet",
                ".pptx" or ".ppt" or ".key" or ".odp" => "Presentation",
                ".zip" or ".rar" or ".7z" or ".tar" or ".gz" or ".bz2" or ".xz" or ".iso" => "Archive",
                ".mp3" or ".wav" or ".m4a" or ".ogg" or ".flac" or ".aac" => "Audio",
                ".mp4" or ".mkv" or ".avi" or ".mov" or ".webm" => "Video",
                ".apk" or ".aab" or ".xapk" or ".apks" => "Android App",
                ".exe" or ".msi" => "Application",
                ".py" or ".js" or ".ts" or ".cs" or ".cpp" or ".c" or ".java" or ".json" or ".xml" or ".html" or ".css" => "Code",
                _ => "File"
            };
        }

        // ═══ [FIX M-58]: Consolidated from 5 duplicate implementations ═══
        // Previously in: ClipboardItem.Constructors.cs, LanTransferSession.cs,
        //   NetworkFileQueue.cs, TransferHistory.cs, FlyShelfViewModel.cs

        /// <summary>
        /// Formats a byte count into a human-readable string (B, KB, MB, GB).
        /// </summary>
        public static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return string.Create(CultureInfo.InvariantCulture, $"{bytes} B");
            if (bytes < 1_048_576) return string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0:F1} KB");
            if (bytes < 1_073_741_824) return string.Create(CultureInfo.InvariantCulture, $"{bytes / 1_048_576.0:F1} MB");
            return string.Create(CultureInfo.InvariantCulture, $"{bytes / 1_073_741_824.0:F2} GB");
        }

        /// <summary>
        /// Formats a bytes-per-second speed into a human-readable string (KB/s, MB/s, GB/s).
        /// </summary>
        public static string FormatSpeed(double bytesPerSecond)
        {
            if (bytesPerSecond <= 0) return "—";
            if (bytesPerSecond < 1_048_576) return string.Create(CultureInfo.InvariantCulture, $"{bytesPerSecond / 1024.0:F0} KB/s");
            if (bytesPerSecond < 1_073_741_824) return string.Create(CultureInfo.InvariantCulture, $"{bytesPerSecond / 1_048_576.0:F1} MB/s");
            return string.Create(CultureInfo.InvariantCulture, $"{bytesPerSecond / 1_073_741_824.0:F2} GB/s");
        }
    }
}
