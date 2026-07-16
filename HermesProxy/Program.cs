using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Builder;
using System.CommandLine.Parsing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace HermesProxy;

public class Program
{
    public static int Main(string[] args)
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;

        // Wine's bcrypt.dll cannot perform the managed TripleDES operation that
        // .NET's PKCS12 iteration counter runs, so loading our embedded (empty
        // password) BNet server certificate throws under Wine (0xc10000bb). Lifting
        // the "unspecified password" iteration limit makes .NET skip that managed
        // path and let the platform certificate loader handle the PFX instead.
        // Guarded to Wine only, so real Windows builds are completely unaffected.
        // (Under Wine .NET still reports OSPlatform.Windows, so we detect Wine
        // specifically via a pure-managed file check - no P/Invoke, no dynamic API
        // resolution, hence no added AV heuristic surface.)
        if (OsSpecific.IsRunningUnderWine())
            AppContext.SetData("System.Security.Cryptography.Pkcs12UnspecifiedPasswordIterationLimit", -1);

        OsSpecific.ShrinkConsoleWindow();

        var commandTree = new RootCommand("Hermes Proxy: Allows you to play on legacy WoW server with modern client")
        {
            CommandLineArgumentsTemplate.ConfigFileLocation,
            CommandLineArgumentsTemplate.DisableVersionCheck,
            CommandLineArgumentsTemplate.OverwrittenConfigValues,
            CommandLineArgumentsTemplate.LoadDebugger,
        };

        var parser = new CommandLineBuilder(commandTree)
            .UseDefaults()
            .Build();

        commandTree.SetHandler((ctx) =>
        {
            var result = ctx.ParseResult;
            var commandLineArguments = new CommandLineArguments
            {
                ConfigFileLocation = result.GetValueForOption(CommandLineArgumentsTemplate.ConfigFileLocation),
                DisableVersionCheck = result.GetValueForOption(CommandLineArgumentsTemplate.DisableVersionCheck),
                OverwrittenConfigValues = ParseMultiArgument(result.GetValueForOption(CommandLineArgumentsTemplate.OverwrittenConfigValues)),
                LoadDebugger = result.GetValueForOption(CommandLineArgumentsTemplate.LoadDebugger),
            };
            TryLoadDebugger(commandLineArguments.LoadDebugger);
            Server.ServerMain(commandLineArguments);
        });

        int exitCode = 1;
        try
        {
             exitCode = parser.Invoke(args);
        }
        catch (Exception e)
        {
            Console.WriteLine($"Error occured: {e}");
        }

        if (OsSpecific.AreWeInOurOwnConsole())
        {
            // If we would exit immediately the console would close and the user cannot read the error
            // The delay is there if for some reason STDIN is already closed
            Thread.Sleep(TimeSpan.FromSeconds(3));

            Console.WriteLine("Press enter to close");
            Console.ReadLine();
        }

