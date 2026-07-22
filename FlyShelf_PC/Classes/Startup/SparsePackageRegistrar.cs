using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using Windows.Management.Deployment;

namespace FlyShelf.Classes
{
    public static class SparsePackageRegistrar
    {
        private const string PackageName = "Flyshelf.FlyShelfSparse";
        private const string PackageFamilyName = "Flyshelf.FlyShelfSparse_yg7g8e4a5k38g"; // Checked by name filter instead of hardcoded hash to be safe

        public static void EnsureRegistered(string[] args)
        {
            // If already packaged (meaning sparse package is active or running MSIX), do nothing
            if (StartupHelper.IsPackaged())
                return;

            // Prevent infinite relaunch loop in case of mismatch
            if (args.Any(arg => arg.Equals("--no-sparse-relaunch", StringComparison.OrdinalIgnoreCase)))
            {
                Logger.LogAction("SPARSE", "Bypassing sparse registration relaunch to prevent loop.");
                return;
            }

            // PERF: Fire-and-forget — sparse package registration doesn't need to complete before app starts.
            // Errors are logged via ContinueWith since exceptions in fire-and-forget tasks are unobserved.
            _ = Task.Run(() => EnsureRegisteredInternalAsync(args)).ContinueWith(t =>
            {
                if (t.Exception != null)
                    Logger.LogAction("SPARSE_ERR", $"Error in EnsureRegistered: {t.Exception.InnerException?.Message ?? t.Exception.Message}");
            }, TaskContinuationOptions.OnlyOnFaulted);
        }

