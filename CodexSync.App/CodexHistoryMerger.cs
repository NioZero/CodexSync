using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace CodexSync.App;

public sealed class CodexHistoryMerger
{
    private readonly Action<string> _log;

    public CodexHistoryMerger(Action<string> log) => _log = log;

    public async Task MergeAsync(string firstSource, string secondSource, string output, string sqlitePath, CancellationToken cancellationToken)
    {
        var first = ValidateSource(firstSource, "A");
        var second = ValidateSource(secondSource, "B");
        var destination = ValidateOutput(output, first, second);
        Directory.CreateDirectory(destination);

        _log("Copiando sesiones…");
        var stats = new MergeStats();
        foreach (var folder in new[] { "sessions", "archived_sessions" })
        {
            await MergeFolderAsync(first, destination, folder, "A", stats, cancellationToken);
            await MergeFolderAsync(second, destination, folder, "B", stats, cancellationToken);
        }
        _log($"Sesiones: {stats.Copied} archivos copiados, {stats.Deduplicated} duplicados omitidos, {stats.Renamed} colisiones conservadas con nombre alternativo.");

        await MergeIndexAsync(first, second, destination, cancellationToken);
        await MergeStateDatabaseAsync(first, second, destination, sqlitePath, cancellationToken);
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

    private async Task MergeFolderAsync(string sourceRoot, string destinationRoot, string folder, string sourceName, MergeStats stats, CancellationToken cancellationToken)
    {
        var sourceFolder = Path.Combine(sourceRoot, folder);
        if (!Directory.Exists(sourceFolder)) { _log($"{sourceName}: no existe {folder}/."); return; }

        foreach (var sourceFile in Directory.EnumerateFiles(sourceFolder, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(sourceFolder, sourceFile);
            var destination = Path.Combine(destinationRoot, folder, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

            if (!File.Exists(destination))
            {
                File.Copy(sourceFile, destination);
                stats.Copied++;
                continue;
            }

            if (await FilesEqualAsync(sourceFile, destination, cancellationToken))
            {
                stats.Deduplicated++;
                continue;
            }

            var renamed = GetCollisionName(destination, sourceName);
            File.Copy(sourceFile, renamed);
            stats.Copied++;
            stats.Renamed++;
            _log($"Colisión conservada: {Path.GetFileName(renamed)}");
        }
    }

    private static string GetCollisionName(string destination, string sourceName)
    {
        var directory = Path.GetDirectoryName(destination)!;
        var stem = Path.GetFileNameWithoutExtension(destination);
        var extension = Path.GetExtension(destination);
        var candidate = Path.Combine(directory, $"{stem}-from-{sourceName.ToLowerInvariant()}{extension}");
        for (var suffix = 2; File.Exists(candidate); suffix++)
            candidate = Path.Combine(directory, $"{stem}-from-{sourceName.ToLowerInvariant()}-{suffix}{extension}");
        return candidate;
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
            {
                if (seen.Add(line)) { await writer.WriteLineAsync(line); count++; }
            }
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
        try
        {
            process = Process.Start(startInfo) ?? throw new InvalidOperationException("No se pudo iniciar sqlite3.exe.");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new InvalidOperationException("No se encontró sqlite3.exe. Indique su ubicación o agréguelo al PATH.", ex);
        }

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

    private sealed class MergeStats { public int Copied { get; set; } public int Deduplicated { get; set; } public int Renamed { get; set; } }
    private sealed record ProcessResult(string StandardOutput, string StandardError, int ExitCode);
}
