namespace SecureChat.App.Views;

/// <summary>
/// Полноэкранный проигрыватель видео и аудио для расшифрованных вложений.
/// На Android файл встраивается в HTML как base64 data URI; на Windows этого
/// нельзя сделать — WebView2 ограничивает NavigateToString 2 МБ, поэтому HTML
/// пишется рядом с медиафайлом и открывается через file://.
/// Управление (пауза, прогресс, перемотка, полный экран) реализовано
/// в HTML/CSS/JS и работает одинаково на обеих платформах.
/// </summary>
public partial class MediaPlayerPage : ContentPage
{
    public MediaPlayerPage(string filePath, string? fileName, string? contentType, bool isVideoCircle = false)
    {
        InitializeComponent();

        Title = string.IsNullOrWhiteSpace(fileName) ? "Медиа" : fileName;
        BindingContext = this;

        var isAudio = ResolveMediaType(contentType, fileName) == "audio";
        var fileTitle = string.IsNullOrWhiteSpace(fileName) ? "Медиа" : fileName;
        var safeTitle = System.Net.WebUtility.HtmlEncode(fileTitle);

#if WINDOWS
        // Лёгкий HTML рядом с медиафайлом: видео подключается относительной
        // ссылкой, base64-вставка не нужна и не упирается в лимит WebView2.
        var dir = Path.GetDirectoryName(filePath);
        if (string.IsNullOrEmpty(dir))
        {
            dir = Path.GetTempPath();
        }

        var htmlPath = Path.Combine(dir, Path.GetFileNameWithoutExtension(filePath) + ".player.html");
        File.WriteAllText(htmlPath, BuildMediaHtml(
            Path.GetFileName(filePath), isAudio, isVideoCircle, safeTitle));

        MediaWebView.Source = new UrlWebViewSource { Url = new Uri(htmlPath).AbsoluteUri };
#else
        // Android: встраиваем файл как data URI — WebView не зависит
        // от доступа к file://.
        var mime = ResolveMime(contentType, filePath);
        var bytes = File.ReadAllBytes(filePath);
        var base64 = Convert.ToBase64String(bytes);
        var dataUri = $"data:{mime};base64,{base64}";

        MediaWebView.Source = new HtmlWebViewSource
        {
            Html = BuildMediaHtml(dataUri, isAudio, isVideoCircle, safeTitle)
        };
#endif
    }

    public string? FileName => Title;

