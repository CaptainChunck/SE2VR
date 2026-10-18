using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace SE2VR.Launcher;

/// <summary>
/// Starts Space Engineers 2 with the SE2VR plugin; the target of the "Space Engineers 2 VR" desktop shortcut.
/// <para>
/// The game is started directly, with the SteamAppId environment variable telling Steamworks which game it is, so
/// Steam's launch options stay untouched and Steam's own Play button still starts the normal game. Before that it
/// makes sure Steam is running and signed in, and starts SteamVR when it is the active OpenXR runtime.
/// </para>
/// <para>
/// This replaces a PowerShell script: a shortcut to a script flashes a console window, and when the script could not
/// run at all (for example when its folder was missing) the window closed before anyone could read why. A Windows
/// program has no console. Every start is logged to launcher.log next to it; failures are shown in a message box.
/// </para>
/// Arguments are passed on to the game (e.g. -appData:"C:\folder" for a separate profile). --dry-run checks everything
/// and logs what it would start, without starting anything.
/// </summary>
internal static class Program
{
    private const string AppId = "1133870"; // Space Engineers 2
    private const string SteamVRAppId = "250820";
    private static string _logPath = "";

    [STAThread]
    private static int Main(string[] args)
    {
        string directory = AppDomain.CurrentDomain.BaseDirectory;
        _logPath = Path.Combine(directory, "launcher.log");
        try
        {
            RotateLog();
            Log($"---- Space Engineers 2 VR launcher {Assembly.GetExecutingAssembly().GetName().Version}");
            bool dryRun = args.Contains("--dry-run", StringComparer.OrdinalIgnoreCase);
            string[] gameArguments = args.Where(a => !string.Equals(a, "--dry-run", StringComparison.OrdinalIgnoreCase)).ToArray();

            string plugin = Path.Combine(directory, "SE2VR.dll");
            if (!File.Exists(plugin))
            {
                throw new LaunchException($"SE2VR.dll is missing from {directory}. Run Install.cmd again.");
            }
            Process[] running = Process.GetProcessesByName("SpaceEngineers2");
            if (running.Length > 0)
            {
                string message = $"Space Engineers 2 is already running (process {running[0].Id}). Close it first; if it is not visible, end it in Task Manager.";
                if (!dryRun)
                {
                    throw new LaunchException(message);
                }
                Log("Dry run: " + message);
            }
            string exe = FindGame() ?? throw new LaunchException("Space Engineers 2 was not found in your Steam libraries.");
            Log("Game: " + exe);

            EnsureSteam(dryRun);
            EnsureSteamVR(dryRun);

            var start = new ProcessStartInfo(exe)
            {
                WorkingDirectory = Path.GetDirectoryName(exe),
                UseShellExecute = false,
                Arguments = string.Join(" ", new[] { "-loadScripts", $"-plugins:\"{plugin}\"" }.Concat(gameArguments.Select(QuoteArgument)))
            };
            // Steamworks reads the app id from the environment, so no steam_appid.txt is needed in the game folder.
            start.EnvironmentVariables["SteamAppId"] = AppId;
            start.EnvironmentVariables["SteamGameId"] = AppId;
            if (dryRun)
            {
                Log($"Dry run: would start {exe} with: {start.Arguments}");
                return 0;
            }
            using (Process game = Process.Start(start)!)
            {
                Log($"Started the game (process {game.Id}) with: {start.Arguments}");
                // The game restarts itself once while starting, so look for any game process a little later.
                Thread.Sleep(20000);
                running = Process.GetProcessesByName("SpaceEngineers2");
                if (running.Length == 0)
                {
                    string code = game.HasExited ? game.ExitCode.ToString() : "unknown";
                    throw new LaunchException($"Space Engineers 2 closed right after starting (exit code {code}).\n\n" +
                                              $"The game's own logs are in %APPDATA%\\SpaceEngineers2\\AppData\\Temp\\Logs, the mod's log is {Path.Combine(directory, "SE2VR.log")}.");
                }
                Log($"The game is running (process {string.Join(", ", running.Select(p => p.Id))}).");
            }
            return 0;
        }
        catch (Exception e)
        {
            string message = e is LaunchException ? e.Message : "The launcher failed: " + e;
            Log("Error: " + message);
            ShowError(message);
            return 1;
        }
    }

