using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace ParquetNeo.IntegrationTests;

/// <summary>
/// Runs the Python side of the tests: pyarrow, the reference implementation of Parquet. The tests are skipped unless
/// <c>PARQUETNEO_PYTHON_TESTS=1</c>, so that building and testing the solution needs no Python; the integration workflow sets it, and
/// then a missing Python or pyarrow is a failure. <c>PARQUETNEO_PYTHON</c> names the interpreter (default <c>python3</c>, or <c>python</c> on Windows).
/// </summary>
internal static class PythonEnvironment
{
    public const string EnableVariable = "PARQUETNEO_PYTHON_TESTS";
    public const string InterpreterVariable = "PARQUETNEO_PYTHON";

    private static readonly Lazy<GeneratedFiles> Generated = new(Generate, LazyThreadSafetyMode.ExecutionAndPublication);

    public static bool Enabled => Environment.GetEnvironmentVariable(EnableVariable) == "1";

    public static string Interpreter =>
        Environment.GetEnvironmentVariable(InterpreterVariable) is { Length: > 0 } given ? given : OperatingSystem.IsWindows() ? "python" : "python3";

    private static string ScriptDirectory => Path.Combine(AppContext.BaseDirectory, "python");

    /// <summary>Skips the test unless the integration tests are asked for.</summary>
    public static void SkipUnlessEnabled()
    {
        if (!Enabled)
        {
            Skip.Test($"The integration tests with Python are off: set {EnableVariable}=1, with Python and pyarrow installed (tests/ParquetNeo.IntegrationTests/python/requirements.txt).");
        }
    }

    /// <summary>A path for a file a test writes, in a directory that is deleted when the tests end.</summary>
    public static string NewFile(string name) => Path.Combine(Scratch.Value, name + "-" + Guid.NewGuid().ToString("N")[..8] + ".parquet");

    private static readonly Lazy<string> Scratch = new(() =>
    {
        string directory = Path.Combine(Path.GetTempPath(), "parquetneo-written-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => TryDelete(directory);
        return directory;
    });

    private static void TryDelete(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // Left for the system to clean.
        }
    }

    /// <summary>The files pyarrow wrote, once for the whole run of the tests.</summary>
    public static GeneratedFiles Files => Generated.Value;

    /// <summary>Reads a file with pyarrow, which checks the checksums of its pages, and gives what it found: see <c>dump.py</c>.</summary>
    public static JsonElement Dump(string file)
    {
        var (output, _) = Run("dump.py", file);
        using var document = JsonDocument.Parse(output);
        return document.RootElement.Clone();
    }

    /// <summary>Reads a file with DuckDB, a second implementation, and gives what it found, normalized as for pyarrow: see <c>duckdb_dump.py</c>.</summary>
    /// <param name="excluded">The columns DuckDB does not read as the file holds them (a time in milliseconds is microseconds for it), left out of the query.</param>
    public static JsonElement DuckDbDump(string file, params string[] excluded)
    {
        var (output, _) = Run("duckdb_dump.py", file, string.Join(',', excluded));
        using var document = JsonDocument.Parse(output);
        return document.RootElement.Clone();
    }

    /// <summary>Asks DuckDB what it sees in the bloom filters and the page index of a file: see <c>duckdb_probe.py</c> for the request.</summary>
    public static JsonElement DuckDbProbe(object request)
    {
        string path = NewFile("probe").Replace(".parquet", ".json");
        File.WriteAllText(path, JsonSerializer.Serialize(request));
        var (output, _) = Run("duckdb_probe.py", path);
        using var document = JsonDocument.Parse(output);
        return document.RootElement.Clone();
    }

    private static GeneratedFiles Generate()
    {
        string directory = Path.Combine(Path.GetTempPath(), "parquetneo-python-" + Guid.NewGuid().ToString("N"));
        AppDomain.CurrentDomain.ProcessExit += (_, _) => TryDelete(directory);
        Run("generate.py", directory);
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "manifest.json")));
        return new GeneratedFiles(directory, manifest.RootElement.Clone());
    }

    private static (string Output, string Error) Run(string script, params string[] arguments)
    {
        var start = new ProcessStartInfo(Interpreter)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = ScriptDirectory,
        };
        start.ArgumentList.Add(Path.Combine(ScriptDirectory, script));
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        start.Environment["PYTHONIOENCODING"] = "utf-8";

        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start '{Interpreter}'.");
        var error = process.StandardError.ReadToEndAsync();
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"{Interpreter} {script} {string.Join(' ', arguments)} failed with exit code {process.ExitCode}:{Environment.NewLine}{error.Result}");
        }

        return (output, error.Result);
    }
}

/// <summary>What <c>generate.py</c> wrote: the directory of the files, and the manifest that says what is in them.</summary>
internal sealed class GeneratedFiles(string directory, JsonElement manifest)
{
    public string Directory { get; } = directory;

    public JsonElement Manifest { get; } = manifest;

    public IEnumerable<JsonElement> Cases => Manifest.GetProperty("cases").EnumerateArray();

    public IEnumerable<string> Names(string kind) => Cases.Where(c => c.GetProperty("kind").GetString() == kind).Select(c => c.GetProperty("name").GetString()!);

    public JsonElement Case(string name) => Cases.Single(c => c.GetProperty("name").GetString() == name);

    public string PathOf(string name) => Path.Combine(Directory, name + ".parquet");
}
