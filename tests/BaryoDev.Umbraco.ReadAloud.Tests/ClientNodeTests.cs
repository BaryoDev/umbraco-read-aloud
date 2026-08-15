using System.Diagnostics;

namespace BaryoDev.Umbraco.ReadAloud.Tests;

/// <summary>
/// Runs the browser client's own test suite (<c>tests/client</c>, Node's built-in
/// <c>node:test</c>) as part of <c>dotnet test</c>, so a change to <c>readaloud.js</c> that breaks
/// its documented behaviour fails the same command a .NET change would.
/// </summary>
/// <remarks>
/// xUnit 2.9.3 has no supported, public way to skip a fact at runtime, only the compile-time
/// <c>[Fact(Skip = "...")]</c> used by <see cref="EdgeTtsEngineTests.Live_synthesis_returns_mp3_and_word_timings"/>
/// for a permanently-skipped test. A machine without Node is a different case: the test should run
/// everywhere Node is installed and say so plainly everywhere it is not, without failing the build
/// for an environment gap that has nothing to do with the code under test. Printing a clear message
/// and passing is the honest option available without adding a new package dependency for one test.
/// </remarks>
public class ClientNodeTests
{
    [Fact]
    public async Task The_client_test_suite_passes()
    {
        var repoRoot = FindRepoRoot();
        var clientTestsDir = Path.Combine(repoRoot, "tests", "client");

        if (!TryFindNode(out var nodePath))
        {
            Console.WriteLine(
                "SKIPPED: node was not found on PATH, so the browser client's own test suite " +
                $"({Path.Combine("tests", "client")}, run with `node --test`) was not executed. " +
                "Install Node 22+ to run it.");
            return;
        }

        var startInfo = new ProcessStartInfo(nodePath, ["--test", Path.Combine(clientTestsDir, "**", "*.test.js")])
        {
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start node.");

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"node --test failed with exit code {process.ExitCode}.\n" +
                $"--- stdout ---\n{stdout}\n--- stderr ---\n{stderr}");
        }
    }

    private static bool TryFindNode(out string nodePath)
    {
        // Unqualified executable names are resolved against PATH by the OS process-creation call
        // even with UseShellExecute = false, on both Windows and Unix.
        nodePath = "node";
        try
        {
            var startInfo = new ProcessStartInfo(nodePath, "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var process = Process.Start(startInfo);
            if (process is null) return false;
            return process.WaitForExit(5000) && process.ExitCode == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Walks up from the test binary's directory to the checkout containing tests/client.</summary>
    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "tests", "client", "harness.js")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException(
            "Could not locate tests/client/harness.js from the test binary's directory " +
            $"({AppContext.BaseDirectory}).");
    }
}