    /// <summary>The game's executable in any of Steam's libraries (libraryfolders.vdf), or null.</summary>
    private static string? FindGame()
    {
        string? steam = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
        if (string.IsNullOrEmpty(steam))
        {
            return null;
        }
        steam = steam!.Replace('/', '\\');
        var libraries = new List<string> { steam };
        string vdf = Path.Combine(steam, @"steamapps\libraryfolders.vdf");
        if (File.Exists(vdf))
        {
            foreach (Match match in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
            {
                libraries.Add(match.Groups[1].Value.Replace(@"\\", @"\"));
            }
        }
        foreach (string library in libraries.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string exe = Path.Combine(library, @"steamapps\common\SpaceEngineers2\Game2\SpaceEngineers2.exe");
            if (File.Exists(exe))
            {
                return exe;
            }
        }
        return null;
    }

    private static bool SteamSignedIn()
    {
        object? user = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam\ActiveProcess", "ActiveUser", 0);
        return user is int id && id != 0;
    }

    private static void EnsureSteam(bool dryRun)
    {
        if (Process.GetProcessesByName("steam").Length == 0)
        {
            string? steamExe = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamExe", null) as string;
            if (string.IsNullOrEmpty(steamExe))
            {
                throw new LaunchException("Steam is not installed for this Windows user.");
            }
            if (dryRun)
            {
                Log("Dry run: would start Steam.");
                return;
            }
            Log("Starting Steam");
            Process.Start(new ProcessStartInfo(steamExe!) { UseShellExecute = true })?.Dispose();
        }
        DateTime deadline = DateTime.Now.AddMinutes(2);
        while (!SteamSignedIn() && DateTime.Now < deadline && !dryRun)
        {
            Thread.Sleep(1000);
        }
        if (!SteamSignedIn())
        {
            throw new LaunchException("Steam is not signed in. Sign in to Steam and try again.");
        }
    }

    /// <summary>The mod waits only briefly for the headset at start-up, so bring SteamVR up first if it is the VR runtime.</summary>
    private static void EnsureSteamVR(bool dryRun)
    {
        string? runtime;
        using (RegistryKey machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
        using (RegistryKey? openXr = machine.OpenSubKey(@"SOFTWARE\Khronos\OpenXR\1"))
        {
            runtime = openXr?.GetValue("ActiveRuntime") as string;
        }
        Log("OpenXR runtime: " + (runtime ?? "none registered"));
        if (runtime == null || runtime.IndexOf("steamvr", StringComparison.OrdinalIgnoreCase) < 0 ||
            Process.GetProcessesByName("vrcompositor").Length > 0)
        {
            return;
        }
        if (dryRun)
        {
            Log("Dry run: would start SteamVR.");
            return;
        }
        Log("Starting SteamVR");
        Process.Start(new ProcessStartInfo($"steam://rungameid/{SteamVRAppId}") { UseShellExecute = true })?.Dispose();
        DateTime deadline = DateTime.Now.AddSeconds(90);
        while (Process.GetProcessesByName("vrcompositor").Length == 0 && DateTime.Now < deadline)
        {
            Thread.Sleep(1000);
        }
        Thread.Sleep(5000); // let it find the headset
    }

    /// <summary>Quotes a game argument that contains spaces; for -name:value only the value is quoted, as the game expects.</summary>
    private static string QuoteArgument(string argument)
    {
        if (argument.IndexOf(' ') < 0 || argument.StartsWith("\"", StringComparison.Ordinal))
        {
            return argument;
        }
        Match option = Regex.Match(argument, "^(-[A-Za-z]+:)(.*)$");
        return option.Success ? $"{option.Groups[1].Value}\"{option.Groups[2].Value}\"" : $"\"{argument}\"";
    }

    private static void ShowError(string message)
    {
        try
        {
            Application.EnableVisualStyles();
            // There is no window of our own; a top-most owner keeps the message in front of everything else.
            using var owner = new Form { TopMost = true, ShowInTaskbar = false, StartPosition = FormStartPosition.CenterScreen };
            MessageBox.Show(owner, message, "Space Engineers 2 VR", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch
        {
            // The log has it.
        }
    }

    private static void RotateLog()
    {
        try
        {
            if (File.Exists(_logPath) && new FileInfo(_logPath).Length > 256 * 1024)
            {
                File.Delete(_logPath);
            }
        }
        catch
        {
        }
    }

    private static void Log(string message)
    {
        try
        {
            File.AppendAllText(_logPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}\r\n");
        }
        catch
        {
        }
    }

    private sealed class LaunchException : Exception
    {
        public LaunchException(string message)
            : base(message)
        {
        }
    }
}