    private static string BuildMediaHtml(string mediaSrc, bool isAudio, bool isVideoCircle, string fileTitle)
    {
        var mediaTag = isAudio
            ? $"""<audio id="player" preload="metadata" src="{mediaSrc}"></audio>"""
            : $"""<video id="player" playsinline webkit-playsinline preload="metadata" src="{mediaSrc}"{(isVideoCircle ? " loop" : string.Empty)}></video>""";

        var bodyClass = isAudio ? "audio-mode" : isVideoCircle ? "circle-mode" : "video-mode";
        var fullscreenClass = isAudio ? "hidden" : string.Empty;

        return $$"""
<!DOCTYPE html>
<html lang="ru">
<head>
    <meta charset="utf-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0, viewport-fit=cover">
    <title>{{fileTitle}}</title>
    <style>
        * { box-sizing: border-box; -webkit-tap-highlight-color: transparent; }
        html, body {
            margin: 0; padding: 0; width: 100%; height: 100%;
            background: #000; color: #fff; overflow: hidden;
            font-family: -apple-system, "Segoe UI", Roboto, Arial, sans-serif;
            user-select: none; -webkit-user-select: none;
        }
        #stage {
            position: relative; width: 100%; height: 100%;
            display: flex; align-items: center; justify-content: center;
            background: #000;
        }
        #player {
            width: 100%; height: 100%; object-fit: contain;
            background: #000; display: block; outline: none;
        }
        body.circle-mode #player {
            width: min(78vw, 62vh); height: min(78vw, 62vh);
            border-radius: 50%; object-fit: cover;
            box-shadow: 0 0 0 2px rgba(255, 255, 255, 0.10);
        }
        body.audio-mode #player { display: none; }
        #art {
            display: none; flex-direction: column; align-items: center;
            justify-content: center; gap: 18px; width: 100%; height: 100%;
        }
        body.audio-mode #art { display: flex; }
        #art .disc {
            width: 168px; height: 168px; border-radius: 50%;
            background: linear-gradient(135deg, #0a84ff, #5e5ce6);
            display: flex; align-items: center; justify-content: center;
            font-size: 68px; box-shadow: 0 12px 40px rgba(10, 132, 255, 0.35);
        }
        #art .name {
            max-width: 78vw; font-size: 15px; color: #d7d7d7;
            text-align: center; overflow: hidden; text-overflow: ellipsis;
            white-space: nowrap;
        }
        #bigPlay {
            position: absolute; left: 50%; top: 50%;
            transform: translate(-50%, -50%);
            width: 78px; height: 78px; border-radius: 50%;
            border: 0; padding: 0 0 0 5px;
            background: rgba(0, 0, 0, 0.48); color: #fff;
            font-size: 30px; line-height: 1;
            display: flex; align-items: center; justify-content: center;
            backdrop-filter: blur(8px); -webkit-backdrop-filter: blur(8px);
            transition: opacity 0.18s ease; cursor: pointer; z-index: 3;
        }
        #bigPlay.hidden { opacity: 0; pointer-events: none; }
        #controls {
            position: absolute; left: 0; right: 0; bottom: 0;
            display: flex; align-items: center; gap: 10px;
            padding: 18px 14px calc(14px + env(safe-area-inset-bottom));
            background: linear-gradient(to top, rgba(0, 0, 0, 0.82), rgba(0, 0, 0, 0));
            z-index: 4;
        }
        #controls button {
            flex: 0 0 auto; width: 34px; height: 34px;
            border: 0; border-radius: 50%; padding: 0;
            background: rgba(255, 255, 255, 0.14); color: #fff;
            font-size: 15px; line-height: 1; cursor: pointer;
        }
        #controls button.hidden { display: none; }
        #seek {
            flex: 1 1 auto; min-width: 0; height: 4px;
            appearance: none; -webkit-appearance: none;
            border-radius: 2px; outline: none; margin: 0;
            background: rgba(255, 255, 255, 0.25);
        }
        #seek::-webkit-slider-thumb {
            appearance: none; -webkit-appearance: none;
            width: 14px; height: 14px; border-radius: 50%;
            background: #fff; box-shadow: 0 0 5px rgba(0, 0, 0, 0.55);
        }
        #seek::-moz-range-thumb {
            width: 14px; height: 14px; border: 0; border-radius: 50%;
            background: #fff; box-shadow: 0 0 5px rgba(0, 0, 0, 0.55);
        }
        .time {
            flex: 0 0 auto; min-width: 36px; text-align: center;
            font-size: 12px; color: #d7d7d7;
            font-variant-numeric: tabular-nums;
        }
    </style>
</head>
<body class="{{bodyClass}}">
    <div id="stage">
        {{mediaTag}}
        <div id="art">
            <div class="disc">🎧</div>
            <div class="name">{{fileTitle}}</div>
        </div>
        <button id="bigPlay" type="button" aria-label="Воспроизвести">▶</button>
        <div id="controls">
            <button id="playBtn" type="button" aria-label="Пауза">▶</button>
            <span id="cur" class="time">0:00</span>
            <input id="seek" type="range" min="0" max="1000" value="0" step="1" aria-label="Перемотка">
            <span id="dur" class="time">0:00</span>
            <button id="fsBtn" type="button" class="{{fullscreenClass}}" aria-label="На весь экран">⛶</button>
        </div>
    </div>
    <script>
        (function () {
            var player = document.getElementById('player');
            var stage = document.getElementById('stage');
            var bigPlay = document.getElementById('bigPlay');
            var playBtn = document.getElementById('playBtn');
            var seek = document.getElementById('seek');
            var cur = document.getElementById('cur');
            var dur = document.getElementById('dur');
            var fsBtn = document.getElementById('fsBtn');
            var seeking = false;

            function fmt(value) {
                if (!isFinite(value) || value < 0) { value = 0; }
                var total = Math.floor(value);
                var h = Math.floor(total / 3600);
                var m = Math.floor((total % 3600) / 60);
                var s = total % 60;
                return (h > 0 ? h + ':' + String(m).padStart(2, '0') : String(m)) + ':' + String(s).padStart(2, '0');
            }

            function paint() {
                var d = player.duration;
                var p = player.currentTime;
                cur.textContent = fmt(p);
                dur.textContent = (isFinite(d) && d > 0) ? fmt(d) : '0:00';
                var percent = (isFinite(d) && d > 0) ? Math.max(0, Math.min(100, p / d * 100)) : 0;
                if (!seeking) { seek.value = String(percent * 10); }
                seek.style.background = 'linear-gradient(to right, #0a84ff 0%, #0a84ff ' + percent + '%, rgba(255,255,255,0.25) ' + percent + '%, rgba(255,255,255,0.25) 100%)';
            }

            function syncIcon() {
                var paused = player.paused;
                playBtn.textContent = paused ? '▶' : '⏸';
                bigPlay.textContent = '▶';
                bigPlay.classList.toggle('hidden', !paused);
            }

            function toggle() {
                if (player.paused) {
                    player.play().catch(function () { syncIcon(); });
                } else {
                    player.pause();
                }
            }

            player.addEventListener('loadedmetadata', function () { paint(); });
            player.addEventListener('durationchange', paint);
            player.addEventListener('timeupdate', paint);
            player.addEventListener('play', syncIcon);
            player.addEventListener('pause', syncIcon);
            player.addEventListener('ended', function () { paint(); syncIcon(); });
            player.addEventListener('click', function (event) { event.stopPropagation(); toggle(); });
            stage.addEventListener('click', function (event) {
                if (event.target === stage || event.target.id === 'art') { toggle(); }
            });

            bigPlay.addEventListener('click', function (event) { event.stopPropagation(); toggle(); });
            playBtn.addEventListener('click', function (event) { event.stopPropagation(); toggle(); });

            seek.addEventListener('pointerdown', function () { seeking = true; });
            seek.addEventListener('touchstart', function () { seeking = true; }, { passive: true });
            seek.addEventListener('input', function () {
                var d = player.duration;
                if (isFinite(d) && d > 0) {
                    player.currentTime = (Number(seek.value) / 1000) * d;
                    paint();
                }
            });
            seek.addEventListener('change', function () { seeking = false; });
            seek.addEventListener('touchend', function () { seeking = false; }, { passive: true });

            if (fsBtn) {
                fsBtn.addEventListener('click', function (event) {
                    event.stopPropagation();
                    if (player.requestFullscreen) {
                        player.requestFullscreen().catch(function () { });
                    } else if (player.webkitEnterFullscreen) {
                        player.webkitEnterFullscreen();
                    }
                });
            }

            document.addEventListener('keydown', function (event) {
                if (event.code === 'Space') {
                    event.preventDefault();
                    toggle();
                } else if (event.code === 'ArrowRight') {
                    player.currentTime = Math.min(player.duration || 0, player.currentTime + 5);
                } else if (event.code === 'ArrowLeft') {
                    player.currentTime = Math.max(0, player.currentTime - 5);
                }
            });

            paint();
            syncIcon();
            player.play().catch(function () { syncIcon(); });
        })();
    </script>
</body>
</html>
""";
    }