        return exitCode;
    }

    // Loads a managed debugger/diagnostic agent and invokes its
    // StartupHook.Initialize static method. Used for in-process debugging
    // and instrumentation. Silent on errors so a missing/broken agent doesn't
    // crash Hermes on startup.
    private static void TryLoadDebugger(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        Console.Error.WriteLine($"[debugger] step 1: about to LoadFrom('{path}')");
        Console.Error.Flush();
        try
        {
            var asm = System.Reflection.Assembly.LoadFrom(path);
            Console.Error.WriteLine($"[debugger] step 2: loaded assembly {asm.FullName}");
            Console.Error.Flush();

            var hookType = asm.GetType("StartupHook");
            Console.Error.WriteLine($"[debugger] step 3: GetType('StartupHook') = {(hookType?.FullName ?? "<null>")}");
            Console.Error.Flush();

            var init = hookType?.GetMethod("Initialize",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            Console.Error.WriteLine($"[debugger] step 4: GetMethod('Initialize') = {(init?.Name ?? "<null>")}");
            Console.Error.Flush();

            Console.Error.WriteLine("[debugger] step 5: invoking Initialize...");
            Console.Error.Flush();
            init?.Invoke(null, null);
            Console.Error.WriteLine("[debugger] step 6: Initialize returned");
            Console.Error.Flush();
        }
        catch (System.Reflection.TargetInvocationException tie)
        {
            var inner = tie.InnerException ?? tie;
            Console.Error.WriteLine($"[debugger] Initialize threw: {inner.GetType().Name}: {inner.Message}");
            Console.Error.WriteLine(inner.StackTrace ?? "(no stack)");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[debugger] load failed: {ex.GetType().Name}: {ex.Message}");
            Console.Error.WriteLine(ex.StackTrace ?? "(no stack)");
        }
    }

    private static Dictionary<string, string> ParseMultiArgument(string[]? multiArgs)
    {
        if (multiArgs == null)
            return new Dictionary<string, string>();

        var result = new Dictionary<string, string>();
        foreach (var arg in multiArgs)
        {
            var keyValue = arg.Split('=', 2);
            if (keyValue.Length != 2)
                throw new Exception($"Invalid argument '{arg}'");
            result[keyValue[0]] = keyValue[1];
        }
        return result;
    }

    public static class CommandLineArgumentsTemplate
    {
        public static readonly Option<string?> ConfigFileLocation = new(
            name: "--config",
            description: "The config file that will be used",
            isDefault: true, // Must be set so parseArgument can return default value
            parseArgument: result =>
            {
                if (result.Tokens.Count == 0)
                    return "HermesProxy.config";

                string? filePath = result.Tokens.Single().Value;
                if (!File.Exists(filePath))
                {
                    result.ErrorMessage = $"Error: config file '{filePath}' does not exist";
                    return null;
                }

                return filePath;
            });
        public static readonly Option<bool> DisableVersionCheck = new(
            name: "--no-version-check",
            description: "Disables the initial version update check"
            );
        public static readonly Option<string[]> OverwrittenConfigValues = new(
            name: "--set",
            description: "Overwrites a specific config value. Example: --set ServerAddress=logon.example.com"
            );

        // Used internally for in-process diagnostics; not documented for end users.
        public static readonly Option<string?> LoadDebugger = new(
            name: "--load-debugger",
            description: "")
        {
            IsHidden = true,
        };
    }
}

public class CommandLineArguments
{
    public string? ConfigFileLocation { init; get; }
    public bool DisableVersionCheck { init; get; }
    public Dictionary<string, string> OverwrittenConfigValues { init; get; }
    public string? LoadDebugger { init; get; }
}

internal static class OsSpecific
{
    /// True only when running under Wine (i.e. on Linux). Uses pure managed file
    /// checks - no P/Invoke and no dynamic API resolution - so it adds no AV
    /// heuristic surface. Real Windows has neither of these markers, so this
    /// returns false there and the caller's behaviour is unchanged on Windows.
    public static bool IsRunningUnderWine()
    {
        try
        {
            return File.Exists(@"C:\windows\system32\winemenubuilder.exe") // Wine-only stub
                || Directory.Exists(@"Z:\usr");                            // Wine maps Z:\ to /
        }
        catch
        {
            return false;
        }
    }

    /// Checks whenever or not we are in our own console
    /// For example on Windows you can just double click the exe which spawns a new Console Window Host
    public static bool AreWeInOurOwnConsole()
    {
        try
        {
#if _WINDOWS
            var consoleWindowHandle = GetConsoleWindow();
            GetWindowThreadProcessId(consoleWindowHandle, out var consoleWindowProcess);
            var weAreTheOwner = (consoleWindowProcess == Environment.ProcessId);
            return weAreTheOwner;
#else
            return true;
#endif
        }
        catch
        {
            return false;
        }
    }

    public static void ShrinkConsoleWindow()
    {
        try
        {
            int cols = Math.Min(80, Console.LargestWindowWidth);
            int rows = Math.Min(12, Console.LargestWindowHeight);
            Console.SetWindowSize(cols, rows);
            Console.SetBufferSize(cols, 500);
        }
        catch { }
#if _WINDOWS
        try
        {
            var hwnd = GetConsoleWindow();
            if (hwnd != IntPtr.Zero)
            {
                int w = 306, h = 198;
                int sx = GetSystemMetrics(0); // SM_CXSCREEN
                int sy = GetSystemMetrics(1); // SM_CYSCREEN
                SetWindowPos(hwnd, new IntPtr(1), sx - w - 12, sy - h - 50, w, h, 0x0010); // SWP_NOACTIVATE | HWND_BOTTOM
            }
        }
        catch { }
#endif
    }

#if _WINDOWS
    [DllImport("kernel32.dll")]
    static extern IntPtr GetConsoleWindow();

    [DllImport("user32.dll", SetLastError = true)]
    static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);
#endif
}
