using System;
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
                ".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp" or ".gif" => "Image",
                ".pdf" => "PDF",
                ".docx" or ".doc" or ".rtf" or ".txt" => "Document",
                ".xlsx" or ".xls" or ".csv" => "Spreadsheet",
                ".pptx" or ".ppt" => "Presentation",
                ".zip" or ".rar" or ".7z" or ".tar" or ".gz" => "Archive",
                ".mp3" or ".wav" or ".m4a" or ".ogg" => "Audio",
                ".mp4" or ".mkv" or ".avi" or ".mov" => "Video",
                ".apk" => "Android App",
                _ => "File"
            };
        }
    }
}
