using System.Globalization;
using Microsoft.Data.Sqlite;
using ResourceMonitor.Alerting;
using ResourceMonitor.Sampling;

namespace ResourceMonitor.Storage;

// Base persistente entre execuções. Só recebe dados quando um alerta dispara:
// o próprio evento, o snapshot de processos, e a janela de amostras em torno do pico
// (copiada do CacheDatabase pelo EventCaptureCoordinator).
public sealed class PermanentDatabase : IDisposable
{
    private readonly SqliteConnection _connection;

    public PermanentDatabase(string databaseFilePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databaseFilePath)!);
        _connection = new SqliteConnection($"Data Source={databaseFilePath}");
        _connection.Open();
        ApplySchema(_connection);
        SeedOrUpdateBuiltInTemplates(_connection);
    }

    // Chamado pelo lado de leitura (AlertEventQueries) antes de abrir sua própria conexão
    // somente-leitura: garante que um banco criado por uma versão antiga do app (antes de
    // colunas como LastActiveUtc/Interrupted existirem) já esteja migrado, sem depender de
    // que o usuário tenha clicado em "Iniciar" nessa execução (o que criaria um PermanentDatabase
    // e migraria de qualquer forma).
    public static void EnsureSchema(string databaseFilePath)
    {
        if (!File.Exists(databaseFilePath))
        {
            return;
        }

        using var connection = new SqliteConnection($"Data Source={databaseFilePath}");
        connection.Open();
        ApplySchema(connection);
        SeedOrUpdateBuiltInTemplates(connection);
    }

    private static void ApplySchema(SqliteConnection connection)
    {
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
            CREATE TABLE IF NOT EXISTS AlertEvents (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                TimestampUtc TEXT NOT NULL,
                EventType TEXT NOT NULL,
                Metric TEXT NOT NULL,
                DriveName TEXT NULL,
                RawValue REAL NOT NULL,
                AdjustedValue REAL NULL,
                Threshold REAL NOT NULL,
                LastActiveUtc TEXT NULL,
                Interrupted INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS idx_alertevents_timestamp ON AlertEvents(TimestampUtc);

            CREATE TABLE IF NOT EXISTS AlertProcessSnapshots (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                AlertEventId INTEGER NOT NULL REFERENCES AlertEvents(Id),
                Kind TEXT NOT NULL,
                ProcessName TEXT NOT NULL,
                ProcessId INTEGER NOT NULL,
                CpuPercent REAL NOT NULL,
                RamMb REAL NOT NULL,
                IoKbPerSec REAL NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS idx_snapshots_alert ON AlertProcessSnapshots(AlertEventId);

            CREATE TABLE IF NOT EXISTS Samples (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                AlertEventId INTEGER NOT NULL REFERENCES AlertEvents(Id),
                TimestampUtc TEXT NOT NULL,
                CpuRawPercent REAL NOT NULL,
                CpuAdjustedPercent REAL NOT NULL,
                RamRawPercent REAL NOT NULL,
                RamAdjustedPercent REAL NOT NULL,
                RamTotalGb REAL NOT NULL,
                RamAvailableGb REAL NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_samples_alert ON Samples(AlertEventId);
            CREATE INDEX IF NOT EXISTS idx_samples_timestamp ON Samples(TimestampUtc);

            CREATE TABLE IF NOT EXISTS DiskSamples (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                SampleId INTEGER NOT NULL REFERENCES Samples(Id),
                DriveName TEXT NOT NULL,
                FreePercent REAL NOT NULL,
                FreeGb REAL NOT NULL,
                TotalGb REAL NOT NULL,
                IoPercent REAL NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_disksamples_sample ON DiskSamples(SampleId);

            -- Um registro por dia (data local), independente de alerta — soma+contador em vez
            -- de já gravar a média, pra dar pra continuar acumulando corretamente num Parar+Iniciar
            -- no mesmo dia (ver UpsertDailyAggregate). Alimenta tendência de uso pra decisão de
            -- upgrade de hardware, não o fluxo de alerta.
            CREATE TABLE IF NOT EXISTS DailyAggregates (
                Date TEXT PRIMARY KEY,
                SampleCount INTEGER NOT NULL,
                CpuRawSum REAL NOT NULL,
                RamRawSum REAL NOT NULL,
                IoPercentSum REAL NOT NULL,
                DiskFreePercentSum REAL NOT NULL,
                SystemDrive TEXT NOT NULL,
                LastUpdatedUtc TEXT NOT NULL
            );

            -- Templates SQL (aba Templates, ex-Dados) — consultas somente-leitura salvas.
            -- IsBuiltIn marca os 3 templates padrão (Tendência diária/Ofensores/Base de
            -- picos), não editáveis/deletáveis pela UI; o resto é criado pelo usuário.
            CREATE TABLE IF NOT EXISTS Templates (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Name TEXT NOT NULL,
                Command TEXT NOT NULL,
                IsBuiltIn INTEGER NOT NULL DEFAULT 0
            );
            """;
            command.ExecuteNonQuery();
        }

        // CREATE TABLE IF NOT EXISTS não altera uma tabela que já existia antes dessas colunas
        // serem adicionadas — então garante que bancos antigos ganhem as colunas novas também.
        EnsureColumnExists(connection, "AlertEvents", "LastActiveUtc", "TEXT NULL");
        EnsureColumnExists(connection, "AlertEvents", "Interrupted", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumnExists(connection, "AlertProcessSnapshots", "IoKbPerSec", "REAL NOT NULL DEFAULT 0");
    }

    private static void EnsureColumnExists(SqliteConnection connection, string table, string column, string columnDefinition)
    {
        var exists = false;
        using (var checkCommand = connection.CreateCommand())
        {
            checkCommand.CommandText = $"PRAGMA table_info({table});";
            using var reader = checkCommand.ExecuteReader();
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                {
                    exists = true;
                    break;
                }
            }
        }

        if (!exists)
        {
            using var alterCommand = connection.CreateCommand();
            alterCommand.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {columnDefinition};";
            alterCommand.ExecuteNonQuery();
        }
    }

    public long InsertAlertEvent(AlertEvent alertEvent)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT INTO AlertEvents (TimestampUtc, EventType, Metric, DriveName, RawValue, AdjustedValue, Threshold)
            VALUES ($timestamp, $eventType, $metric, $driveName, $rawValue, $adjustedValue, $threshold);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$timestamp", FormatTimestamp(alertEvent.Timestamp));
        command.Parameters.AddWithValue("$eventType", alertEvent.EventType.ToString());
        command.Parameters.AddWithValue("$metric", alertEvent.Metric);
        command.Parameters.AddWithValue("$driveName", (object?)alertEvent.DriveName ?? DBNull.Value);
        command.Parameters.AddWithValue("$rawValue", alertEvent.RawValue);
        command.Parameters.AddWithValue("$adjustedValue", (object?)alertEvent.AdjustedValue ?? DBNull.Value);
        command.Parameters.AddWithValue("$threshold", alertEvent.Threshold);

        return (long)command.ExecuteScalar()!;
    }

    public void InsertProcessSnapshots(long alertEventId, string kind, IReadOnlyList<ProcessUsage> processes)
    {
        if (processes.Count == 0)
        {
            return;
        }

        using var transaction = _connection.BeginTransaction();
        foreach (var process in processes)
        {
            using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO AlertProcessSnapshots (AlertEventId, Kind, ProcessName, ProcessId, CpuPercent, RamMb, IoKbPerSec)
                VALUES ($alertEventId, $kind, $processName, $processId, $cpuPercent, $ramMb, $ioKbPerSec);
                """;
            command.Parameters.AddWithValue("$alertEventId", alertEventId);
            command.Parameters.AddWithValue("$kind", kind);
            command.Parameters.AddWithValue("$processName", process.Name);
            command.Parameters.AddWithValue("$processId", process.Id);
            command.Parameters.AddWithValue("$cpuPercent", process.CpuPercent);
            command.Parameters.AddWithValue("$ramMb", process.RamMb);
            command.Parameters.AddWithValue("$ioKbPerSec", process.IoKbPerSec);
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public void InsertSampleWindow(long alertEventId, IEnumerable<ResourceSample> samples)
    {
        using var transaction = _connection.BeginTransaction();

        foreach (var sample in samples)
        {
            using var insertSample = _connection.CreateCommand();
            insertSample.Transaction = transaction;
            insertSample.CommandText = """
                INSERT INTO Samples
                    (AlertEventId, TimestampUtc, CpuRawPercent, CpuAdjustedPercent, RamRawPercent, RamAdjustedPercent, RamTotalGb, RamAvailableGb)
                VALUES
                    ($alertEventId, $timestamp, $cpuRaw, $cpuAdjusted, $ramRaw, $ramAdjusted, $ramTotal, $ramAvailable);
                SELECT last_insert_rowid();
                """;
            insertSample.Parameters.AddWithValue("$alertEventId", alertEventId);
            insertSample.Parameters.AddWithValue("$timestamp", FormatTimestamp(sample.Timestamp));
            insertSample.Parameters.AddWithValue("$cpuRaw", sample.CpuRawPercent);
            insertSample.Parameters.AddWithValue("$cpuAdjusted", sample.CpuAdjustedPercent);
            insertSample.Parameters.AddWithValue("$ramRaw", sample.RamRawPercent);
            insertSample.Parameters.AddWithValue("$ramAdjusted", sample.RamAdjustedPercent);
            insertSample.Parameters.AddWithValue("$ramTotal", sample.RamTotalGb);
            insertSample.Parameters.AddWithValue("$ramAvailable", sample.RamAvailableGb);

            var sampleId = (long)insertSample.ExecuteScalar()!;

            foreach (var disk in sample.Disks)
            {
                using var insertDisk = _connection.CreateCommand();
                insertDisk.Transaction = transaction;
                insertDisk.CommandText = """
                    INSERT INTO DiskSamples (SampleId, DriveName, FreePercent, FreeGb, TotalGb, IoPercent)
                    VALUES ($sampleId, $driveName, $freePercent, $freeGb, $totalGb, $ioPercent);
                    """;
                insertDisk.Parameters.AddWithValue("$sampleId", sampleId);
                insertDisk.Parameters.AddWithValue("$driveName", disk.DriveName);
                insertDisk.Parameters.AddWithValue("$freePercent", disk.FreePercent);
                insertDisk.Parameters.AddWithValue("$freeGb", disk.FreeGb);
                insertDisk.Parameters.AddWithValue("$totalGb", disk.TotalGb);
                insertDisk.Parameters.AddWithValue("$ioPercent", disk.IoPercent);
                insertDisk.ExecuteNonQuery();
            }
        }

        transaction.Commit();
    }

    // "Heartbeat" chamado a cada tick enquanto o alerta segue ativo — se o app for encerrado
    // sem um End (crash, kill), esse é o último instante confirmado em que o alerta ainda
    // estava de pé, usado como duração mínima conhecida na listagem.
    public void UpdateLastActive(long alertEventId, DateTimeOffset timestamp)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "UPDATE AlertEvents SET LastActiveUtc = $timestamp WHERE Id = $id;";
        command.Parameters.AddWithValue("$timestamp", FormatTimestamp(timestamp));
        command.Parameters.AddWithValue("$id", alertEventId);
        command.ExecuteNonQuery();
    }

    // Chamado no encerramento (Parar manual ou shutdown) pra todo Start que ainda não tinha
    // recebido seu End — marca explicitamente como interrompido, distinto de "ainda monitorando".
    public void MarkInterrupted(long alertEventId, DateTimeOffset timestamp)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "UPDATE AlertEvents SET Interrupted = 1, LastActiveUtc = $timestamp WHERE Id = $id;";
        command.Parameters.AddWithValue("$timestamp", FormatTimestamp(timestamp));
        command.Parameters.AddWithValue("$id", alertEventId);
        command.ExecuteNonQuery();
    }

    // Capturado a cada ~5min pelo loop de monitoramento (ver MonitoringService), não a cada
    // tick — o ON CONFLICT soma em cima do que já existe, então um Parar+Iniciar no mesmo dia
    // simplesmente continua a média de onde parou, sem precisar guardar estado em memória.
    public void UpsertDailyAggregate(
        DateOnly date, double cpuRawPercent, double ramRawPercent, double ioPercent, double diskFreePercent, string systemDrive)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT INTO DailyAggregates (Date, SampleCount, CpuRawSum, RamRawSum, IoPercentSum, DiskFreePercentSum, SystemDrive, LastUpdatedUtc)
            VALUES ($date, 1, $cpuRaw, $ramRaw, $ioPercent, $diskFreePercent, $systemDrive, $now)
            ON CONFLICT(Date) DO UPDATE SET
                SampleCount = SampleCount + 1,
                CpuRawSum = CpuRawSum + $cpuRaw,
                RamRawSum = RamRawSum + $ramRaw,
                IoPercentSum = IoPercentSum + $ioPercent,
                DiskFreePercentSum = DiskFreePercentSum + $diskFreePercent,
                SystemDrive = $systemDrive,
                LastUpdatedUtc = $now;
            """;
        command.Parameters.AddWithValue("$date", date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$cpuRaw", cpuRawPercent);
        command.Parameters.AddWithValue("$ramRaw", ramRawPercent);
        command.Parameters.AddWithValue("$ioPercent", ioPercent);
        command.Parameters.AddWithValue("$diskFreePercent", diskFreePercent);
        command.Parameters.AddWithValue("$systemDrive", systemDrive);
        command.Parameters.AddWithValue("$now", FormatTimestamp(DateTimeOffset.UtcNow));
        command.ExecuteNonQuery();
    }

    // Chamado uma vez ao iniciar uma nova execução (antes do loop começar) — cobre o caso em
    // que a execução ANTERIOR não encerrou normalmente (processo morto/kill, sem chance de
    // rodar o finally do loop) e por isso nunca marcou seus alertas abertos como interrompidos.
    // Reaproveita o mesmo pareamento Start/End de AlertEventQueries.GetAlertEpisodes — um Start
    // sem End correspondente até aqui só pode ter sobrado de uma sessão anterior, já que essa
    // sessão nova ainda não avaliou nenhuma amostra.
    public void MarkOrphanedAlertsInterrupted()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT Id, TimestampUtc, EventType, Metric, DriveName, LastActiveUtc, Interrupted
            FROM AlertEvents
            ORDER BY TimestampUtc;
            """;

        var rawRows = new List<(long Id, DateTimeOffset Timestamp, string EventType, string Metric,
            string? DriveName, DateTimeOffset? LastActiveUtc, bool Interrupted)>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                rawRows.Add((
                    reader.GetInt64(0),
                    ParseTimestamp(reader.GetString(1)),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : ParseTimestamp(reader.GetString(5)),
                    reader.GetInt64(6) != 0));
            }
        }

        var openStarts = new Dictionary<string, (long Id, DateTimeOffset Timestamp, DateTimeOffset? LastActiveUtc, bool Interrupted)>();
        foreach (var row in rawRows)
        {
            var key = $"{row.Metric}|{row.DriveName}";
            if (row.EventType == "Start")
            {
                // Já havia um Start aberto pra essa chave — mesma situação corrigida em
                // AlertEventQueries.GetAlertEpisodes: sem isso, o Start anterior seria perdido
                // silenciosamente na sobrescrita abaixo em vez de marcado como órfão.
                if (openStarts.TryGetValue(key, out var previousOpen) && !previousOpen.Interrupted)
                {
                    MarkInterrupted(previousOpen.Id, previousOpen.LastActiveUtc ?? previousOpen.Timestamp);
                }

                openStarts[key] = (row.Id, row.Timestamp, row.LastActiveUtc, row.Interrupted);
            }
            else
            {
                openStarts.Remove(key);
            }
        }

        foreach (var open in openStarts.Values)
        {
            if (open.Interrupted)
            {
                continue; // já foi marcado (encerramento limpo) — nada a corrigir
            }

            MarkInterrupted(open.Id, open.LastActiveUtc ?? open.Timestamp);
        }
    }

    // Os 3 templates que cobrem o que a antiga aba Dados mostrava fixo (Tendência diária,
    // Ofensores, Base de picos) — semeados uma vez, na primeira execução que criar o banco
    // (ou migrar um banco antigo sem a tabela Templates). "Base de picos" é uma aproximação
    // via subconsulta correlacionada do pareamento Start/End: não reproduz o tratamento de
    // interrompidos/órfãos de AlertEventQueries.GetAlertEpisodes (que continua sendo a fonte
    // de verdade usada por Gráficos > Eventos de Picos) — serve pra leitura/exploração rápida.
    private static readonly (string Name, string Command)[] BuiltInTemplates =
    {
        ("Tendência diária", """
            /* Média diária de uso de CPU, RAM, I/O de disco e espaço em disco, calculada a
               partir das capturas automáticas feitas a cada ~5min. Mostra a tendência de
               consumo ao longo do período selecionado, mesmo sem nenhum alerta ter disparado. */
            SELECT Date,
                   CpuRawSum / SampleCount AS AvgCpuRawPercent,
                   RamRawSum / SampleCount AS AvgRamRawPercent,
                   IoPercentSum / SampleCount AS AvgIoPercent,
                   DiskFreePercentSum / SampleCount AS AvgDiskFreePercent,
                   SystemDrive
            FROM DailyAggregates
            WHERE Date >= date($from) AND Date <= date($to)
            ORDER BY Date;
            """),
        ("Ofensores", """
            /* Processos que mais aparecem como consumidores de destaque nos alertas do
               período: quantas vezes cada um apareceu (OccurrenceCount), o valor médio e
               máximo registrado, e a última vez que foi visto. Ajuda a identificar quem
               repetidamente estoura os limites configurados. */
            SELECT s.ProcessName, s.Kind,
                   COUNT(DISTINCT s.AlertEventId) AS OccurrenceCount,
                   AVG(CASE s.Kind WHEN 'Cpu' THEN s.CpuPercent WHEN 'Ram' THEN s.RamMb ELSE s.IoKbPerSec END) AS AvgValue,
                   MAX(CASE s.Kind WHEN 'Cpu' THEN s.CpuPercent WHEN 'Ram' THEN s.RamMb ELSE s.IoKbPerSec END) AS MaxValue,
                   MAX(e.TimestampUtc) AS LastSeenUtc
            FROM AlertProcessSnapshots s
            JOIN AlertEvents e ON e.Id = s.AlertEventId
            WHERE e.EventType = 'Start' AND e.TimestampUtc >= $from AND e.TimestampUtc <= $to
            GROUP BY s.ProcessName, s.Kind
            ORDER BY OccurrenceCount DESC, AvgValue DESC
            LIMIT 20;
            """),
        ("Base de picos", """
            /* Lista os alertas (picos) do período, pareando o início (Start) com o fim (End)
               de cada episódio pra calcular a duração aproximada. É uma aproximação simples em
               SQL puro — não trata os mesmos casos de borda (interrupção, órfãos) que a lógica
               interna do app usa em Gráficos > Eventos de Picos; serve pra consulta/exploração
               rápida, não é a fonte de verdade oficial. */
            SELECT s.Id AS StartEventId, s.TimestampUtc AS StartTimestamp, s.Metric, s.DriveName,
                   s.RawValue, s.AdjustedValue, s.Threshold, s.Interrupted, e.TimestampUtc AS EndTimestamp,
                   ROUND((julianday(e.TimestampUtc) - julianday(s.TimestampUtc)) * 24 * 60, 1) AS DurationMinutes
            FROM AlertEvents s
            LEFT JOIN AlertEvents e ON e.EventType = 'End' AND e.Metric = s.Metric
                AND IFNULL(e.DriveName, '') = IFNULL(s.DriveName, '')
                AND e.TimestampUtc = (
                    SELECT MIN(e2.TimestampUtc) FROM AlertEvents e2
                    WHERE e2.EventType = 'End' AND e2.Metric = s.Metric
                        AND IFNULL(e2.DriveName, '') = IFNULL(s.DriveName, '')
                        AND e2.TimestampUtc > s.TimestampUtc
                )
            WHERE s.EventType = 'Start' AND s.TimestampUtc >= $from AND s.TimestampUtc <= $to
            ORDER BY s.TimestampUtc DESC;
            """),
    };

    // Recebe a conexão em vez de usar _connection direto — chamado tanto pelo construtor
    // (conexão de escrita normal) quanto por EnsureSchema (idem, mesmo sendo um método
    // estático só de migração: ela abre uma conexão gravável, só quem lê depois é que usa
    // Mode=ReadOnly) — assim um banco de versão antiga, migrado só pelo lado de leitura
    // antes de qualquer execução escrever nele, já ganha os templates padrão também.
    //
    // Roda em toda inicialização, não só quando a tabela está vazia: os 3 templates padrão
    // nunca são editáveis pelo usuário (UpdateTemplate/DeleteTemplate ignoram IsBuiltIn=1),
    // então não existe personalização a perder — se o SQL hardcoded aqui mudar numa
    // atualização do app (ex: corrigir a aproximação de "Base de picos"), quem já tinha o
    // banco criado numa versão anterior precisa ganhar a versão nova automaticamente, sem
    // ficar preso pra sempre num SQL desatualizado que não tem como editar nem recriar.
    // Identificado por Name (+ IsBuiltIn=1) — existe atualiza o Command, não existe insere.
    private static void SeedOrUpdateBuiltInTemplates(SqliteConnection connection)
    {
        foreach (var (name, command) in BuiltInTemplates)
        {
            long? existingId = null;
            using (var checkCommand = connection.CreateCommand())
            {
                checkCommand.CommandText = "SELECT Id FROM Templates WHERE Name = $name AND IsBuiltIn = 1;";
                checkCommand.Parameters.AddWithValue("$name", name);
                if (checkCommand.ExecuteScalar() is { } result)
                {
                    existingId = (long)result;
                }
            }

            using var writeCommand = connection.CreateCommand();
            if (existingId is { } id)
            {
                writeCommand.CommandText = "UPDATE Templates SET Command = $command WHERE Id = $id;";
                writeCommand.Parameters.AddWithValue("$id", id);
                writeCommand.Parameters.AddWithValue("$command", command);
            }
            else
            {
                writeCommand.CommandText = """
                    INSERT INTO Templates (Name, Command, IsBuiltIn)
                    VALUES ($name, $command, 1);
                    """;
                writeCommand.Parameters.AddWithValue("$name", name);
                writeCommand.Parameters.AddWithValue("$command", command);
            }

            writeCommand.ExecuteNonQuery();
        }
    }

    // Estáticos, mesmo padrão de ClearData: abrem sua própria conexão de escrita — a tela de
    // Templates roda na GUI e não tem acesso à instância de PermanentDatabase de longa duração
    // que o MonitoringService mantém internamente, então cada escrita aqui é uma operação
    // avulsa e curta, sem coordenar com o loop de monitoramento.
    public static long InsertTemplate(string databasePath, string name, string command)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath}");
        connection.Open();
        ApplySchema(connection);

        using var insertCommand = connection.CreateCommand();
        insertCommand.CommandText = """
            INSERT INTO Templates (Name, Command, IsBuiltIn)
            VALUES ($name, $command, 0);
            SELECT last_insert_rowid();
            """;
        insertCommand.Parameters.AddWithValue("$name", name);
        insertCommand.Parameters.AddWithValue("$command", command);

        return (long)insertCommand.ExecuteScalar()!;
    }

    public static void UpdateTemplate(string databasePath, long id, string name, string command)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath}");
        connection.Open();

        using var command2 = connection.CreateCommand();
        command2.CommandText = """
            UPDATE Templates SET Name = $name, Command = $command
            WHERE Id = $id AND IsBuiltIn = 0;
            """;
        command2.Parameters.AddWithValue("$id", id);
        command2.Parameters.AddWithValue("$name", name);
        command2.Parameters.AddWithValue("$command", command);
        command2.ExecuteNonQuery();
    }

    public static void DeleteTemplate(string databasePath, long id)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath}");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM Templates WHERE Id = $id AND IsBuiltIn = 0;";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    private static string FormatTimestamp(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    // Limpeza seletiva (painel "Limpeza" na aba Dados) — só deve ser chamada com o
    // monitoramento parado, abre sua própria conexão e não coordena com uma instância de
    // PermanentDatabase que porventura já esteja escrevendo. O cache em memória (CacheDatabase)
    // é uma categoria separada, sem tabela em disco — ver MonitoringService.ClearCache.
    public static void ClearData(string databasePath, bool clearPeaks, bool clearTrend)
    {
        if (!File.Exists(databasePath) || (!clearPeaks && !clearTrend))
        {
            return;
        }

        using var connection = new SqliteConnection($"Data Source={databasePath}");
        connection.Open();

        var statements = new List<string>();
        if (clearPeaks)
        {
            statements.Add("DELETE FROM DiskSamples;");
            statements.Add("DELETE FROM Samples;");
            statements.Add("DELETE FROM AlertProcessSnapshots;");
            statements.Add("DELETE FROM AlertEvents;");
        }

        if (clearTrend)
        {
            statements.Add("DELETE FROM DailyAggregates;");
        }

        statements.Add("VACUUM;");

        using var command = connection.CreateCommand();
        command.CommandText = string.Join("\n", statements);
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        _connection.Dispose();
    }
}
