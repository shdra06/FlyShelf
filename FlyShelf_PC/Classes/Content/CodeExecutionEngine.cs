using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using FlyShelf.ViewModels;

namespace FlyShelf.Classes
{
    /// <summary>
    /// Supported programming languages for auto-compilation and execution.
    /// </summary>
    public enum CodeLanguage
    {
        Cpp,
        C,
        Python,
        JavaScript,
        TypeScript,
        CSharp,
        Java,
        Rust,
        Go,
        Batch,
        PowerShell,
        ShellCommand,
        Unknown
    }

    /// <summary>
    /// Intelligent multi-language compilation and execution engine for FlyShelf.
    /// Automatically detects source code language, writes to an isolated runner workspace,
    /// compiles with native toolchains (g++, gcc, clang, MSVC, rustc, javac, csc, etc.),
    /// and launches an interactive terminal with full stdin/stdout support.
    /// </summary>
    public static class CodeExecutionEngine
    {
        private static readonly Regex _rxCppInclude = new(@"#include\s*<[a-zA-Z0-9_./]+>", RegexOptions.Compiled);
        private static readonly Regex _rxCppFeatures = new(@"\b(std::|using\s+namespace\s+std|cout\s*<<|cin\s*>>|template\s*<|nullptr|constexpr|class\s+[A-Z]\w*)\b", RegexOptions.Compiled);
        private static readonly Regex _rxCInclude = new(@"#include\s*<(stdio\.h|stdlib\.h|string\.h|math\.h|time\.h|stdbool\.h|stdint\.h|assert\.h)>", RegexOptions.Compiled);
        private static readonly Regex _rxCFeatures = new(@"\b(printf\s*\(|scanf\s*\(|malloc\s*\(|free\s*\(|puts\s*\()\b", RegexOptions.Compiled);
        private static readonly Regex _rxPython = new(@"\b(def\s+\w+\s*\(|import\s+[a-zA-Z0-9_.]+|from\s+[a-zA-Z0-9_.]+\s+import|if\s+__name__\s*==\s*['""]__main__['""]|elif\s+|print\s*\(|lambda\s+)\b", RegexOptions.Compiled);
        private static readonly Regex _rxJavaClass = new(@"\b(public\s+)?class\s+([A-Za-z0-9_]+)", RegexOptions.Compiled);
        private static readonly Regex _rxJavaFeatures = new(@"\b(public\s+static\s+void\s+main\s*\(|System\.out\.print(ln)?\s*\(|@Override)\b", RegexOptions.Compiled);
        private static readonly Regex _rxCSharp = new(@"\b(using\s+System(\.[a-zA-Z0-9_]+)?;|Console\.(Write(Line)?|Read(Line)?)\s*\(|namespace\s+[A-Za-z0-9_.]+|public\s+class\s+Program|static\s+void\s+Main\s*\()\b", RegexOptions.Compiled);
        private static readonly Regex _rxRust = new(@"\b(fn\s+main\s*\(|println!\s*\(|eprintln!\s*\(|let\s+mut\s+|use\s+std::)\b", RegexOptions.Compiled);
        private static readonly Regex _rxGo = new(@"\b(package\s+main|func\s+main\s*\(|fmt\.(Print(ln|f)?))\b", RegexOptions.Compiled);
        private static readonly Regex _rxJsTs = new(@"\b(console\.log\s*\(|const\s+\w+\s*=|let\s+\w+\s*=|function\s+\w+\s*\(|module\.exports|require\s*\(|import\s+.*\s+from)\b", RegexOptions.Compiled);
        private static readonly Regex _rxPowerShell = new(@"\b(Write-Host|Get-ChildItem|Get-Process|Get-Service|param\s*\(|\[CmdletBinding\(\)\]|\$[A-Za-z0-9_]+)\b", RegexOptions.Compiled);
        private static readonly Regex _rxBatch = new(@"\b(@echo\s+off|setlocal|endlocal|echo\.|goto\s+:\w+)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// Detects the programming language from an item's file path, extension, and content.
        /// </summary>
        public static CodeLanguage DetectLanguage(ClipboardItem item)
        {
            if (item == null) return CodeLanguage.Unknown;

            string ext = "";
            if (!string.IsNullOrEmpty(item.FilePath))
            {
                try { ext = Path.GetExtension(item.FilePath).ToLowerInvariant(); } catch { }
            }
            if (string.IsNullOrEmpty(ext) && !string.IsNullOrEmpty(item.Extension))
            {
                ext = item.Extension.StartsWith(".", StringComparison.Ordinal) ? item.Extension.ToLowerInvariant() : "." + item.Extension.ToLowerInvariant();
            }

            // 1. Extension-based identification
            switch (ext)
            {
                case ".cpp" or ".cc" or ".cxx" or ".hpp" or ".hxx" or ".c++":
                    return CodeLanguage.Cpp;
                case ".c" or ".h":
                    // If header or .c has C++ keywords, treat as C++
                    if (!string.IsNullOrEmpty(item.RawContent) && (_rxCppInclude.IsMatch(item.RawContent) || _rxCppFeatures.IsMatch(item.RawContent)))
                        return CodeLanguage.Cpp;
                    return CodeLanguage.C;
                case ".py" or ".pyw" or ".python":
                    return CodeLanguage.Python;
                case ".js" or ".mjs" or ".cjs":
                    return CodeLanguage.JavaScript;
                case ".ts" or ".tsx":
                    return CodeLanguage.TypeScript;
                case ".cs" or ".c#":
                    return CodeLanguage.CSharp;
                case ".java":
                    return CodeLanguage.Java;
                case ".rs" or ".rust":
                    return CodeLanguage.Rust;
                case ".go":
                    return CodeLanguage.Go;
                case ".bat" or ".cmd":
                    return CodeLanguage.Batch;
                case ".ps1" or ".psm1":
                    return CodeLanguage.PowerShell;
            }

            // 2. Content-based heuristics for raw snippets
            string content = (item.RawContent ?? item.FileName ?? "").Trim();
            if (string.IsNullOrEmpty(content)) return CodeLanguage.Unknown;

            string sample = content.Length > 8000 ? content[..8000] : content;

            // C++ vs C
            if (_rxCppInclude.IsMatch(sample) || _rxCppFeatures.IsMatch(sample))
                return CodeLanguage.Cpp;
            if (_rxCInclude.IsMatch(sample) || (_rxCFeatures.IsMatch(sample) && sample.Contains("int main", StringComparison.Ordinal)))
                return CodeLanguage.C;

            // C#
            if (_rxCSharp.IsMatch(sample))
                return CodeLanguage.CSharp;

            // Java
            if (_rxJavaFeatures.IsMatch(sample) || (_rxJavaClass.IsMatch(sample) && sample.Contains("class ", StringComparison.Ordinal)))
                return CodeLanguage.Java;

            // Python
            if (_rxPython.IsMatch(sample))
                return CodeLanguage.Python;

            // Rust
            if (_rxRust.IsMatch(sample))
                return CodeLanguage.Rust;

            // Go
            if (_rxGo.IsMatch(sample))
                return CodeLanguage.Go;

            // TypeScript / JavaScript
            if (ext == ".ts" || ext == ".tsx" || sample.Contains(": string", StringComparison.Ordinal) || sample.Contains(": number", StringComparison.Ordinal) || sample.Contains("interface ", StringComparison.Ordinal))
                return CodeLanguage.TypeScript;
            if (_rxJsTs.IsMatch(sample))
                return CodeLanguage.JavaScript;

            // PowerShell
            if (_rxPowerShell.IsMatch(sample))
                return CodeLanguage.PowerShell;

            // Batch
            if (_rxBatch.IsMatch(sample))
                return CodeLanguage.Batch;

            return CodeLanguage.ShellCommand;
        }

        /// <summary>
        /// Compiles and runs the code or script in an interactive terminal window.
        /// </summary>
        public static void Execute(ClipboardItem item)
        {
#if MSIX_STORE
            FlyShelf.Windows.ToastWindow.ShowToast("Terminal execution is not available in the Store version.");
            return;
#else
            if (item == null) return;
            string rawContent = item.RawContent ?? "";
            string filePath = item.FilePath ?? "";

            if (string.IsNullOrEmpty(rawContent) && string.IsNullOrEmpty(filePath))
                return;

            try
            {
                CodeLanguage lang = DetectLanguage(item);

                // Create a clean isolated runner directory
                string runnerDir = Path.Combine(Path.GetTempPath(), "FlyShelf", "CodeRunner", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(runnerDir);

                string runnerBatPath = Path.Combine(runnerDir, "flyshelf_runner.bat");
                string batContent = GenerateRunnerScript(lang, item, runnerDir);

                File.WriteAllText(runnerBatPath, batContent);

                var startInfo = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/k \"{runnerBatPath}\"",
                    WorkingDirectory = (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
                        ? (Path.GetDirectoryName(filePath) ?? runnerDir)
                        : runnerDir,
                    UseShellExecute = true,
                    CreateNoWindow = false
                };

                Logger.LogAction("CODE_EXEC", $"Running {lang} in terminal. Runner: {runnerBatPath}");
                _ = Task.Run(() => { try { Process.Start(startInfo); } catch (Exception ex) { Logger.LogAction("CODE_EXEC_ERR", ex.Message); } });
            }
            catch (Exception ex)
            {
                Logger.LogAction("CODE_EXEC_ERR", $"Execution setup failed: {ex.Message}");
                System.Windows.MessageBox.Show($"Failed to prepare code runner: {ex.Message}", "FlyShelf Code Runner", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            }
#endif
        }

        private static string GenerateRunnerScript(CodeLanguage lang, ClipboardItem item, string runnerDir)
        {
            string rawContent = item.RawContent ?? "";
            bool isPhysicalFile = !string.IsNullOrEmpty(item.FilePath) && File.Exists(item.FilePath);

            switch (lang)
            {
                case CodeLanguage.Cpp:
                    return GenerateCppRunner(item, runnerDir, isPhysicalFile, rawContent);

                case CodeLanguage.C:
                    return GenerateCRunner(item, runnerDir, isPhysicalFile, rawContent);

                case CodeLanguage.Python:
                    return GeneratePythonRunner(item, runnerDir, isPhysicalFile, rawContent);

                case CodeLanguage.Java:
                    return GenerateJavaRunner(item, runnerDir, isPhysicalFile, rawContent);

                case CodeLanguage.CSharp:
                    return GenerateCSharpRunner(item, runnerDir, isPhysicalFile, rawContent);

                case CodeLanguage.Rust:
                    return GenerateRustRunner(item, runnerDir, isPhysicalFile, rawContent);

                case CodeLanguage.Go:
                    return GenerateGoRunner(item, runnerDir, isPhysicalFile, rawContent);

                case CodeLanguage.JavaScript:
                    return GenerateJsRunner(item, runnerDir, isPhysicalFile, rawContent);

                case CodeLanguage.TypeScript:
                    return GenerateTsRunner(item, runnerDir, isPhysicalFile, rawContent);

                case CodeLanguage.PowerShell:
                    return GeneratePowerShellRunner(item, runnerDir, isPhysicalFile, rawContent);

                case CodeLanguage.Batch:
                case CodeLanguage.ShellCommand:
                default:
                    return GenerateBatchRunner(item, runnerDir, isPhysicalFile, rawContent);
            }
        }

        private static string GenerateCppRunner(ClipboardItem item, string runnerDir, bool isPhysicalFile, string rawContent)
        {
            string sourcePath = isPhysicalFile
                ? item.FilePath
                : Path.Combine(runnerDir, "main.cpp");

            if (!isPhysicalFile)
            {
                File.WriteAllText(sourcePath, rawContent);
            }

            string exePath = Path.Combine(runnerDir, "main.exe");

            return $@"@echo off
chcp 65001 >nul
cls
title FlyShelf — Compiling & Running C++
echo ================================================================
echo   FlyShelf Developer Engine — C++ Runner
echo ================================================================
echo.

:: 1. Check for g++ (MinGW / GCC)
where g++ >nul 2>&1
if not errorlevel 1 (
    echo [Compiler] Using g++ (MinGW / GCC)
    echo [Compiling] g++ -O2 -std=c++17 ""{sourcePath}"" -o ""{exePath}""
    echo.
    g++ -O2 -std=c++17 ""{sourcePath}"" -o ""{exePath}""
    if errorlevel 1 (
        echo.
        echo [FlyShelf Error] Compilation failed! See errors above.
        goto :finish
    )
    goto :run_program
)

:: 2. Check for clang++ (LLVM)
where clang++ >nul 2>&1
if not errorlevel 1 (
    echo [Compiler] Using clang++ (LLVM)
    echo [Compiling] clang++ -O2 -std=c++17 ""{sourcePath}"" -o ""{exePath}""
    echo.
    clang++ -O2 -std=c++17 ""{sourcePath}"" -o ""{exePath}""
    if errorlevel 1 (
        echo.
        echo [FlyShelf Error] Compilation failed! See errors above.
        goto :finish
    )
    goto :run_program
)

:: 3. Check for cl.exe (MSVC)
where cl >nul 2>&1
if not errorlevel 1 (
    echo [Compiler] Using cl.exe (MSVC)
    echo [Compiling] cl.exe /nologo /EHsc /std:c++17 ""{sourcePath}"" /Fe:""{exePath}""
    echo.
    cl.exe /nologo /EHsc /std:c++17 ""{sourcePath}"" /Fe:""{exePath}""
    if errorlevel 1 (
        echo.
        echo [FlyShelf Error] Compilation failed! See errors above.
        goto :finish
    )
    goto :run_program
)

:: 4. Check for gcc
where gcc >nul 2>&1
if not errorlevel 1 (
    echo [Compiler] Using gcc
    echo [Compiling] gcc -O2 -std=c++17 ""{sourcePath}"" -o ""{exePath}"" -lstdc++
    echo.
    gcc -O2 -std=c++17 ""{sourcePath}"" -o ""{exePath}"" -lstdc++
    if errorlevel 1 (
        echo.
        echo [FlyShelf Error] Compilation failed! See errors above.
        goto :finish
    )
    goto :run_program
)

echo [FlyShelf Error] No C++ compiler (g++, clang++, or cl.exe) was found in system PATH.
echo.
echo To run and compile C++ code on Windows, install MinGW or LLVM:
echo   Option 1: winget install -e --id MSYS2.MSYS2 (MinGW)
echo   Option 2: winget install -e --id LLVM.LLVM (Clang)
echo   Option 3: Install Visual Studio with 'Desktop development with C++'
echo.
goto :finish

:run_program
echo.
echo ================================================================
echo   Program Output:
echo ================================================================
echo.
""{exePath}""
echo.
echo ================================================================
echo   Process exited with return code %errorlevel%.
echo ================================================================

:finish
echo.
pause
";
        }

        private static string GenerateCRunner(ClipboardItem item, string runnerDir, bool isPhysicalFile, string rawContent)
        {
            string sourcePath = isPhysicalFile
                ? item.FilePath
                : Path.Combine(runnerDir, "main.c");

            if (!isPhysicalFile)
            {
                File.WriteAllText(sourcePath, rawContent);
            }

            string exePath = Path.Combine(runnerDir, "main.exe");

            return $@"@echo off
chcp 65001 >nul
cls
title FlyShelf — Compiling & Running C
echo ================================================================
echo   FlyShelf Developer Engine — C Runner
echo ================================================================
echo.

:: 1. Check for gcc
where gcc >nul 2>&1
if not errorlevel 1 (
    echo [Compiler] Using gcc
    echo [Compiling] gcc -O2 ""{sourcePath}"" -o ""{exePath}""
    echo.
    gcc -O2 ""{sourcePath}"" -o ""{exePath}""
    if errorlevel 1 (
        echo.
        echo [FlyShelf Error] Compilation failed! See errors above.
        goto :finish
    )
    goto :run_program
)

:: 2. Check for clang
where clang >nul 2>&1
if not errorlevel 1 (
    echo [Compiler] Using clang
    echo [Compiling] clang -O2 ""{sourcePath}"" -o ""{exePath}""
    echo.
    clang -O2 ""{sourcePath}"" -o ""{exePath}""
    if errorlevel 1 (
        echo.
        echo [FlyShelf Error] Compilation failed! See errors above.
        goto :finish
    )
    goto :run_program
)

:: 3. Check for cl.exe
where cl >nul 2>&1
if not errorlevel 1 (
    echo [Compiler] Using cl.exe (MSVC)
    echo [Compiling] cl.exe /nologo ""{sourcePath}"" /Fe:""{exePath}""
    echo.
    cl.exe /nologo ""{sourcePath}"" /Fe:""{exePath}""
    if errorlevel 1 (
        echo.
        echo [FlyShelf Error] Compilation failed! See errors above.
        goto :finish
    )
    goto :run_program
)

echo [FlyShelf Error] No C compiler (gcc, clang, or cl.exe) was found in system PATH.
echo Install via: winget install -e --id MSYS2.MSYS2 or winget install -e --id LLVM.LLVM
goto :finish

:run_program
echo.
echo ================================================================
echo   Program Output:
echo ================================================================
echo.
""{exePath}""
echo.
echo ================================================================
echo   Process exited with return code %errorlevel%.
echo ================================================================

:finish
echo.
pause
";
        }

        private static string GeneratePythonRunner(ClipboardItem item, string runnerDir, bool isPhysicalFile, string rawContent)
        {
            string sourcePath = isPhysicalFile
                ? item.FilePath
                : Path.Combine(runnerDir, "script.py");

            if (!isPhysicalFile)
            {
                File.WriteAllText(sourcePath, rawContent);
            }

            return $@"@echo off
chcp 65001 >nul
cls
title FlyShelf — Running Python
echo ================================================================
echo   FlyShelf Developer Engine — Python Runner
echo ================================================================
echo.

where python >nul 2>&1
if not errorlevel 1 (
    echo [Interpreter] python ""{sourcePath}""
    echo.
    echo ================================================================
    echo   Program Output:
    echo ================================================================
    echo.
    python ""{sourcePath}""
    goto :after_run
)

where py >nul 2>&1
if not errorlevel 1 (
    echo [Interpreter] py ""{sourcePath}""
    echo.
    echo ================================================================
    echo   Program Output:
    echo ================================================================
    echo.
    py ""{sourcePath}""
    goto :after_run
)

where python3 >nul 2>&1
if not errorlevel 1 (
    echo [Interpreter] python3 ""{sourcePath}""
    echo.
    echo ================================================================
    echo   Program Output:
    echo ================================================================
    echo.
    python3 ""{sourcePath}""
    goto :after_run
)

echo [FlyShelf Error] Python was not found in system PATH.
echo Install Python via: winget install -e --id Python.Python.3.12
goto :finish

:after_run
echo.
echo ================================================================
echo   Process exited with return code %errorlevel%.
echo ================================================================

:finish
echo.
pause
";
        }

        private static string GenerateJavaRunner(ClipboardItem item, string runnerDir, bool isPhysicalFile, string rawContent)
        {
            string className = "Main";
            if (!string.IsNullOrEmpty(rawContent))
            {
                var match = _rxJavaClass.Match(rawContent);
                if (match.Success && match.Groups.Count >= 3 && !string.IsNullOrWhiteSpace(match.Groups[2].Value))
                {
                    className = match.Groups[2].Value.Trim();
                }
            }

            string sourcePath = isPhysicalFile
                ? item.FilePath
                : Path.Combine(runnerDir, className + ".java");

            if (!isPhysicalFile)
            {
                File.WriteAllText(sourcePath, rawContent);
            }

            return $@"@echo off
chcp 65001 >nul
cls
title FlyShelf — Compiling & Running Java
echo ================================================================
echo   FlyShelf Developer Engine — Java Runner
echo ================================================================
echo.

where java >nul 2>&1
if errorlevel 1 (
    echo [FlyShelf Error] Java runtime (java) was not found in system PATH.
    echo Install Java via: winget install -e --id Oracle.JDK.21
    goto :finish
)

:: Try Java 11+ single file direct execution
echo [Executing] java ""{sourcePath}""
echo.
echo ================================================================
echo   Program Output:
echo ================================================================
echo.
java ""{sourcePath}""
if not errorlevel 1 goto :after_run

:: If direct execution failed, attempt javac compile + java execution
echo.
echo [Direct execution returned %errorlevel%, trying javac compile...]
where javac >nul 2>&1
if not errorlevel 1 (
    javac ""{sourcePath}""
    if not errorlevel 1 (
        echo [Running compiled class] java -cp ""{runnerDir}"" {className}
        java -cp ""{runnerDir}"" {className}
    )
)

:after_run
echo.
echo ================================================================
echo   Process exited with return code %errorlevel%.
echo ================================================================

:finish
echo.
pause
";
        }

        private static string GenerateCSharpRunner(ClipboardItem item, string runnerDir, bool isPhysicalFile, string rawContent)
        {
            string sourcePath = isPhysicalFile
                ? item.FilePath
                : Path.Combine(runnerDir, "Program.cs");

            if (!isPhysicalFile)
            {
                File.WriteAllText(sourcePath, rawContent);
            }

            string exePath = Path.Combine(runnerDir, "Program.exe");

            return $@"@echo off
chcp 65001 >nul
cls
title FlyShelf — Compiling & Running C#
echo ================================================================
echo   FlyShelf Developer Engine — C# / .NET Runner
echo ================================================================
echo.

:: 1. Check for csc (C# Compiler) in PATH or Microsoft.NET Framework
set CSC_PATH=
where csc >nul 2>&1
if not errorlevel 1 (
    set CSC_PATH=csc
) else if exist ""%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"" (
    set ""CSC_PATH=%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\csc.exe""
)

if not ""%CSC_PATH%""=="""" (
    echo [Compiler] Using %CSC_PATH%
    echo [Compiling] ""%CSC_PATH%"" /nologo /out:""{exePath}"" ""{sourcePath}""
    echo.
    ""%CSC_PATH%"" /nologo /out:""{exePath}"" ""{sourcePath}""
    if errorlevel 1 (
        echo.
        echo [FlyShelf Error] Compilation failed! See errors above.
        goto :finish
    )
    goto :run_program
)

:: 2. Check for dotnet-script
where dotnet-script >nul 2>&1
if not errorlevel 1 (
    echo [Runner] Using dotnet-script
    echo.
    dotnet-script ""{sourcePath}""
    goto :finish
)

echo [FlyShelf Error] No C# compiler (csc.exe or dotnet SDK) was found.
echo Install .NET SDK via: winget install -e --id Microsoft.DotNet.SDK.8
goto :finish

:run_program
echo.
echo ================================================================
echo   Program Output:
echo ================================================================
echo.
""{exePath}""
echo.
echo ================================================================
echo   Process exited with return code %errorlevel%.
echo ================================================================

:finish
echo.
pause
";
        }

        private static string GenerateRustRunner(ClipboardItem item, string runnerDir, bool isPhysicalFile, string rawContent)
        {
            string sourcePath = isPhysicalFile
                ? item.FilePath
                : Path.Combine(runnerDir, "main.rs");

            if (!isPhysicalFile)
            {
                File.WriteAllText(sourcePath, rawContent);
            }

            string exePath = Path.Combine(runnerDir, "main.exe");

            return $@"@echo off
chcp 65001 >nul
cls
title FlyShelf — Compiling & Running Rust
echo ================================================================
echo   FlyShelf Developer Engine — Rust Runner
echo ================================================================
echo.

where rustc >nul 2>&1
if errorlevel 1 (
    echo [FlyShelf Error] rustc compiler was not found in system PATH.
    echo Install Rust via: winget install -e --id Rustlang.Rustup
    goto :finish
)

echo [Compiling] rustc ""{sourcePath}"" -o ""{exePath}""
echo.
rustc ""{sourcePath}"" -o ""{exePath}""
if errorlevel 1 (
    echo.
    echo [FlyShelf Error] Rust compilation failed! See errors above.
    goto :finish
)

echo.
echo ================================================================
echo   Program Output:
echo ================================================================
echo.
""{exePath}""
echo.
echo ================================================================
echo   Process exited with return code %errorlevel%.
echo ================================================================

:finish
echo.
pause
";
        }

        private static string GenerateGoRunner(ClipboardItem item, string runnerDir, bool isPhysicalFile, string rawContent)
        {
            string sourcePath = isPhysicalFile
                ? item.FilePath
                : Path.Combine(runnerDir, "main.go");

            if (!isPhysicalFile)
            {
                File.WriteAllText(sourcePath, rawContent);
            }

            return $@"@echo off
chcp 65001 >nul
cls
title FlyShelf — Running Go
echo ================================================================
echo   FlyShelf Developer Engine — Go Runner
echo ================================================================
echo.

where go >nul 2>&1
if errorlevel 1 (
    echo [FlyShelf Error] Go compiler was not found in system PATH.
    echo Install Go via: winget install -e --id GoLang.Go
    goto :finish
)

echo [Running] go run ""{sourcePath}""
echo.
echo ================================================================
echo   Program Output:
echo ================================================================
echo.
go run ""{sourcePath}""
echo.
echo ================================================================
echo   Process exited with return code %errorlevel%.
echo ================================================================

:finish
echo.
pause
";
        }

        private static string GenerateJsRunner(ClipboardItem item, string runnerDir, bool isPhysicalFile, string rawContent)
        {
            string sourcePath = isPhysicalFile
                ? item.FilePath
                : Path.Combine(runnerDir, "script.js");

            if (!isPhysicalFile)
            {
                File.WriteAllText(sourcePath, rawContent);
            }

            return $@"@echo off
chcp 65001 >nul
cls
title FlyShelf — Running JavaScript
echo ================================================================
echo   FlyShelf Developer Engine — Node.js / JavaScript Runner
echo ================================================================
echo.

where node >nul 2>&1
if not errorlevel 1 (
    echo [Engine] Node.js
    echo.
    echo ================================================================
    echo   Program Output:
    echo ================================================================
    echo.
    node ""{sourcePath}""
    goto :after_run
)

where bun >nul 2>&1
if not errorlevel 1 (
    echo [Engine] Bun
    echo.
    echo ================================================================
    echo   Program Output:
    echo ================================================================
    echo.
    bun ""{sourcePath}""
    goto :after_run
)

where deno >nul 2>&1
if not errorlevel 1 (
    echo [Engine] Deno
    echo.
    echo ================================================================
    echo   Program Output:
    echo ================================================================
    echo.
    deno run ""{sourcePath}""
    goto :after_run
)

echo [FlyShelf Error] Node.js, Bun, or Deno not found in system PATH.
echo Install Node.js via: winget install -e --id OpenJS.NodeJS
goto :finish

:after_run
echo.
echo ================================================================
echo   Process exited with return code %errorlevel%.
echo ================================================================

:finish
echo.
pause
";
        }

        private static string GenerateTsRunner(ClipboardItem item, string runnerDir, bool isPhysicalFile, string rawContent)
        {
            string sourcePath = isPhysicalFile
                ? item.FilePath
                : Path.Combine(runnerDir, "script.ts");

            if (!isPhysicalFile)
            {
                File.WriteAllText(sourcePath, rawContent);
            }

            return $@"@echo off
chcp 65001 >nul
cls
title FlyShelf — Running TypeScript
echo ================================================================
echo   FlyShelf Developer Engine — TypeScript Runner
echo ================================================================
echo.

where tsx >nul 2>&1
if not errorlevel 1 (
    echo [Engine] tsx
    echo.
    echo ================================================================
    echo   Program Output:
    echo ================================================================
    echo.
    tsx ""{sourcePath}""
    goto :after_run
)

where bun >nul 2>&1
if not errorlevel 1 (
    echo [Engine] Bun
    echo.
    echo ================================================================
    echo   Program Output:
    echo ================================================================
    echo.
    bun ""{sourcePath}""
    goto :after_run
)

where deno >nul 2>&1
if not errorlevel 1 (
    echo [Engine] Deno
    echo.
    echo ================================================================
    echo   Program Output:
    echo ================================================================
    echo.
    deno run ""{sourcePath}""
    goto :after_run
)

where ts-node >nul 2>&1
if not errorlevel 1 (
    echo [Engine] ts-node
    echo.
    echo ================================================================
    echo   Program Output:
    echo ================================================================
    echo.
    ts-node ""{sourcePath}""
    goto :after_run
)

where npx >nul 2>&1
if not errorlevel 1 (
    echo [Engine] npx tsx
    echo.
    echo ================================================================
    echo   Program Output:
    echo ================================================================
    echo.
    npx tsx ""{sourcePath}""
    goto :after_run
)

echo [FlyShelf Error] TypeScript runner (tsx, bun, deno, or ts-node) not found in system PATH.
echo Install tsx via: npm install -g tsx
goto :finish

:after_run
echo.
echo ================================================================
echo   Process exited with return code %errorlevel%.
echo ================================================================

:finish
echo.
pause
";
        }

        private static string GeneratePowerShellRunner(ClipboardItem item, string runnerDir, bool isPhysicalFile, string rawContent)
        {
            string sourcePath = isPhysicalFile
                ? item.FilePath
                : Path.Combine(runnerDir, "script.ps1");

            if (!isPhysicalFile)
            {
                File.WriteAllText(sourcePath, rawContent);
            }

            return $@"@echo off
chcp 65001 >nul
cls
title FlyShelf — Running PowerShell Script
echo ================================================================
echo   FlyShelf Developer Engine — PowerShell Runner
echo ================================================================
echo.

powershell.exe -NoLogo -ExecutionPolicy Bypass -File ""{sourcePath}""
echo.
echo ================================================================
echo   Process exited with return code %errorlevel%.
echo ================================================================
echo.
pause
";
        }

        private static string GenerateBatchRunner(ClipboardItem item, string runnerDir, bool isPhysicalFile, string rawContent)
        {
            string sourcePath = isPhysicalFile
                ? item.FilePath
                : Path.Combine(runnerDir, "script.bat");

            if (!isPhysicalFile)
            {
                File.WriteAllText(sourcePath, rawContent);
            }

            return $@"@echo off
chcp 65001 >nul
cls
title FlyShelf — Running Script
echo ================================================================
echo   FlyShelf Developer Engine — Command Prompt
echo ================================================================
echo.

call ""{sourcePath}""
echo.
echo ================================================================
echo   Process exited with return code %errorlevel%.
echo ================================================================
echo.
pause
";
        }
    }
}
