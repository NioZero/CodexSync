using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace CodexSync.App;

public sealed class CodexHistoryMerger
{
    private readonly Action<string> _log;

    public CodexHistoryMerger(Action<string> log) => _log = log;

    public async Task<HistoryMergePlan> AnalyzeAsync(string firstSource, string secondSource, CancellationToken cancellationToken)
    {
        var first = ValidateSource(firstSource, "A");
        var second = ValidateSource(secondSource, "B");
        var firstFiles = GetSessionFiles(first);
        var secondFiles = GetSessionFiles(second);
        var entries = new List<ConversationEntry>();

        foreach (var key in firstFiles.Keys.Union(secondFiles.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(key => key, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            firstFiles.TryGetValue(key, out var firstFile);
            secondFiles.TryGetValue(key, out var secondFile);
            var (folder, relativePath) = SplitKey(key);
            if (firstFile is null)
            {
                entries.Add(new ConversationEntry(folder, relativePath, ConversationStatus.NewInB, null, secondFile!, ConflictChoice.B));
                continue;
            }
            if (secondFile is null)
            {
                entries.Add(new ConversationEntry(folder, relativePath, ConversationStatus.NewInA, firstFile, null, ConflictChoice.A));
                continue;
            }

            if (await FilesEqualAsync(firstFile.FullName, secondFile.FullName, cancellationToken))
            {
                entries.Add(new ConversationEntry(folder, relativePath, ConversationStatus.Identical, firstFile, secondFile, ConflictChoice.A));
                continue;
            }

            var defaultChoice = secondFile.LastWriteTimeUtc > firstFile.LastWriteTimeUtc ? ConflictChoice.B : ConflictChoice.A;
            entries.Add(new ConversationEntry(folder, relativePath, ConversationStatus.Conflict, firstFile, secondFile, defaultChoice));
        }

        return new HistoryMergePlan(first, second, entries);
    }

    public async Task MergeAsync(string firstSource, string secondSource, string output, string sqlitePath, HistoryMergePlan plan, CancellationToken cancellationToken)
    {
        var first = ValidateSource(firstSource, "A");
        var second = ValidateSource(secondSource, "B");
        if (!plan.BelongsTo(first, second))
            throw new InvalidOperationException("El análisis no corresponde a las carpetas seleccionadas. Ejecute el análisis de nuevo.");

        var destination = ValidateOutput(output, first, second);
        Directory.CreateDirectory(destination);
        _log("Copiando las sesiones según las decisiones del análisis…");
        var stats = new MergeStats();
        foreach (var folder in new[] { "sessions", "archived_sessions" })
            MergeFolder(destination, folder, plan, stats, cancellationToken);
        _log($"Sesiones: {stats.Copied} archivos escritos; {stats.Identical} idénticos consolidados; {stats.ResolvedConflicts} conflictos resueltos por selección.");

        await MergeIndexAsync(first, second, destination, cancellationToken);
        await MergeStateDatabaseAsync(first, second, destination, sqlitePath, cancellationToken);
    }

    private static Dictionary<string, FileInfo> GetSessionFiles(string root)
    {
        var result = new Dictionary<string, FileInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in new[] { "sessions", "archived_sessions" })
        {
            var sourceFolder = Path.Combine(root, folder);
            if (!Directory.Exists(sourceFolder)) continue;
            foreach (var file in Directory.EnumerateFiles(sourceFolder, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(sourceFolder, file).Replace(Path.DirectorySeparatorChar, '/');
                result.Add($"{folder}/{relative}", new FileInfo(file));
            }
        }
        return result;
    }

    private static (string Folder, string RelativePath) SplitKey(string key)
    {
        var separator = key.IndexOf('/');
        return (key[..separator], key[(separator + 1)..]);
    }

    private static string ValidateSource(string path, string label)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new InvalidOperationException($"Seleccione la carpeta .codex {label}.");
        var resolved = Path.GetFullPath(path.Trim());
        if (!Directory.Exists(resolved)) throw new InvalidOperationException($"La carpeta .codex {label} no existe: {resolved}");
        return resolved;
    }

    private static string ValidateOutput(string path, string first, string second)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new InvalidOperationException("Seleccione una carpeta de salida.");
        var resolved = Path.GetFullPath(path.Trim());
        if (string.Equals(resolved, first, StringComparison.OrdinalIgnoreCase) || string.Equals(resolved, second, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("La salida debe ser distinta de las dos carpetas de origen.");
        if (Directory.Exists(resolved) && Directory.EnumerateFileSystemEntries(resolved).Any())
            throw new InvalidOperationException("Por seguridad, la carpeta de salida debe estar vacía.");
        return resolved;
    }

    private static void MergeFolder(string destinationRoot, string folder, HistoryMergePlan plan, MergeStats stats, CancellationToken cancellationToken)
    {
        foreach (var entry in plan.Entries.Where(entry => entry.Folder == folder))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = entry.SelectedFile;
            if (!source.Exists)
                throw new InvalidOperationException($"La sesión analizada ya no está disponible: {source.FullName}. Ejecute el análisis otra vez.");

            var destination = Path.Combine(destinationRoot, folder, entry.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source.FullName, destination);
            stats.Copied++;
            if (entry.Status == ConversationStatus.Identical) stats.Identical++;
            if (entry.Status == ConversationStatus.Conflict) stats.ResolvedConflicts++;
        }
    }

    private static async Task<bool> FilesEqualAsync(string left, string right, CancellationToken cancellationToken)
    {
        if (new FileInfo(left).Length != new FileInfo(right).Length) return false;
        await using var leftStream = File.OpenRead(left);
        await using var rightStream = File.OpenRead(right);
        var leftHash = await SHA256.HashDataAsync(leftStream, cancellationToken);
        var rightHash = await SHA256.HashDataAsync(rightStream, cancellationToken);
        return CryptographicOperations.FixedTimeEquals(leftHash, rightHash);
    }

    private async Task MergeIndexAsync(string first, string second, string destination, CancellationToken cancellationToken)
    {
        const string indexName = "session_index.jsonl";
        var sources = new[] { Path.Combine(first, indexName), Path.Combine(second, indexName) }.Where(File.Exists).ToArray();
        if (sources.Length == 0) { _log("No se encontró session_index.jsonl."); return; }

        var outputFile = Path.Combine(destination, indexName);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var count = 0;
        await using var writer = new StreamWriter(outputFile, false, new UTF8Encoding(false));
        foreach (var source in sources)
        {
            using var reader = new StreamReader(source, detectEncodingFromByteOrderMarks: true);
            while (await reader.ReadLineAsync(cancellationToken) is { } line)
                if (seen.Add(line)) { await writer.WriteLineAsync(line); count++; }
        }
        _log($"session_index.jsonl: {count} líneas únicas escritas.");
    }

    private async Task MergeStateDatabaseAsync(string first, string second, string destination, string sqlitePath, CancellationToken cancellationToken)
    {
        const string databaseName = "state_5.sqlite";
        var firstDatabase = Path.Combine(first, databaseName);
        var secondDatabase = Path.Combine(second, databaseName);
        if (!File.Exists(firstDatabase) && !File.Exists(secondDatabase)) { _log("No se encontró state_5.sqlite."); return; }

        var executable = string.IsNullOrWhiteSpace(sqlitePath) ? "sqlite3.exe" : Path.GetFullPath(sqlitePath.Trim());
        if (!string.IsNullOrWhiteSpace(sqlitePath) && !File.Exists(executable))
            throw new InvalidOperationException($"No existe sqlite3.exe en: {executable}");

        _log("Comprobando SQLite…");
        await RunSqliteAsync(executable, new[] { "--version" }, cancellationToken);
        var outputDatabase = Path.Combine(destination, databaseName);
        var baseDatabase = File.Exists(firstDatabase) ? firstDatabase : secondDatabase;
        await RunSqliteAsync(executable, new[] { baseDatabase, $".backup '{SqlLiteral(outputDatabase)}'" }, cancellationToken);
        _log($"state_5.sqlite: copia consistente creada desde {(File.Exists(firstDatabase) ? "A" : "B")}.");

        if (!File.Exists(firstDatabase) || !File.Exists(secondDatabase)) return;

        var incomingTables = await QueryLinesAsync(executable, outputDatabase,
            $"ATTACH DATABASE '{SqlLiteral(secondDatabase)}' AS incoming; SELECT name FROM incoming.sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name; DETACH DATABASE incoming;", cancellationToken);
        var mergedTables = 0;
        foreach (var table in incomingTables.Where(IsSafeSqliteName))
        {
            var mainSchema = await QueryScalarAsync(executable, outputDatabase, $"SELECT sql FROM main.sqlite_master WHERE type = 'table' AND name = '{SqlLiteral(table)}';", cancellationToken);
            var incomingSchema = await QueryScalarAsync(executable, outputDatabase, $"ATTACH DATABASE '{SqlLiteral(secondDatabase)}' AS incoming; SELECT sql FROM incoming.sqlite_master WHERE type = 'table' AND name = '{SqlLiteral(table)}'; DETACH DATABASE incoming;", cancellationToken);
            if (string.IsNullOrWhiteSpace(incomingSchema)) { _log($"SQLite: se omitió '{table}' porque no se pudo leer su esquema."); continue; }

            var identifier = QuoteIdentifier(table);
            if (string.IsNullOrWhiteSpace(mainSchema))
            {
                var createAndCopy = $"ATTACH DATABASE '{SqlLiteral(secondDatabase)}' AS incoming; BEGIN IMMEDIATE; {incomingSchema}; INSERT OR IGNORE INTO main.{identifier} SELECT * FROM incoming.{identifier}; COMMIT; DETACH DATABASE incoming;";
                await RunSqliteAsync(executable, new[] { outputDatabase, createAndCopy }, cancellationToken);
                mergedTables++;
                _log($"SQLite: tabla nueva '{table}' incorporada desde B.");
                continue;
            }
            if (!string.Equals(mainSchema.Trim(), incomingSchema.Trim(), StringComparison.Ordinal)) { _log($"SQLite: se omitió '{table}' por esquema diferente."); continue; }

            var script = $"ATTACH DATABASE '{SqlLiteral(secondDatabase)}' AS incoming; BEGIN IMMEDIATE; INSERT OR IGNORE INTO main.{identifier} SELECT * FROM incoming.{identifier}; COMMIT; DETACH DATABASE incoming;";
            await RunSqliteAsync(executable, new[] { outputDatabase, script }, cancellationToken);
            mergedTables++;
        }
        _log($"state_5.sqlite: {mergedTables} tablas compatibles fusionadas (las claves en conflicto conservan la versión de A).");
    }

    private static bool IsSafeSqliteName(string name) => name.Length > 0 && name.All(c => char.IsLetterOrDigit(c) || c is '_' or '$');
    private static string QuoteIdentifier(string value) => $"\"{value.Replace("\"", "\"\"")}\"";
    private static string SqlLiteral(string value) => value.Replace("'", "''");
    private static async Task<string?> QueryScalarAsync(string executable, string database, string sql, CancellationToken cancellationToken)
        => (await QueryLinesAsync(executable, database, sql, cancellationToken)).FirstOrDefault();
    private static async Task<IReadOnlyList<string>> QueryLinesAsync(string executable, string database, string sql, CancellationToken cancellationToken)
    {
        var result = await RunSqliteAsync(executable, new[] { "-noheader", database, sql }, cancellationToken);
        return result.StandardOutput.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static async Task<ProcessResult> RunSqliteAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo { FileName = executable, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        Process process;
        try { process = Process.Start(startInfo) ?? throw new InvalidOperationException("No se pudo iniciar sqlite3.exe."); }
        catch (System.ComponentModel.Win32Exception ex) { throw new InvalidOperationException("No se encontró sqlite3.exe. Indique su ubicación o agréguelo al PATH.", ex); }
        using (process)
        {
            var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var result = new ProcessResult(await standardOutput, await standardError, process.ExitCode);
            if (result.ExitCode != 0) throw new InvalidOperationException($"SQLite devolvió el código {result.ExitCode}: {result.StandardError.Trim()}");
            return result;
        }
    }

    private sealed class MergeStats { public int Copied { get; set; } public int Identical { get; set; } public int ResolvedConflicts { get; set; } }
    private sealed record ProcessResult(string StandardOutput, string StandardError, int ExitCode);
}

