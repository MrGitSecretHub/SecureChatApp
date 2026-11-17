using System.Data;
using Microsoft.Data.Sqlite;

namespace SecureChat.DbEditor;

/// <summary>
/// GUI-редактор базы данных SQLite сервера SecureChat.
/// Открывает файл .db напрямую, показывает таблицы и позволяет править ячейки.
/// </summary>
public sealed class MainForm : Form
{
    private readonly TextBox _pathBox = new() { ReadOnly = true, Dock = DockStyle.Fill };
    private readonly Button _openButton = new() { Text = "Открыть БД…", AutoSize = true };
    private readonly ComboBox _tablesBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
    private readonly Button _refreshButton = new() { Text = "Обновить", AutoSize = true };
    private readonly DataGridView _grid = new()
    {
        Dock = DockStyle.Fill,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        ReadOnly = true,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        SelectionMode = DataGridViewSelectionMode.CellSelect
    };
    private readonly TextBox _sqlBox = new()
    {
        Dock = DockStyle.Fill,
        Multiline = true,
        ScrollBars = ScrollBars.Vertical,
        Font = new Font("Consolas", 10)
    };
    private readonly Button _queryButton = new() { Text = "Выполнить SELECT", AutoSize = true };
    private readonly Label _status = new() { Dock = DockStyle.Bottom, Height = 26, Text = "Откройте файл securechat.db" };
    private readonly Button _saveButton = new() { Text = "💾 Сохранить изменения", AutoSize = true };

    private string _databasePath = string.Empty;
    private DataTable? _currentTable;
    private string? _currentTableName;
    private readonly HashSet<string> _primaryKeys = new(StringComparer.OrdinalIgnoreCase);