    private static string ResolveMediaType(string? contentType, string? fileName)
    {
        if (!string.IsNullOrWhiteSpace(contentType))
        {
            var ct = contentType.ToLowerInvariant();
            if (ct.StartsWith("audio/"))
            {
                return "audio";
            }
            if (ct.StartsWith("video/"))
            {
                return "video";
            }
        }

        return Path.GetExtension(fileName ?? string.Empty).ToLowerInvariant() switch
        {
            ".mp3" or ".wav" or ".ogg" or ".oga" or ".aac" or ".flac" or ".wma" or ".m4a" => "audio",
            _ => "video"
        };
    }

    private static string ResolveMime(string? contentType, string filePath)
    {
        if (!string.IsNullOrWhiteSpace(contentType))
        {
            var ct = contentType.Split(';', 2)[0].Trim().ToLowerInvariant();
            if (ct.StartsWith("audio/") || ct.StartsWith("video/"))
            {
                return ct;
            }
        }

        return Path.GetExtension(filePath).ToLowerInvariant() switch
        {
            ".mp4" => "video/mp4",
            ".webm" => "video/webm",
            ".ogv" => "video/ogg",
            ".mov" => "video/quicktime",
            ".mkv" => "video/x-matroska",
            ".avi" => "video/x-msvideo",
            ".3gp" => "video/3gpp",
            ".mp3" => "audio/mpeg",
            ".wav" => "audio/wav",
            ".oga" => "audio/ogg",
            ".aac" => "audio/aac",
            ".flac" => "audio/flac",
            ".wma" => "audio/x-ms-wma",
            ".m4a" => "audio/mp4",
            _ => "application/octet-stream"
        };
    }

    private async void OnCloseClicked(object? sender, EventArgs e)
    {
        MediaWebView.Source = null;
        await Navigation.PopModalAsync();
    }
}