        private static async Task EnsureRegisteredInternalAsync(string[] args)
        {
            try
            {
                var pm = new PackageManager();
                
                // Query registered packages for the current user matching our sparse name
                var packages = pm.FindPackagesForUser(string.Empty).Where(p => p.Id.Name.Equals(PackageName, StringComparison.OrdinalIgnoreCase));
                
                if (packages.Any())
                {
                    // Sparse package is registered, but we are running unpackaged.
                    // This means we need to relaunch under the package identity!
                    Logger.LogAction("SPARSE", "Sparse package is registered but app is running unpackaged. Relaunching with identity...");
                    RelaunchWithIdentity(args);
                    return;
                }

                Logger.LogAction("SPARSE", "Sparse package registration not found. Initiating auto-registration...");

                // 1. Extract embedded MSIX and Certificate
                string appDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FlyShelf");
                string msixPath = Path.Combine(appDataDir, "FlyShelfSparse.msix");
                string certPath = Path.Combine(appDataDir, "FlyShelfSparse.cer");

                if (!Directory.Exists(appDataDir))
                {
                    Directory.CreateDirectory(appDataDir);
                }

                // Extract resources from assembly
                bool msixExtracted = ExtractResource("FlyShelf.Resources.FlyShelfSparse.msix", msixPath);
                bool certExtracted = ExtractResource("FlyShelf.Resources.FlyShelfSparse.cer", certPath);

                if (!msixExtracted)
                {
                    Logger.LogAction("SPARSE_ERR", "Resource extraction failed for MSIX package. Cannot proceed.");
                    return;
                }

                if (!certExtracted)
                {
                    Logger.LogAction("SPARSE", "Certificate resource not found or extraction skipped. Continuing registration.");
                }

                // 2. Install Certificate to CurrentUser\TrustedPeople silently (if extracted)
                if (certExtracted && File.Exists(certPath))
                {
                    try
                    {
                        using (var store = new X509Store(StoreName.TrustedPeople, StoreLocation.CurrentUser))
                        {
                            store.Open(OpenFlags.ReadWrite);
#pragma warning disable SYSLIB0057
                            using (var cert = new X509Certificate2(certPath))
#pragma warning restore SYSLIB0057
                            {
                                bool exists = store.Certificates.Find(X509FindType.FindByThumbprint, cert.Thumbprint, false).Count > 0;
                                if (!exists)
                                {
                                    store.Add(cert);
                                    Logger.LogAction("SPARSE", "Successfully installed developer certificate to CurrentUser\\TrustedPeople.");
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.LogAction("SPARSE", $"Certificate installation skipped or failed (common if already trusted): {ex.Message}");
                    }
                }

                // 3. Register sparse package pointing to AppContext.BaseDirectory
                try
                {
                    var externalLocation = AppContext.BaseDirectory;
                    var packageUri = new Uri(msixPath);
                    var externalUri = new Uri(externalLocation);

                    var options = new AddPackageOptions();
                    options.ExternalLocationUri = externalUri;

                    Logger.LogAction("SPARSE", $"Registering sparse package. Location: {externalLocation}");
                    
                    var deploymentOperation = pm.AddPackageByUriAsync(packageUri, options);
                    var result = await deploymentOperation;

                    if (result.ExtendedErrorCode != null)
                    {
                        throw new Exception($"Deployment failed with HRESULT: {result.ExtendedErrorCode.HResult:X} ({result.ErrorText})");
                    }

                    Logger.LogAction("SPARSE", "Sparse package registered successfully! Relaunching with identity...");
                    RelaunchWithIdentity(args);
                }
                catch (Exception ex)
                {
                    Logger.LogAction("SPARSE_ERR", $"Deployment registration failed: {ex.Message}. Attempting elevated registration...");
                    if (RunElevatedRegistration(msixPath, certPath))
                    {
                        Logger.LogAction("SPARSE", "Elevated registration succeeded! Relaunching with identity...");
                        RelaunchWithIdentity(args);
                    }
                    else
                    {
                        Logger.LogAction("SPARSE_ERR", "Elevated registration failed or was cancelled by user.");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogAction("SPARSE_ERR", $"General error in EnsureRegisteredInternalAsync: {ex.Message}");
            }
        }

        private static bool ExtractResource(string resourceName, string outputPath)
        {
            try
            {
                var assembly = Assembly.GetExecutingAssembly();
                using (var stream = assembly.GetManifestResourceStream(resourceName))
                {
                    if (stream == null)
                    {
                        // Try scanning all resource names to find match case-insensitively
                        string actualName = assembly.GetManifestResourceNames()
                            .FirstOrDefault(n => n.Equals(resourceName, StringComparison.OrdinalIgnoreCase));
                        
                        if (actualName == null) return false;
                        
                        using (var fallbackStream = assembly.GetManifestResourceStream(actualName))
                        {
                            if (fallbackStream == null) return false;
                            using (var fileStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write))
                            {
                                fallbackStream.CopyTo(fileStream);
                            }
                            return true;
                        }
                    }

                    using (var fileStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write))
                    {
                        stream.CopyTo(fileStream);
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                Logger.LogAction("SPARSE_ERR", $"ExtractResource exception ({resourceName}): {ex.Message}");
                return false;
            }
        }

        private static void RelaunchWithIdentity(string[] args)
        {
            try
            {
                string exePath = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "FlyShelf.exe");
                
                // Construct arguments preserving original ones, and ensure --no-sparse-relaunch is present
                var newArgsList = args.Where(arg => !arg.Equals("--no-sparse-relaunch", StringComparison.OrdinalIgnoreCase)).ToList();
                newArgsList.Add("--no-sparse-relaunch");
                string combinedArgs = string.Join(" ", newArgsList.Select(a => a.Contains(' ') ? $"\"{a}\"" : a));
                
                var startInfo = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = combinedArgs,
                    UseShellExecute = true
                };
                
                Process.Start(startInfo);
                
                // Shutdown current instance
                System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
                {
                    System.Windows.Application.Current?.Shutdown();
                });
            }
            catch (Exception ex)
            {
                Logger.LogAction("SPARSE_ERR", $"Failed to relaunch: {ex.Message}");
            }
        }

        private static bool RunElevatedRegistration(string msixPath, string certPath)
        {
            try
            {
                string baseDir = AppContext.BaseDirectory.TrimEnd('\\');
                string escapedMsixPath = msixPath.Replace("'", "''");
                string escapedBaseDir = baseDir.Replace("'", "''");

                string psCommand = "";
                if (File.Exists(certPath))
                {
                    string escapedCertPath = certPath.Replace("'", "''");
                    psCommand += $"Import-Certificate -FilePath '{escapedCertPath}' -CertStoreLocation Cert:\\LocalMachine\\TrustedPeople; ";
                }
                
                psCommand += $"Add-AppxPackage -Path '{escapedMsixPath}' -ExternalLocation '{escapedBaseDir}'";

                var startInfo = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -Command \"{psCommand}\"",
                    Verb = "runas",
                    UseShellExecute = true,
                    CreateNoWindow = true
                };

                using (var process = Process.Start(startInfo))
                {
                    process.WaitForExit();
                    return process.ExitCode == 0;
                }
            }
            catch (Exception ex)
            {
                Logger.LogAction("SPARSE_ERR", $"Elevated registration failed to start: {ex.Message}");
                return false;
            }
        }
    }
}
