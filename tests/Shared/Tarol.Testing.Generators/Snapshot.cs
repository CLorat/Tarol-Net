using System.Reflection;
using System.Text;
using Microsoft.CodeAnalysis;

namespace Tarol.Testing.Generators;

/// <summary>
///     Approval-style snapshot testing for generator output (the Verify workflow without its licence claim, see
///     docs/migration/plan.md D-9). Each generated file and the diagnostics are compared with committed
///     <c>Snapshots/&lt;name&gt;/&lt;file&gt;.verified.&lt;ext&gt;</c> files in the test project. On a mismatch, or when
///     no snapshot exists yet, the actual text is written as <c>*.received.*</c> and the match fails. Accept a
///     change by renaming the received file, or run the tests with <c>TAROL_ACCEPT_SNAPSHOTS=1</c>.
///     <para>
///         The snapshot directory comes from the test assembly's <c>[AssemblyMetadata("TarolSnapshotRoot", …)]</c>,
///         which <c>tests/Directory.Build.props</c> adds to every test project.
///     </para>
/// </summary>
public static class Snapshot
{
    private static bool Accept => Environment.GetEnvironmentVariable("TAROL_ACCEPT_SNAPSHOTS") == "1";

    /// <summary>Compares every source the driver generated, plus its diagnostics, with the snapshot <paramref name="name" />.</summary>
    /// <exception cref="SnapshotMismatchException">At least one file differs or is missing.</exception>
    public static void Match(string name, GeneratorDriver driver)
    {
        ArgumentNullException.ThrowIfNull(driver);
        var result = driver.GetRunResult();
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);

        foreach (var generated in result.Results.SelectMany(r => r.GeneratedSources))
            files[generated.HintName] = generated.SourceText.ToString();

        files["diagnostics.txt"] = FormatDiagnostics(result.Diagnostics);

        var directory = Path.Combine(Root(), name);
        Directory.CreateDirectory(directory);
        var failures = new List<string>();

        foreach (var (file, actualText) in files)
        {
            var actual = actualText.Replace("\r\n", "\n", StringComparison.Ordinal);
            var stem = Path.Combine(directory, Path.ChangeExtension(file, null));
            var extension = Path.GetExtension(file);
            var verified = $"{stem}.verified{extension}";
            var received = $"{stem}.received{extension}";

            if (File.Exists(verified) && File.ReadAllText(verified).Replace("\r\n", "\n", StringComparison.Ordinal) == actual)
            {
                File.Delete(received);
                continue;
            }

            if (Accept)
            {
                File.WriteAllText(verified, actual);
                File.Delete(received);
                continue;
            }

            File.WriteAllText(received, actual);
            failures.Add(file);
        }

        // Snapshots of files the generator no longer produces.
        foreach (var stale in Directory.GetFiles(directory, "*.verified.*"))
        {
            var original = Path.GetFileName(stale).Replace(".verified", "", StringComparison.Ordinal);
            if (files.ContainsKey(original))
                continue;
            if (Accept)
                File.Delete(stale);
            else
                failures.Add($"{original} (no longer generated)");
        }

        if (failures.Count > 0)
            throw new SnapshotMismatchException(
                $"Snapshot '{name}' differs for: {string.Join(", ", failures)}. See *.received.* in {directory}.");
    }

    private static string FormatDiagnostics(IEnumerable<Diagnostic> diagnostics)
    {
        var text = new StringBuilder();
        foreach (var d in diagnostics.OrderBy(d => d.Id, StringComparer.Ordinal)
                     .ThenBy(d => d.GetMessage(System.Globalization.CultureInfo.InvariantCulture), StringComparer.Ordinal))
        {
            var span = d.Location.GetLineSpan();
            var where = d.Location.IsInSource
                ? $"{Path.GetFileName(span.Path)}({span.StartLinePosition.Line + 1},{span.StartLinePosition.Character + 1})"
                : "-";
            text.Append(System.Globalization.CultureInfo.InvariantCulture,
                $"{d.Id} {d.Severity} {where}: {d.GetMessage(System.Globalization.CultureInfo.InvariantCulture)}\n");
        }

        return text.ToString();
    }

    private static string Root()
    {
        var assembly = Assembly.GetEntryAssembly()
                       ?? throw new InvalidOperationException("Snapshot tests need an entry assembly (the test executable).");
        return assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                   .SingleOrDefault(a => a.Key == "TarolSnapshotRoot")?.Value
               ?? throw new InvalidOperationException(
                   $"{assembly.GetName().Name} has no [AssemblyMetadata(\"TarolSnapshotRoot\", …)] (set by tests/Directory.Build.props).");
    }
}

/// <summary>A generated file differs from its committed snapshot.</summary>
public sealed class SnapshotMismatchException : Exception
{
    /// <summary>Creates the exception.</summary>
    public SnapshotMismatchException() { }

    /// <summary>Creates the exception with a message.</summary>
    public SnapshotMismatchException(string message) : base(message) { }

    /// <summary>Creates the exception with a message and an inner exception.</summary>
    public SnapshotMismatchException(string message, Exception innerException) : base(message, innerException) { }
}
