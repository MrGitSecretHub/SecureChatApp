using System.Text.Json;
using Microsoft.Maui;
using Microsoft.Maui.Storage;
using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace SecureChat.App.WinUI;

/// <summary>Сохранение и восстановление размера и положения окна Windows.</summary>
internal static class WindowPlacement
{
    private const int DefaultMinimumWidth = 900;
    private const int DefaultMinimumHeight = 600;
    private const int MinimumVisiblePart = 80;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private static CancellationTokenSource? _saveCts;

    private static string FilePath =>
        Path.Combine(FileSystem.AppDataDirectory, "securechat.window.json");

    public static void Attach(Microsoft.Maui.Controls.Window window)
    {
        if (window.Handler?.PlatformView is not MauiWinUIWindow nativeWindow)
        {
            return;
        }

        var appWindow = nativeWindow.AppWindow;
        if (appWindow is null)
        {
            return;
        }

        Restore(appWindow);

        try
        {
            if (appWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.PreferredMinimumWidth = DefaultMinimumWidth;
                presenter.PreferredMinimumHeight = DefaultMinimumHeight;
            }
        }
        catch
        {
            // Минимальный размер — дополнительное удобство, не критичен.
        }

        appWindow.Changed += (_, args) =>
        {
            if (args.DidPositionChange || args.DidSizeChange)
            {
                ScheduleSave(Capture(appWindow));
            }
        };

        nativeWindow.Closed += (_, _) =>
        {
            _saveCts?.Cancel();
            Save(Capture(appWindow));
        };
    }

    private static void Restore(AppWindow appWindow)
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return;
            }

            var state = JsonSerializer.Deserialize<PlacementState>(
                File.ReadAllText(FilePath),
                JsonOptions);
            if (state is null)
            {
                return;
            }

            var width = Math.Clamp(state.Width, DefaultMinimumWidth, 10000);
            var height = Math.Clamp(state.Height, DefaultMinimumHeight, 8000);

            var area = DisplayArea.GetFromPoint(
                new PointInt32(state.X + 40, state.Y + 40),
                DisplayAreaFallback.Nearest);
            var workArea = area.WorkArea;

            width = Math.Min(width, workArea.Width);
            height = Math.Min(height, workArea.Height);

            var x = Math.Clamp(
                state.X,
                workArea.X,
                workArea.X + workArea.Width - MinimumVisiblePart);
            var y = Math.Clamp(
                state.Y,
                workArea.Y,
                workArea.Y + workArea.Height - MinimumVisiblePart);

            x = Math.Min(x, workArea.X + workArea.Width - width);
            y = Math.Min(y, workArea.Y + workArea.Height - height);

            appWindow.MoveAndResize(new RectInt32(x, y, width, height));
        }
        catch
        {
            // Повреждённый файл или изменившаяся конфигурация мониторов:
            // используем размеры по умолчанию из App.CreateWindow.
        }
    }

    private static PlacementState Capture(AppWindow appWindow) => new()
    {
        X = appWindow.Position.X,
        Y = appWindow.Position.Y,
        Width = appWindow.Size.Width,
        Height = appWindow.Size.Height
    };

    private static void ScheduleSave(PlacementState state)
    {
        _saveCts?.Cancel();
        _saveCts?.Dispose();
        _saveCts = new CancellationTokenSource();
        var token = _saveCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(500, token);
                Save(state);
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    private static void Save(PlacementState state)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var tempPath = FilePath + ".tmp";
            File.WriteAllText(tempPath, JsonSerializer.Serialize(state, JsonOptions));
            File.Move(tempPath, FilePath, overwrite: true);
        }
        catch
        {
            // Геометрия окна не является критичными данными.
        }
    }

    private sealed class PlacementState
    {
        public int X { get; set; }
        public int Y { get; set; }
        public int Width { get; set; } = 1180;
        public int Height { get; set; } = 800;
    }
}
