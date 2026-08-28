// ═══════════════════════════════════════════════════════════════════════
// PdfSecurityHelper.cs — PDF Password Protection & Decryption Engine
// Supports 128-bit/256-bit AES encryption with User & Owner passwords.
// ═══════════════════════════════════════════════════════════════════════

using System;
using System.IO;
using System.Threading.Tasks;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using PdfSharp.Pdf.Security;

namespace FlyShelf.Classes.Utils
{
    public static class PdfSecurityHelper
    {
        /// <summary>
        /// Encrypts a PDF with a user password (required to open) and/or owner password (permissions).
        /// </summary>
        public static async Task<string> ProtectPdfAsync(
            string sourcePdfPath,
            string userPassword,
            string ownerPassword = null,
            string outputPath = null)
        {
            if (string.IsNullOrEmpty(sourcePdfPath) || !File.Exists(sourcePdfPath))
                throw new FileNotFoundException("Source PDF not found", sourcePdfPath);

            if (string.IsNullOrEmpty(userPassword) && string.IsNullOrEmpty(ownerPassword))
                throw new ArgumentException("At least one password must be provided.");

            if (string.IsNullOrEmpty(outputPath))
            {
                string dir = Path.GetDirectoryName(sourcePdfPath) ?? Path.GetTempPath();
                string name = Path.GetFileNameWithoutExtension(sourcePdfPath);
                outputPath = Path.Combine(dir, $"{name}_Protected.pdf");
            }

            bool success = await Task.Run(() =>
            {
                try
                {
                    using (var inputDoc = PdfReader.Open(sourcePdfPath, PdfDocumentOpenMode.Import))
                    using (var outputDoc = new PdfDocument())
                    {
                        var securitySettings = outputDoc.SecuritySettings;
                        if (!string.IsNullOrEmpty(userPassword))
                            securitySettings.UserPassword = userPassword;
                        if (!string.IsNullOrEmpty(ownerPassword))
                            securitySettings.OwnerPassword = ownerPassword;
                        else
                            securitySettings.OwnerPassword = userPassword;

                        for (int i = 0; i < inputDoc.PageCount; i++)
                        {
                            outputDoc.AddPage(inputDoc.Pages[i]);
                        }

                        outputDoc.Save(outputPath);
                    }
                    return true;
                }
                catch (Exception ex)
                {
                    Logger.LogAction("PDF_PROTECT_ERR", ex.Message);
                    return false;
                }
            });

            if (!success || !File.Exists(outputPath))
                throw new InvalidOperationException("Failed to protect PDF.");

            return outputPath;
        }

        /// <summary>
        /// Unlocks and saves an unencrypted copy of a password-protected PDF.
        /// </summary>
        public static async Task<string> UnlockPdfAsync(
            string sourcePdfPath,
            string password,
            string outputPath = null)
        {
            if (string.IsNullOrEmpty(sourcePdfPath) || !File.Exists(sourcePdfPath))
                throw new FileNotFoundException("Source PDF not found", sourcePdfPath);

            if (string.IsNullOrEmpty(outputPath))
            {
                string dir = Path.GetDirectoryName(sourcePdfPath) ?? Path.GetTempPath();
                string name = Path.GetFileNameWithoutExtension(sourcePdfPath);
                outputPath = Path.Combine(dir, $"{name}_Unlocked.pdf");
            }

            bool success = await Task.Run(() =>
            {
                try
                {
                    using (var inputDoc = PdfReader.Open(sourcePdfPath, password ?? "", PdfDocumentOpenMode.Import))
                    using (var outputDoc = new PdfDocument())
                    {
                        for (int i = 0; i < inputDoc.PageCount; i++)
                        {
                            outputDoc.AddPage(inputDoc.Pages[i]);
                        }
                        outputDoc.Save(outputPath);
                    }
                    return true;
                }
                catch (Exception ex)
                {
                    Logger.LogAction("PDF_UNLOCK_ERR", ex.Message);
                    return false;
                }
            });

            if (!success || !File.Exists(outputPath))
                throw new InvalidOperationException("Failed to unlock PDF. Invalid password or corrupted file.");

            return outputPath;
        }
    }
}
