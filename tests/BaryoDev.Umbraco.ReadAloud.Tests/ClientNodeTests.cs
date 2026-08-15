using System.Diagnostics;
using System.Text.RegularExpressions;

namespace BaryoDev.Umbraco.ReadAloud.Tests;

/// <summary>
/// Runs the browser client's own test suite (<c>tests/client</c>, Node's built-in
/// <c>node:test</c>) as part of <c>dotnet test</c>, so a change to <c>readaloud.js</c> that breaks
/// its documented behaviour fails the same command a .NET change would.
/// </summary>
/// <remarks>
/// xUnit 2.9.3 has no supported, public way to skip a fact at runtime, only the compile-time
/// <c>[Fact(Skip = "...")]</c> used by <see cref="EdgeTtsEngineTests.Live_synthesis_returns_mp3_and_word_timings"/>
/// for a permanently-skipped test. A machine without Node is a different case, and it splits in
/// two: on a developer's own machine, printing a plain message and passing is more useful than
/// failing a build over an environment gap that has nothing to do with the code under test. In CI,
/// that same message is a hole: nobody reads console output from a green run, `.github/workflows`
/// in this repo does not exist yet, and whoever writes the first pipeline will not necessarily
/// remember `actions/setup-node`. So CI (detected via the `CI` or `GITHUB_ACTIONS` environment
/// variables GitHub Actions and most other CI systems set) gets a hard failure instead.
///
/// This test also does not trust `node --test`'s exit code alone. An empty glob (a Windows path
/// separator ending up inside what Node expects to be a glob, a typo, a file that got renamed and
/// left the pattern matching nothing) exits 0 with zero tests run, which is indistinguishable from
/// success by exit code alone. The TAP summary is parsed and required to report zero failures and
/// at least <see cref="MinimumExpectedTests"/> passes, which turns "nothing ran" into a failure too.
/// </remarks>
public class ClientNodeTests
{
    /// <summary>
    /// The suite's size the last time this was updated (25). A floor, not an exact match: raise it
    /// when tests are added, never to make a broken run look complete.
    /// </summary>
    private const int MinimumExpectedTests = 25;

    [Fact]
    public async Task The_client_test_suite_passes()
    {
        var repoRoot = FindRepoRoot();
        var clientTestsDir = Path.Combine(repoRoot, "tests", "client");

        if (!TryFindNode(out var nodePath))
        {
            if (IsRunningInCi())
            {
                throw new InvalidOperationException(
                    "node was not found on PATH. In CI this must fail rather than silently skip: " +
                    "install Node 22+ (e.g. actions/setup-node) so the browser client's own test " +
                    $"suite ({Path.Combine("tests", "client")}) actually runs.");
            }

            Console.WriteLine(
                "SKIPPED: node was not found on PATH, so the browser client's own test suite " +
                $"({Path.Combine("tests", "client")}, run with `node --test`) was not executed. " +
                "Install Node 22+ to run it. This message only appears on a local machine; the " +
                "same gap fails the build in CI.");
            return;
        }

        // Built with explicit forward slashes rather than Path.Combine: Node's test runner treats
        // the pattern as a glob, and Path.Combine yields backslashes on Windows, where a glob
        // reads `\*` as an escaped `*` rather than a wildcard, silently matching nothing.
        var glob = clientTestsDir.Replace(Path.DirectorySeparatorChar, '/') + "/**/*.test.js";

        var startInfo = new ProcessStartInfo(nodePath, ["--test", glob])
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

        var (passed, failed) = ParseTapSummary(stdout);

        if (failed != 0)
        {
            throw new InvalidOperationException(
                $"node --test reported {failed} failing test(s) despite exiting 0.\n--- stdout ---\n{stdout}");
        }

        if (passed < MinimumExpectedTests)
        {
            throw new InvalidOperationException(
                $"node --test reported only {passed} passing test(s), fewer than the expected " +
                $"floor of {MinimumExpectedTests}. The glob likely matched nothing (an empty glob " +
                "exits 0 with zero tests run, which looks like success by exit code alone).\n" +
                $"--- stdout ---\n{stdout}");
        }
    }

    /// <summary>Reads the "# pass N" / "# fail N" lines from node --test's TAP summary.</summary>
    private static (int Passed, int Failed) ParseTapSummary(string tapOutput)
    {
        var passMatch = Regex.Match(tapOutput, @"^# pass (\d+)$", RegexOptions.Multiline);
        var failMatch = Regex.Match(tapOutput, @"^# fail (\d+)$", RegexOptions.Multiline);

        if (!passMatch.Success || !failMatch.Success)
        {
            throw new InvalidOperationException(
                "Could not find TAP '# pass'/'# fail' summary lines in node --test output. " +
                $"The output format may have changed.\n--- stdout ---\n{tapOutput}");
        }

        return (int.Parse(passMatch.Groups[1].Value), int.Parse(failMatch.Groups[1].Value));
    }

    private static bool IsRunningInCi() =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI"))
        || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"));

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
