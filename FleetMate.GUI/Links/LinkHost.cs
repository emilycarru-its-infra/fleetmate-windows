using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using FleetMate.Core.Links;
using Microsoft.Win32;
using Serilog;

namespace FleetMate.GUI.Links;

/// <summary>
/// One FleetMate per person: the first launch owns a named mutex and listens
/// on a per-user named pipe; a later launch (typically Windows opening a
/// <c>fleetmate:</c> link) hands its link to the first and exits, so a link
/// never opens a second window. Also registers the <c>fleetmate:</c> protocol
/// for the current user.
/// </summary>
public static class LinkHost
{
    private static Mutex? _mutex;
    private static CancellationTokenSource? _listening;

    private static string UserKey =>
        WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;

    private static string MutexName => $@"Local\FleetMate.GUI.{UserKey}";
    private static string PipeName => $"FleetMate.Links.{UserKey}";

    /// <summary>The link among the command-line arguments, if Windows passed one.</summary>
    public static string? LinkFromArgs(IEnumerable<string> args) =>
        args.FirstOrDefault(a => a.TrimStart('"').StartsWith(FleetMateLink.Scheme + ":", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// True when this process is the first FleetMate for this user. Otherwise
    /// the link (or a plain "come to the front") is sent to the running one
    /// and false is returned; the caller then exits.
    /// </summary>
    public static bool TryBecomePrimary(string? link)
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (createdNew) return true;

        _mutex.Dispose();
        _mutex = null;
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            pipe.Connect(3000);
            using var writer = new StreamWriter(pipe) { AutoFlush = true };
            writer.WriteLine(link ?? "");
            Log.Information("Handed {Link} to the running FleetMate", link ?? "(activate)");
        }
        catch (Exception ex)
        {
            // The first instance is starting or hung; the link is lost rather
            // than opening a second window.
            Log.Warning(ex, "Could not reach the running FleetMate");
        }
        return false;
    }

    /// <summary>Listen for links from later launches; each arrives on the caller's dispatcher.</summary>
    public static void Listen(Action<string> onMessage)
    {
        _listening = new CancellationTokenSource();
        var token = _listening.Token;
        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(token);
                    using var reader = new StreamReader(server);
                    var message = await reader.ReadLineAsync(token) ?? "";
                    onMessage(message.Trim());
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Link pipe failed; listening again");
                    await Task.Delay(500, CancellationToken.None);
                }
            }
        }, token);
    }

    public static void Stop()
    {
        _listening?.Cancel();
        try { _mutex?.ReleaseMutex(); } catch (ApplicationException) { }
        _mutex?.Dispose();
    }

    /// <summary>
    /// Register <c>fleetmate:</c> for the current user, pointing at this
    /// executable, when it is missing or points elsewhere. A development run
    /// through <c>dotnet fleetmate-gui.dll</c> uses the apphost beside the dll
    /// when there is one, and otherwise registers nothing.
    /// </summary>
    public static void RegisterProtocol()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (exe == null || Path.GetFileName(exe).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
            {
                var apphost = Path.Combine(AppContext.BaseDirectory, "fleetmate-gui.exe");
                if (!File.Exists(apphost))
                {
                    Log.Information("fleetmate: links not registered: running under the dotnet host with no apphost");
                    return;
                }
                exe = apphost;
            }

            var command = $"\"{exe}\" \"%1\"";
            using var root = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{FleetMateLink.Scheme}");
            using var open = root.CreateSubKey(@"shell\open\command");
            if (string.Equals(open.GetValue(null) as string, command, StringComparison.OrdinalIgnoreCase)) return;

            root.SetValue(null, "URL:FleetMate");
            root.SetValue("URL Protocol", "");
            open.SetValue(null, command);
            Log.Information("Registered fleetmate: links to {Exe}", exe);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not register fleetmate: links");
        }
    }
}