    public MainForm()
    {
        Text = "SecureChat — редактор базы данных";
        Width = 1200;
        Height = 760;
        StartPosition = FormStartPosition.CenterScreen;

        // Компоновка строками: шапка файла, выбор таблицы, сетка, SQL, статус.
        // TableLayoutPanel гарантирует, что сетка займёт именно среднюю строку
        // и её заголовки колонок всегда будут видны.
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            Padding = new Padding(0)
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 120));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        Controls.Add(layout);

        // Верхняя панель: файл БД
        var topPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(8, 6, 8, 6),
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false
        };
        topPanel.Controls.Add(_openButton);
        var pathPanel = new Panel { Width = 620, Height = 28, Margin = new Padding(8, 0, 8, 0) };
        _pathBox.Dock = DockStyle.Fill;
        pathPanel.Controls.Add(_pathBox);
        topPanel.Controls.Add(pathPanel);
        topPanel.Controls.Add(_refreshButton);
        topPanel.Controls.Add(_saveButton);
        layout.Controls.Add(topPanel, 0, 0);

        // Панель таблиц
        var tablePanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(8, 6, 8, 6),
            FlowDirection = FlowDirection.LeftToRight
        };
        tablePanel.Controls.Add(new Label { Text = "Таблица:", AutoSize = true, Padding = new Padding(0, 6, 6, 0) });
        tablePanel.Controls.Add(_tablesBox);
        layout.Controls.Add(tablePanel, 0, 1);

        // Сетка — средняя строка, заголовки колонок видны всегда.
        _grid.ColumnHeadersVisible = true;
        _grid.RowHeadersVisible = true;
        _grid.Dock = DockStyle.Fill;
        _grid.Margin = new Padding(8, 0, 8, 0);
        layout.Controls.Add(_grid, 0, 2);

        // SQL-панель
        var sqlPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8) };
        var sqlHeader = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 28 };
        sqlHeader.Controls.Add(new Label { Text = "SQL-запрос (SELECT / PRAGMA):", AutoSize = true });
        sqlHeader.Controls.Add(_queryButton);
        sqlPanel.Controls.Add(sqlHeader);
        _sqlBox.Dock = DockStyle.Fill;
        sqlPanel.Controls.Add(_sqlBox);
        layout.Controls.Add(sqlPanel, 0, 3);

        // Статус
        _status.Dock = DockStyle.Fill;
        _status.Padding = new Padding(10, 4, 0, 0);
        layout.Controls.Add(_status, 0, 4);

        _openButton.Click += (_, _) => OpenDatabase();
        _refreshButton.Click += (_, _) => LoadTables();
        _tablesBox.SelectedIndexChanged += (_, _) => LoadTable(_tablesBox.SelectedItem?.ToString());
        _queryButton.Click += (_, _) => RunQuery();
        _saveButton.Click += (_, _) => SaveChanges();
    }

    [STAThread]
    public static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }

    private void OpenDatabase()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Выберите файл базы данных SecureChat",
            Filter = "SQLite база (*.db)|*.db|Все файлы|*.*",
            FileName = "securechat.db"
        };

        if (dialog.ShowDialog() != DialogResult.OK)
        {
            return;
        }

        _databasePath = dialog.FileName;
        _pathBox.Text = _databasePath;
        _status.Text = $"Открыта БД: {_databasePath}";
        LoadTables();
    }

    private void LoadTables()
    {
        if (string.IsNullOrEmpty(_databasePath))
        {
            return;
        }

        try
        {
            using var connection = new SqliteConnection($"Data Source={_databasePath}");
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT name FROM sqlite_master
                WHERE type = 'table' AND name NOT LIKE 'sqlite_%'
                ORDER BY name;
                """;

            var tables = new List<string>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                tables.Add(reader.GetString(0));
            }

            _tablesBox.Items.Clear();
            _tablesBox.Items.AddRange(tables.Cast<object>().ToArray());
            if (tables.Count > 0)
            {
                _tablesBox.SelectedIndex = 0;
            }

            _status.Text = $"Таблиц: {tables.Count}";
        }
        catch (Exception ex)
        {
            _status.Text = "Ошибка: " + ex.Message;
            MessageBox.Show(ex.Message, "Ошибка открытия БД", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void LoadTable(string? tableName)
    {
        if (string.IsNullOrEmpty(tableName) || string.IsNullOrEmpty(_databasePath))
        {
            return;
        }

        try
        {
            using var connection = new SqliteConnection($"Data Source={_databasePath}");
            connection.Open();

            // Первичный ключ нужен, чтобы обновлять конкретную строку.
            _primaryKeys.Clear();
            using (var info = connection.CreateCommand())
            {
                info.CommandText = $"PRAGMA table_info(\"{tableName}\");";
                using var reader = info.ExecuteReader();
                while (reader.Read())
                {
                    if (reader.GetInt32(5) > 0)
                    {
                        _primaryKeys.Add(reader.GetString(1));
                    }
                }
            }

            _currentTable = new DataTable(tableName);
            using (var command = connection.CreateCommand())
            {
                command.CommandText = $"SELECT * FROM \"{tableName}\" LIMIT 500;";
                using var reader = command.ExecuteReader();
                FillTable(_currentTable, reader);
            }

            _currentTableName = tableName;
            _grid.DataSource = _currentTable;
            _grid.ReadOnly = _primaryKeys.Count == 0;
            _status.Text = _primaryKeys.Count == 0
                ? $"{tableName}: {_currentTable.Rows.Count} строк (нет первичного ключа — только чтение)"
                : $"{tableName}: {_currentTable.Rows.Count} строк. Правьте ячейки и нажмите «Сохранить».";
        }
        catch (Exception ex)
        {
            _status.Text = "Ошибка: " + ex.Message;
            MessageBox.Show(ex.Message, "Ошибка загрузки таблицы", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void RunQuery()
    {
        if (string.IsNullOrEmpty(_databasePath))
        {
            MessageBox.Show("Сначала откройте базу данных.", "Внимание", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var sql = _sqlBox.Text.Trim();
        if (sql.Length == 0)
        {
            return;
        }

        var first = sql.Split(' ', 2)[0].ToUpperInvariant();
        if (first is not ("SELECT" or "PRAGMA" or "WITH"))
        {
            MessageBox.Show("Разрешены только запросы на чтение: SELECT, PRAGMA, WITH.",
                "Только чтение", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        try
        {
            using var connection = new SqliteConnection($"Data Source={_databasePath}");
            connection.Open();

            var table = new DataTable();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = sql;
                command.CommandTimeout = 15;
                using var reader = command.ExecuteReader();
                FillTable(table, reader);
            }

            _currentTable = table;
            _currentTableName = null;
            _grid.DataSource = table;
            _grid.ReadOnly = true;
            _status.Text = $"Запрос выполнен: {table.Rows.Count} строк";
        }
        catch (Exception ex)
        {
            _status.Text = "Ошибка: " + ex.Message;
            MessageBox.Show(ex.Message, "Ошибка запроса", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>Заполняет DataTable из SqliteDataReader вручную.</summary>
    private static void FillTable(DataTable table, SqliteDataReader reader)
    {
        for (var i = 0; i < reader.FieldCount; i++)
        {
            table.Columns.Add(reader.GetName(i), typeof(object));
        }

        while (reader.Read())
        {
            var row = table.NewRow();
            for (var i = 0; i < reader.FieldCount; i++)
            {
                var value = reader.IsDBNull(i) ? DBNull.Value : reader.GetValue(i);

                // BLOB показываем в HEX, а не «System.Byte[]».
                if (value is byte[] bytes)
                {
                    var preview = bytes.Length > 16 ? bytes[..16] : bytes;
                    value = Convert.ToHexString(preview).ToLowerInvariant() +
                            (bytes.Length > 16 ? $"… ({bytes.Length} байт)" : string.Empty);
                }

                row[i] = value;
            }
            table.Rows.Add(row);
        }

        table.AcceptChanges();
    }

    private void SaveChanges()
    {
        if (_currentTable is null || _currentTableName is null || _primaryKeys.Count == 0)
        {
            MessageBox.Show("Откройте таблицу с первичным ключом и измените ячейку.",
                "Нечего сохранять", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var changes = _currentTable.GetChanges();
        if (changes is null || changes.Rows.Count == 0)
        {
            MessageBox.Show("Изменений нет.", "Сохранение", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (MessageBox.Show($"Сохранить {changes.Rows.Count} изменённых строк в таблицу «{_currentTableName}»?",
                "Подтверждение", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
        {
            return;
        }

        try
        {
            using var connection = new SqliteConnection($"Data Source={_databasePath}");
            connection.Open();
            using var transaction = connection.BeginTransaction();

            foreach (DataRow row in changes.Rows)
            {
                var setParts = new List<string>();
                var parameters = new List<SqliteParameter>();

                foreach (DataColumn column in _currentTable.Columns)
                {
                    if (_primaryKeys.Contains(column.ColumnName))
                    {
                        continue;
                    }

                    setParts.Add($"\"{column.ColumnName}\" = $p{parameters.Count}");
                    parameters.Add(new SqliteParameter(
                        $"$p{parameters.Count}",
                        row[column] == DBNull.Value ? DBNull.Value : row[column]));
                }

                var whereParts = new List<string>();
                foreach (var key in _primaryKeys)
                {
                    whereParts.Add($"\"{key}\" = $k{whereParts.Count}");
                    parameters.Add(new SqliteParameter($" $k{whereParts.Count}".Trim(), row[key]));
                }

                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    $"UPDATE \"{_currentTableName}\" SET {string.Join(", ", setParts)} WHERE {string.Join(" AND ", whereParts)};";
                command.Parameters.AddRange(parameters.ToArray());
                command.ExecuteNonQuery();
            }

            transaction.Commit();
            _currentTable.AcceptChanges();
            _status.Text = $"Сохранено строк: {changes.Rows.Count}";
        }
        catch (Exception ex)
        {
            _status.Text = "Ошибка сохранения: " + ex.Message;
            MessageBox.Show(ex.Message, "Ошибка сохранения", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