public enum ConversationStatus { NewInA, NewInB, Identical, Conflict }
public enum ConflictChoice { A, B }

public sealed class ConversationEntry
{
    public ConversationEntry(string folder, string relativePath, ConversationStatus status, FileInfo? firstFile, FileInfo? secondFile, ConflictChoice selection)
    {
        Folder = folder;
        RelativePath = relativePath;
        Status = status;
        FirstFile = firstFile;
        SecondFile = secondFile;
        Selection = selection;
    }

    public string Folder { get; }
    public string RelativePath { get; }
    public ConversationStatus Status { get; }
    public FileInfo? FirstFile { get; }
    public FileInfo? SecondFile { get; }
    public ConflictChoice Selection { get; set; }
    public FileInfo SelectedFile => Selection == ConflictChoice.A ? FirstFile ?? SecondFile! : SecondFile ?? FirstFile!;
    public DateTime? FirstModifiedUtc => FirstFile?.LastWriteTimeUtc;
    public DateTime? SecondModifiedUtc => SecondFile?.LastWriteTimeUtc;
}

public sealed class HistoryMergePlan
{
    public HistoryMergePlan(string firstSource, string secondSource, IReadOnlyList<ConversationEntry> entries)
    {
        FirstSource = firstSource;
        SecondSource = secondSource;
        Entries = entries;
    }

    public string FirstSource { get; }
    public string SecondSource { get; }
    public IReadOnlyList<ConversationEntry> Entries { get; }
    public bool BelongsTo(string first, string second) => string.Equals(FirstSource, first, StringComparison.OrdinalIgnoreCase) && string.Equals(SecondSource, second, StringComparison.OrdinalIgnoreCase);
}
