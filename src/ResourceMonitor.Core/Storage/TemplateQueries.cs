using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using ResourceMonitor.Diagnostics;

namespace ResourceMonitor.Storage;

public sealed record TemplateRow(long Id, string Name, string Command, bool IsBuiltIn);

// Consultas de leitura pra tela de Templates (ex-aba Dados) — mesmo padrão de
// AlertEventQueries: abre uma conexão curta por chamada, pensado pra uso interativo.
public sealed class TemplateQueries
{
    private readonly ITraceLogger _traceLogger;

    public TemplateQueries(ITraceLogger traceLogger)
    {
        _traceLogger = traceLogger;
    }

    public List<TemplateRow> GetAll(string databasePath)
    {
        if (!File.Exists(databasePath))
        {
            return new List<TemplateRow>();
        }

        PermanentDatabase.EnsureSchema(databasePath);

        using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, Name, Command, IsBuiltIn FROM Templates ORDER BY IsBuiltIn DESC, Name;";

        var results = new List<TemplateRow>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new TemplateRow(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt64(3) != 0));
        }

        return results;
    }

    // Intervalo real dos dados existentes (AlertEvents + DailyAggregates) — usado pra
    // pré-selecionar De/Até na primeira vez que a tela de Templates abre, em vez do padrão
    // fixo de 7 dias (que fica vazio se os dados existentes forem mais antigos que isso;
    // mesmo problema já resolvido em Gráficos > Eventos de Picos).
    public (DateTimeOffset? Min, DateTimeOffset? Max) GetOverallDateRange(string databasePath)
    {
        if (!File.Exists(databasePath))
        {
            return (null, null);
        }

        PermanentDatabase.EnsureSchema(databasePath);

        using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT MIN(ts), MAX(ts) FROM (
                SELECT TimestampUtc AS ts FROM AlertEvents
                UNION ALL
                SELECT Date || 'T00:00:00.0000000+00:00' AS ts FROM DailyAggregates
            );
            """;

        using var reader = command.ExecuteReader();
        if (reader.Read() && !reader.IsDBNull(0) && !reader.IsDBNull(1))
        {
            return (ParseTimestamp(reader.GetString(0)), ParseTimestamp(reader.GetString(1)));
        }

        return (null, null);
    }

    // Executa uma consulta arbitrária somente-leitura. Duas camadas de proteção contra
    // escrita: (1) validação leve de texto abaixo (mensagem amigável antes de tocar no
    // banco); (2) Mode=ReadOnly na conexão — garantia de verdade, no nível do próprio
    // SQLite, que barra qualquer INSERT/UPDATE/DELETE/DDL que passar da camada (1).
    // $from/$to sempre disponíveis pro comando (SQLite ignora parâmetro não referenciado
    // no texto) — mesmo formato ISO-8601 UTC já usado em AlertEventQueries.
    public (IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<string?>> Rows) ExecuteReadOnly(
        string databasePath, string command, DateTimeOffset from, DateTimeOffset to)
    {
        ValidateReadOnly(command);

        if (!File.Exists(databasePath))
        {
            return (Array.Empty<string>(), Array.Empty<IReadOnlyList<string?>>());
        }

        PermanentDatabase.EnsureSchema(databasePath);

        using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly");
        connection.Open();

        using var sqlCommand = connection.CreateCommand();
        sqlCommand.CommandText = command;
        sqlCommand.Parameters.AddWithValue("$from", FormatTimestamp(from));
        sqlCommand.Parameters.AddWithValue("$to", FormatTimestamp(to));

        using var reader = sqlCommand.ExecuteReader();

        var columns = new List<string>();
        for (var i = 0; i < reader.FieldCount; i++)
        {
            columns.Add(reader.GetName(i));
        }

        var rows = new List<IReadOnlyList<string?>>();
        while (reader.Read())
        {
            var row = new string?[reader.FieldCount];
            for (var i = 0; i < reader.FieldCount; i++)
            {
                row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i).ToString();
            }

            rows.Add(row);
        }

        _traceLogger.Trace("TemplateQueries", $"ExecuteReadOnly retornou {rows.Count} linha(s), {columns.Count} coluna(s).");

        return (columns, rows);
    }

    // Só bloqueia o caso óbvio (múltiplos statements, ou não começa com SELECT/WITH) —
    // não é um parser de SQL. A garantia real é o Mode=ReadOnly da conexão em
    // ExecuteReadOnly; isso aqui só existe pra dar um erro legível antes de bater no banco.
    // Comentários (`-- ...` até o fim da linha, `/* ... */`) são ignorados só pra essa checagem
    // — um `;` dentro de um comentário não conta como separador de statement, e um comentário
    // antes do SELECT não impede reconhecer que a consulta começa com SELECT/WITH. O texto
    // original (com os comentários) continua sendo o que é salvo e executado — o SQLite já
    // entende os dois estilos de comentário nativamente, não precisa remover de verdade.
    public static void ValidateReadOnly(string command)
    {
        if (command.Trim().Length == 0)
        {
            throw new InvalidOperationException("Digite uma consulta.");
        }

        var withoutComments = StripComments(command).Trim();
        var withoutTrailingSemicolon = withoutComments.TrimEnd(';', ' ', '\t', '\r', '\n');

        if (withoutTrailingSemicolon.Length == 0)
        {
            throw new InvalidOperationException("Digite uma consulta (só comentário não é suficiente).");
        }

        if (withoutTrailingSemicolon.Contains(';'))
        {
            throw new InvalidOperationException("Só é permitido um único comando SELECT (sem ; no meio do texto).");
        }

        var startsWithSelect = withoutTrailingSemicolon.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
            || withoutTrailingSemicolon.StartsWith("WITH", StringComparison.OrdinalIgnoreCase);
        if (!startsWithSelect)
        {
            throw new InvalidOperationException("Só são permitidas consultas SELECT (ou WITH ... SELECT).");
        }
    }

    private static string StripComments(string sql)
    {
        var withoutLineComments = Regex.Replace(sql, "--[^\n]*", "");
        return Regex.Replace(withoutLineComments, @"/\*.*?\*/", "", RegexOptions.Singleline);
    }

    private static string FormatTimestamp(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
