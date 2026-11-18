using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using SecureChat.Server.Data;

namespace SecureChat.Server.Controllers;

/// <summary>
/// Админский редактор базы данных SQLite сервера. Защищён отдельным паролем
/// из конфигурации (Admin:Password) — доступен только владельцу сервера.
/// Позволяет просматривать таблицы, выполнять SELECT и менять отдельные записи.
/// </summary>
[ApiController]
[Route("api/admin/db")]
public sealed class AdminDbController : ControllerBase
{
    private readonly IConfiguration _configuration;
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<AdminDbController> _logger;

    public AdminDbController(
        IConfiguration configuration,
        IWebHostEnvironment environment,
        ILogger<AdminDbController> logger)
    {
        _configuration = configuration;
        _environment = environment;
        _logger = logger;
    }

    /// <summary>Проверяет пароль администратора из заголовка X-Admin-Password.</summary>
    private bool IsAuthorized()
    {
        var expected = _configuration["Admin:Password"];
        if (string.IsNullOrWhiteSpace(expected))
        {
            return false;
        }

        var provided = Request.Headers["X-Admin-Password"].ToString();
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var providedBytes = Encoding.UTF8.GetBytes(provided);
        // Выравниваем длины, чтобы не утекать информацию через время сравнения.
        if (providedBytes.Length != expectedBytes.Length)
        {
            return false;
        }
        return CryptographicOperations.FixedTimeEquals(expectedBytes, providedBytes);
    }

    private string DatabasePath =>
        ServerDataPaths.ResolveDatabasePath("App_Data/securechat.db", _environment);

    [HttpGet("tables")]
    public async Task<IActionResult> Tables(CancellationToken cancellationToken)
    {
        if (!IsAuthorized())
        {
            return Unauthorized();
        }

        await using var connection = new SqliteConnection($"Data Source={DatabasePath}");
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT name FROM sqlite_master
            WHERE type = 'table' AND name NOT LIKE 'sqlite_%'
            ORDER BY name;
            """;

        var tables = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            tables.Add(reader.GetString(0));
        }

        return Ok(tables);
    }

    /// <summary>Возвращает первые 200 строк таблицы и её колонки.</summary>
    [HttpGet("rows")]
    public async Task<IActionResult> Rows([FromQuery] string table, CancellationToken cancellationToken)
    {
        if (!IsAuthorized())
        {
            return Unauthorized();
        }

        if (!IsSafeIdentifier(table))
        {
            return BadRequest("Некорректное имя таблицы.");
        }

        await using var connection = new SqliteConnection($"Data Source={DatabasePath}");
        await connection.OpenAsync(cancellationToken);

        var columns = new List<string>();
        await using (var info = connection.CreateCommand())
        {
            info.CommandText = $"PRAGMA table_info(\"{table}\");";
            await using var reader = await info.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                columns.Add(reader.GetString(1));
            }
        }

        var rows = new List<Dictionary<string, object?>>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = $"SELECT * FROM \"{table}\" LIMIT 200;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var row = new Dictionary<string, object?>();
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i).ToString();
                }
                rows.Add(row);
            }
        }

        return Ok(new { table, columns, rows });
    }

    /// <summary>Выполняет SELECT-запрос только для чтения.</summary>
    [HttpPost("query")]
    public async Task<IActionResult> Query([FromBody] QueryRequest request, CancellationToken cancellationToken)
    {
        if (!IsAuthorized())
        {
            return Unauthorized();
        }

        var sql = request.Sql?.Trim() ?? string.Empty;
        if (sql.Length == 0)
        {
            return BadRequest("Пустой запрос.");
        }

        var firstWord = sql.Split(' ', 2)[0].ToUpperInvariant();
        // WITH в SQLite может начинать INSERT/UPDATE/DELETE — разрешаем только SELECT и PRAGMA.
        if (firstWord != "SELECT" && firstWord != "PRAGMA")
        {
            return BadRequest("Разрешены только запросы на чтение (SELECT, PRAGMA).");
        }

        try
        {
            await using var connection = new SqliteConnection($"Data Source={DatabasePath}");
            await connection.OpenAsync(cancellationToken);

            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.CommandTimeout = 15;

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var columns = Enumerable.Range(0, reader.FieldCount)
                .Select(reader.GetName)
                .ToList();
            var rows = new List<Dictionary<string, object?>>();

            while (await reader.ReadAsync(cancellationToken))
            {
                var row = new Dictionary<string, object?>();
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i).ToString();
                }
                rows.Add(row);
                if (rows.Count >= 500)
                {
                    break;
                }
            }

            return Ok(new { columns, rows });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ошибка админского SQL-запроса");
            return BadRequest(ex.Message);
        }
    }

    /// <summary>Обновляет одно поле одной записи по первичному ключу.</summary>
    [HttpPost("update")]
    public async Task<IActionResult> Update([FromBody] UpdateRequest request, CancellationToken cancellationToken)
    {
        if (!IsAuthorized())
        {
            return Unauthorized();
        }

        if (!IsSafeIdentifier(request.Table) || !IsSafeIdentifier(request.Column) ||
            !IsSafeIdentifier(request.KeyColumn))
        {
            return BadRequest("Некорректное имя таблицы или колонки.");
        }

        await using var connection = new SqliteConnection($"Data Source={DatabasePath}");
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            $"UPDATE \"{request.Table}\" SET \"{request.Column}\" = $value WHERE \"{request.KeyColumn}\" = $key;";
        command.Parameters.AddWithValue("$value", (object?)request.Value ?? DBNull.Value);
        command.Parameters.AddWithValue("$key", (object?)request.KeyValue ?? DBNull.Value);

        var changed = await command.ExecuteNonQueryAsync(cancellationToken);
        return Ok(new { changed });
    }

    private static bool IsSafeIdentifier(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.All(ch => char.IsLetterOrDigit(ch) || ch == '_');

    public sealed class QueryRequest
    {
        public string? Sql { get; set; }
    }

    public sealed class UpdateRequest
    {
        public string Table { get; set; } = string.Empty;
        public string Column { get; set; } = string.Empty;
        public string KeyColumn { get; set; } = string.Empty;
        public string? KeyValue { get; set; }
        public string? Value { get; set; }
    }
}
