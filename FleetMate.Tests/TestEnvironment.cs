using System.Runtime.CompilerServices;

namespace FleetMate.Tests;

/// <summary>
/// Makes every git and az process a test starts independent of the machine
/// running it: no system or user git config, hooks, credential manager or
/// askpass program, and no prompt of any kind. A test that would otherwise
/// need an answer fails at once instead of waiting for one.
/// </summary>
internal static class TestEnvironment
{
    [ModuleInitializer]
    internal static void Isolate()
    {
        var root = Path.Combine(Path.GetTempPath(), "fleetmate-tests-env-" + Environment.ProcessId);
        Directory.CreateDirectory(root);
        var globalConfig = Path.Combine(root, "gitconfig");
        File.WriteAllText(globalConfig, string.Empty);

        Set("GIT_CONFIG_NOSYSTEM", "1");
        Set("GIT_CONFIG_GLOBAL", globalConfig);
        Set("GIT_TERMINAL_PROMPT", "0");
        Set("GCM_INTERACTIVE", "never");
        Set("GIT_ASKPASS", null);
        Set("SSH_ASKPASS", null);
        Set("GIT_SSH_COMMAND", "ssh -o BatchMode=yes");
        // A run started from a git hook inherits these, which would point the
        // tests' own repositories at the outer checkout.
        foreach (var name in new[] { "GIT_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE", "GIT_OBJECT_DIRECTORY", "GIT_COMMON_DIR" })
            Set(name, null);
        // Nothing under test may decide it has a person at the console to sign in.
        Set("FLEETMATE_NONINTERACTIVE", "1");
    }

    private static void Set(string name, string? value) => Environment.SetEnvironmentVariable(name, value);
}
