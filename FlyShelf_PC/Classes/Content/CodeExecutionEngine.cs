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
        private static readonly Regex _rxPython = new(@"\b(def\s+\w+\s*\(|import\s+[a-zA-Z0-9_.]+|from\s+[a-zA-Z0-9_.]+\s+import|if\s+__name__\s*==\s*['""]+__main__['""]+|elif\s+|print\s*\(|lambda\s+)\b", RegexOptions.Compiled);
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

            if (_rxCppInclude.IsMatch(sample) || _rxCppFeatures.IsMatch(sample))
                return CodeLanguage.Cpp;
            if (_rxCInclude.IsMatch(sample) || (_rxCFeatures.IsMatch(sample) && sample.Contains("int main", StringComparison.Ordinal)))
                return CodeLanguage.C;
            if (_rxCSharp.IsMatch(sample))
                return CodeLanguage.CSharp;
            if (_rxJavaFeatures.IsMatch(sample) || (_rxJavaClass.IsMatch(sample) && sample.Contains("class ", StringComparison.Ordinal)))
                return CodeLanguage.Java;
            if (_rxPython.IsMatch(sample))
                return CodeLanguage.Python;
            if (_rxRust.IsMatch(sample))
                return CodeLanguage.Rust;
            if (_rxGo.IsMatch(sample))
                return CodeLanguage.Go;
            if (ext == ".ts" || ext == ".tsx" || sample.Contains(": string", StringComparison.Ordinal) || sample.Contains(": number", StringComparison.Ordinal) || sample.Contains("interface ", StringComparison.Ordinal))
                return CodeLanguage.TypeScript;
            if (_rxJsTs.IsMatch(sample))
                return CodeLanguage.JavaScript;
            if (_rxPowerShell.IsMatch(sample))
                return CodeLanguage.PowerShell;
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

        // ═══════════════════════════════════════════════════════════════
        // RUNNER DISPATCH
        // ═══════════════════════════════════════════════════════════════

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

        // ═══════════════════════════════════════════════════════════════
        // C++ RUNNER
        // ═══════════════════════════════════════════════════════════════

        private static string GenerateCppRunner(ClipboardItem item, string runnerDir, bool isPhysicalFile, string rawContent)
        {
            string sourcePath = isPhysicalFile ? item.FilePath : Path.Combine(runnerDir, "main.cpp");
            if (!isPhysicalFile) File.WriteAllText(sourcePath, rawContent);
            string exePath = Path.Combine(runnerDir, "main.exe");

            string dispSource = sourcePath;

            return $@"@echo off
chcp 65001 >nul 2>&1
cls
title FlyShelf - C++
echo.
echo    FlyShelf Code Runner - C++
echo    ----------------------------------------
echo    {dispSource}
echo.
where g++ >nul 2>&1
if errorlevel 1 goto :try_clangpp
echo    Compiler: g++ [MinGW/GCC]
echo    Compiling...
echo.
g++ -O2 -std=c++17 ""{sourcePath}"" -o ""{exePath}""
if errorlevel 1 goto :compile_fail
goto :run_exe
:try_clangpp
where clang++ >nul 2>&1
if errorlevel 1 goto :try_cl
echo    Compiler: clang++ [LLVM]
echo    Compiling...
echo.
clang++ -O2 -std=c++17 ""{sourcePath}"" -o ""{exePath}""
if errorlevel 1 goto :compile_fail
goto :run_exe
:try_cl
where cl >nul 2>&1
if errorlevel 1 goto :no_compiler
echo    Compiler: cl.exe [MSVC]
echo    Compiling...
echo.
cl.exe /nologo /EHsc /std:c++17 ""{sourcePath}"" /Fe:""{exePath}""
if errorlevel 1 goto :compile_fail
goto :run_exe
:no_compiler
echo.
echo    [Error] No C++ compiler found in PATH (g++, clang++, cl.exe)
echo    Install: winget install -e --id MSYS2.MSYS2
echo.
pause
goto :eof
:compile_fail
echo.
echo    [Error] Compilation failed.
echo.
pause
goto :eof
:run_exe
echo    ----------------------------------------
echo    Output:
echo    ----------------------------------------
echo.
""{exePath}""
echo.
pause
";
        }

        // ═══════════════════════════════════════════════════════════════
        // C RUNNER
        // ═══════════════════════════════════════════════════════════════

        private static string GenerateCRunner(ClipboardItem item, string runnerDir, bool isPhysicalFile, string rawContent)
        {
            string sourcePath = isPhysicalFile ? item.FilePath : Path.Combine(runnerDir, "main.c");
            if (!isPhysicalFile) File.WriteAllText(sourcePath, rawContent);
            string exePath = Path.Combine(runnerDir, "main.exe");
            string dispSource = sourcePath;

            return $@"@echo off
chcp 65001 >nul 2>&1
cls
title FlyShelf - C
echo.
echo    FlyShelf Code Runner - C
echo    ----------------------------------------
echo    {dispSource}
echo.
where gcc >nul 2>&1
if errorlevel 1 goto :try_clang
echo    Compiler: gcc
echo    Compiling...
echo.
gcc -O2 ""{sourcePath}"" -o ""{exePath}""
if errorlevel 1 goto :compile_fail
goto :run_exe
:try_clang
where clang >nul 2>&1
if errorlevel 1 goto :try_cl
echo    Compiler: clang
echo    Compiling...
echo.
clang -O2 ""{sourcePath}"" -o ""{exePath}""
if errorlevel 1 goto :compile_fail
goto :run_exe
:try_cl
where cl >nul 2>&1
if errorlevel 1 goto :no_compiler
echo    Compiler: cl.exe [MSVC]
echo    Compiling...
echo.
cl.exe /nologo ""{sourcePath}"" /Fe:""{exePath}""
if errorlevel 1 goto :compile_fail
goto :run_exe
:no_compiler
echo.
echo    [Error] No C compiler found in PATH (gcc, clang, cl.exe)
echo    Install: winget install -e --id MSYS2.MSYS2
echo.
pause
goto :eof
:compile_fail
echo.
echo    [Error] Compilation failed.
echo.
pause
goto :eof
:run_exe
echo    ----------------------------------------
echo    Output:
echo    ----------------------------------------
echo.
""{exePath}""
echo.
pause
";
        }

        // ═══════════════════════════════════════════════════════════════
        // PYTHON RUNNER
        // ═══════════════════════════════════════════════════════════════

        private static string GeneratePythonRunner(ClipboardItem item, string runnerDir, bool isPhysicalFile, string rawContent)
        {
            string sourcePath = isPhysicalFile ? item.FilePath : Path.Combine(runnerDir, "script.py");
            if (!isPhysicalFile) File.WriteAllText(sourcePath, rawContent);
            string dispSource = sourcePath;

            return $@"@echo off
chcp 65001 >nul 2>&1
cls
title FlyShelf - Python
echo.
echo    FlyShelf Code Runner - Python
echo    ----------------------------------------
echo    {dispSource}
echo.
where python >nul 2>&1
if errorlevel 1 goto :try_py
echo    Interpreter: python
echo    ----------------------------------------
echo    Output:
echo    ----------------------------------------
echo.
python ""{sourcePath}""
echo.
pause
goto :eof
:try_py
where py >nul 2>&1
if errorlevel 1 goto :try_python3
echo    Interpreter: py
echo    ----------------------------------------
echo    Output:
echo    ----------------------------------------
echo.
py ""{sourcePath}""
echo.
pause
goto :eof
:try_python3
where python3 >nul 2>&1
if errorlevel 1 goto :no_python
echo    Interpreter: python3
echo    ----------------------------------------
echo    Output:
echo    ----------------------------------------
echo.
python3 ""{sourcePath}""
echo.
pause
goto :eof
:no_python
echo.
echo    [Error] Python was not found in PATH
echo    Install: winget install -e --id Python.Python.3.12
echo.
pause
";
        }

        // ═══════════════════════════════════════════════════════════════
        // JAVA RUNNER
        // ═══════════════════════════════════════════════════════════════

        private static string GenerateJavaRunner(ClipboardItem item, string runnerDir, bool isPhysicalFile, string rawContent)
        {
            string className = "Main";
            if (!string.IsNullOrEmpty(rawContent))
            {
                var match = _rxJavaClass.Match(rawContent);
                if (match.Success && match.Groups.Count >= 3 && !string.IsNullOrWhiteSpace(match.Groups[2].Value))
                    className = match.Groups[2].Value.Trim();
            }

            string sourcePath = isPhysicalFile ? item.FilePath : Path.Combine(runnerDir, className + ".java");
            if (!isPhysicalFile) File.WriteAllText(sourcePath, rawContent);
            string dispSource = sourcePath;

            return $@"@echo off
chcp 65001 >nul 2>&1
cls
title FlyShelf - Java
echo.
echo    FlyShelf Code Runner - Java
echo    ----------------------------------------
echo    {dispSource}
echo.
where java >nul 2>&1
if errorlevel 1 goto :no_java
echo    Engine: java (single-file mode)
echo    ----------------------------------------
echo    Output:
echo    ----------------------------------------
echo.
java ""{sourcePath}"" 2>nul
if not errorlevel 1 goto :done
echo.
echo    Fallback: javac compile + java run
echo.
where javac >nul 2>&1
if errorlevel 1 goto :done
javac ""{sourcePath}""
if errorlevel 1 goto :compile_fail
java -cp ""{runnerDir}"" {className}
goto :done
:no_java
echo.
echo    [Error] Java was not found in PATH
echo    Install: winget install -e --id Oracle.JDK.21
echo.
pause
goto :eof
:compile_fail
echo.
echo    [Error] Compilation failed.
echo.
pause
goto :eof
:done
echo.
pause
";
        }

        // ═══════════════════════════════════════════════════════════════
        // C# RUNNER
        // ═══════════════════════════════════════════════════════════════

        private static string GenerateCSharpRunner(ClipboardItem item, string runnerDir, bool isPhysicalFile, string rawContent)
        {
            string sourcePath = isPhysicalFile ? item.FilePath : Path.Combine(runnerDir, "Program.cs");
            if (!isPhysicalFile) File.WriteAllText(sourcePath, rawContent);
            string exePath = Path.Combine(runnerDir, "Program.exe");
            string dispSource = sourcePath;

            return $@"@echo off
chcp 65001 >nul 2>&1
cls
title FlyShelf - C#
echo.
echo    FlyShelf Code Runner - C# / .NET
echo    ----------------------------------------
echo    {dispSource}
echo.
set ""CSC_PATH=""
where csc >nul 2>&1
if not errorlevel 1 (
    set ""CSC_PATH=csc""
    goto :found_csc
)
if exist ""%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"" (
    set ""CSC_PATH=%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\csc.exe""
    goto :found_csc
)
where dotnet-script >nul 2>&1
if errorlevel 1 goto :no_csharp
echo    Engine: dotnet-script
echo    ----------------------------------------
echo    Output:
echo    ----------------------------------------
echo.
dotnet-script ""{sourcePath}""
echo.
pause
goto :eof
:found_csc
echo    Compiler: %CSC_PATH%
echo    Compiling...
echo.
""%CSC_PATH%"" /nologo /out:""{exePath}"" ""{sourcePath}""
if errorlevel 1 goto :compile_fail
echo    ----------------------------------------
echo    Output:
echo    ----------------------------------------
echo.
""{exePath}""
echo.
pause
goto :eof
:no_csharp
echo.
echo    [Error] No C# compiler found (csc.exe or dotnet SDK)
echo    Install: winget install -e --id Microsoft.DotNet.SDK.8
echo.
pause
goto :eof
:compile_fail
echo.
echo    [Error] Compilation failed.
echo.
pause
";
        }

        // ═══════════════════════════════════════════════════════════════
        // RUST RUNNER
        // ═══════════════════════════════════════════════════════════════

        private static string GenerateRustRunner(ClipboardItem item, string runnerDir, bool isPhysicalFile, string rawContent)
        {
            string sourcePath = isPhysicalFile ? item.FilePath : Path.Combine(runnerDir, "main.rs");
            if (!isPhysicalFile) File.WriteAllText(sourcePath, rawContent);
            string exePath = Path.Combine(runnerDir, "main.exe");
            string dispSource = sourcePath;

            return $@"@echo off
chcp 65001 >nul 2>&1
cls
title FlyShelf - Rust
echo.
echo    FlyShelf Code Runner - Rust
echo    ----------------------------------------
echo    {dispSource}
echo.
where rustc >nul 2>&1
if errorlevel 1 goto :no_rustc
echo    Compiler: rustc
echo    Compiling...
echo.
rustc ""{sourcePath}"" -o ""{exePath}""
if errorlevel 1 goto :compile_fail
echo    ----------------------------------------
echo    Output:
echo    ----------------------------------------
echo.
""{exePath}""
echo.
pause
goto :eof
:no_rustc
echo.
echo    [Error] rustc was not found in PATH
echo    Install: winget install -e --id Rustlang.Rustup
echo.
pause
goto :eof
:compile_fail
echo.
echo    [Error] Compilation failed.
echo.
pause
";
        }

        // ═══════════════════════════════════════════════════════════════
        // GO RUNNER
        // ═══════════════════════════════════════════════════════════════

        private static string GenerateGoRunner(ClipboardItem item, string runnerDir, bool isPhysicalFile, string rawContent)
        {
            string sourcePath = isPhysicalFile ? item.FilePath : Path.Combine(runnerDir, "main.go");
            if (!isPhysicalFile) File.WriteAllText(sourcePath, rawContent);
            string dispSource = sourcePath;

            return $@"@echo off
chcp 65001 >nul 2>&1
cls
title FlyShelf - Go
echo.
echo    FlyShelf Code Runner - Go
echo    ----------------------------------------
echo    {dispSource}
echo.
where go >nul 2>&1
if errorlevel 1 goto :no_go
echo    Engine: go run
echo    ----------------------------------------
echo    Output:
echo    ----------------------------------------
echo.
go run ""{sourcePath}""
echo.
pause
goto :eof
:no_go
echo.
echo    [Error] Go was not found in PATH
echo    Install: winget install -e --id GoLang.Go
echo.
pause
";
        }

        // ═══════════════════════════════════════════════════════════════
        // JAVASCRIPT RUNNER
        // ═══════════════════════════════════════════════════════════════

        private static string GenerateJsRunner(ClipboardItem item, string runnerDir, bool isPhysicalFile, string rawContent)
        {
            string sourcePath = isPhysicalFile ? item.FilePath : Path.Combine(runnerDir, "script.js");
            if (!isPhysicalFile) File.WriteAllText(sourcePath, rawContent);
            string dispSource = sourcePath;

            return $@"@echo off
chcp 65001 >nul 2>&1
cls
title FlyShelf - JavaScript
echo.
echo    FlyShelf Code Runner - JavaScript
echo    ----------------------------------------
echo    {dispSource}
echo.
where node >nul 2>&1
if errorlevel 1 goto :try_bun
echo    Engine: Node.js
echo    ----------------------------------------
echo    Output:
echo    ----------------------------------------
echo.
node ""{sourcePath}""
echo.
pause
goto :eof
:try_bun
where bun >nul 2>&1
if errorlevel 1 goto :try_deno
echo    Engine: Bun
echo    ----------------------------------------
echo    Output:
echo    ----------------------------------------
echo.
bun ""{sourcePath}""
echo.
pause
goto :eof
:try_deno
where deno >nul 2>&1
if errorlevel 1 goto :no_js
echo    Engine: Deno
echo    ----------------------------------------
echo    Output:
echo    ----------------------------------------
echo.
deno run ""{sourcePath}""
echo.
pause
goto :eof
:no_js
echo.
echo    [Error] No JS engine found (node, bun, deno)
echo    Install: winget install -e --id OpenJS.NodeJS
echo.
pause
";
        }

        // ═══════════════════════════════════════════════════════════════
        // TYPESCRIPT RUNNER
        // ═══════════════════════════════════════════════════════════════

        private static string GenerateTsRunner(ClipboardItem item, string runnerDir, bool isPhysicalFile, string rawContent)
        {
            string sourcePath = isPhysicalFile ? item.FilePath : Path.Combine(runnerDir, "script.ts");
            if (!isPhysicalFile) File.WriteAllText(sourcePath, rawContent);
            string dispSource = sourcePath;

            return $@"@echo off
chcp 65001 >nul 2>&1
cls
title FlyShelf - TypeScript
echo.
echo    FlyShelf Code Runner - TypeScript
echo    ----------------------------------------
echo    {dispSource}
echo.
where tsx >nul 2>&1
if errorlevel 1 goto :try_bun
echo    Engine: tsx
echo    ----------------------------------------
echo    Output:
echo    ----------------------------------------
echo.
tsx ""{sourcePath}""
echo.
pause
goto :eof
:try_bun
where bun >nul 2>&1
if errorlevel 1 goto :try_deno
echo    Engine: Bun
echo    ----------------------------------------
echo    Output:
echo    ----------------------------------------
echo.
bun ""{sourcePath}""
echo.
pause
goto :eof
:try_deno
where deno >nul 2>&1
if errorlevel 1 goto :try_tsnode
echo    Engine: Deno
echo    ----------------------------------------
echo    Output:
echo    ----------------------------------------
echo.
deno run ""{sourcePath}""
echo.
pause
goto :eof
:try_tsnode
where ts-node >nul 2>&1
if errorlevel 1 goto :try_npx
echo    Engine: ts-node
echo    ----------------------------------------
echo    Output:
echo    ----------------------------------------
echo.
ts-node ""{sourcePath}""
echo.
pause
goto :eof
:try_npx
where npx >nul 2>&1
if errorlevel 1 goto :no_ts
echo    Engine: npx tsx
echo    ----------------------------------------
echo    Output:
echo    ----------------------------------------
echo.
npx tsx ""{sourcePath}""
echo.
pause
goto :eof
:no_ts
echo.
echo    [Error] No TS runner found (tsx, bun, deno, ts-node)
echo    Install: npm install -g tsx
echo.
pause
";
        }

        // ═══════════════════════════════════════════════════════════════
        // POWERSHELL RUNNER
        // ═══════════════════════════════════════════════════════════════

        private static string GeneratePowerShellRunner(ClipboardItem item, string runnerDir, bool isPhysicalFile, string rawContent)
        {
            string sourcePath = isPhysicalFile ? item.FilePath : Path.Combine(runnerDir, "script.ps1");
            if (!isPhysicalFile) File.WriteAllText(sourcePath, rawContent);
            string dispSource = sourcePath;

            return $@"@echo off
chcp 65001 >nul 2>&1
cls
title FlyShelf - PowerShell
echo.
echo    FlyShelf Code Runner - PowerShell
echo    ----------------------------------------
echo    {dispSource}
echo.
echo    Engine: powershell.exe
echo    ----------------------------------------
echo    Output:
echo    ----------------------------------------
echo.
powershell.exe -NoLogo -ExecutionPolicy Bypass -File ""{sourcePath}""
echo.
pause
";
        }

        // ═══════════════════════════════════════════════════════════════
        // BATCH / SHELL COMMAND RUNNER
        // ═══════════════════════════════════════════════════════════════

        private static string GenerateBatchRunner(ClipboardItem item, string runnerDir, bool isPhysicalFile, string rawContent)
        {
            string sourcePath = isPhysicalFile ? item.FilePath : Path.Combine(runnerDir, "script.bat");
            if (!isPhysicalFile) File.WriteAllText(sourcePath, rawContent);
            string dispSource = sourcePath;

            return $@"@echo off
chcp 65001 >nul 2>&1
cls
title FlyShelf - Command Prompt
echo.
echo    FlyShelf Code Runner - Command Prompt
echo    ----------------------------------------
echo    {dispSource}
echo.
echo    ----------------------------------------
echo    Output:
echo    ----------------------------------------
echo.
call ""{sourcePath}""
echo.
pause
";
        }
    }
}
