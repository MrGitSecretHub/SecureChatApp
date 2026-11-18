using SecureChat.App.Services;

namespace SecureChat.App.Views;

/// <summary>
/// Полноэкранный проигрыватель видео и аудио для расшифрованных вложений.
/// На Android файл встраивается в HTML как base64 data URI; на Windows этого
/// нельзя сделать — WebView2 ограничивает NavigateToString 2 МБ, поэтому HTML
/// пишется рядом с медиафайлом и открывается через file://.
/// Управление (пауза, перемотка ±10 с и двойным тапом, скорость 0,5–2×,
/// громкость и звук, повтор, «картинка в картинке», полный экран) реализовано
/// в HTML/CSS/JS и работает одинаково на обеих платформах. Громкость, скорость
/// и повтор запоминаются в localStorage и восстанавливаются при след. открытии.
/// </summary>
public partial class MediaPlayerPage : ContentPage
{
    private readonly string _filePath;
    private readonly string? _contentType;

    public MediaPlayerPage(string filePath, string? fileName, string? contentType, bool isVideoCircle = false)
    {
        InitializeComponent();

        _filePath = filePath;
        _contentType = contentType;

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
        // Кнопки настроек: полный экран и PiP — только видео; повтор не нужен
        // для кружков (они и так повторяются).
        var fullscreenClass = isAudio ? "hidden" : string.Empty;
        var pipClass = isAudio ? "hidden" : string.Empty;
        var loopClass = isVideoCircle ? "hidden" : string.Empty;

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
            display: flex; flex-direction: column; gap: 2px;
            padding: 14px 14px calc(12px + env(safe-area-inset-bottom));
            background: linear-gradient(to top, rgba(0, 0, 0, 0.82), rgba(0, 0, 0, 0));
            z-index: 4;
        }
        .row {
            display: flex; align-items: center; gap: 7px; min-height: 34px;
        }
        .row.extra { justify-content: flex-end; min-height: 30px; }
        #controls button {
            flex: 0 0 auto; width: 32px; height: 32px;
            border: 0; border-radius: 50%; padding: 0;
            background: rgba(255, 255, 255, 0.14); color: #fff;
            font-size: 15px; line-height: 1; cursor: pointer;
        }
        #controls button.hidden { display: none; }
        #controls button.on {
            background: rgba(10, 132, 255, 0.6);
        }
        #speedBtn {
            width: auto !important; min-width: 48px; height: 30px;
            border-radius: 15px; padding: 0 10px;
            font-size: 12px; font-weight: 600; letter-spacing: 0.3px;
        }
        input[type="range"] {
            appearance: none; -webkit-appearance: none;
            height: 4px; border-radius: 2px; outline: none; margin: 0;
            background: rgba(255, 255, 255, 0.25);
        }
        input[type="range"]::-webkit-slider-thumb {
            appearance: none; -webkit-appearance: none;
            width: 14px; height: 14px; border-radius: 50%;
            background: #fff; box-shadow: 0 0 5px rgba(0, 0, 0, 0.55);
            border: 0;
        }
        input[type="range"]::-moz-range-thumb {
            width: 14px; height: 14px; border: 0; border-radius: 50%;
            background: #fff; box-shadow: 0 0 5px rgba(0, 0, 0, 0.55);
        }
        #seek { flex: 1 1 auto; min-width: 40px; }
        #vol { flex: 0 0 auto; width: 56px; }
        @media (max-width: 400px) { #vol { display: none; } }
        .time {
            flex: 0 0 auto; min-width: 36px; text-align: center;
            font-size: 12px; color: #d7d7d7;
            font-variant-numeric: tabular-nums;
        }
        #speedMenu {
            position: absolute; right: 14px; bottom: 108px;
            z-index: 6; min-width: 132px;
            background: rgba(24, 24, 27, 0.97); border-radius: 12px;
            padding: 4px; box-shadow: 0 10px 30px rgba(0, 0, 0, 0.6);
            display: flex; flex-direction: column;
        }
        #speedMenu.hidden { display: none; }
        #speedMenu button {
            background: transparent; color: #e7e7e7; border: 0;
            text-align: left; font-size: 13px; padding: 9px 12px;
            border-radius: 8px; cursor: pointer;
        }
        #speedMenu button:hover { background: rgba(255, 255, 255, 0.08); }
        #speedMenu button.on { color: #0a84ff; font-weight: 700; }
    </style>
</head>
<body class="{{bodyClass}}">
    <div id="stage">
        {{mediaTag}}
        <div id="art">
            <div class="disc">🎧</div>
            <div class="name">{{fileTitle}}</div>
        </div>
        <button id="bigPlay" type="button" aria-label=Loc.T("rs_024", "Воспроизвести")>▶</button>
        <div id="controls">
            <div class="row">
                <button id="playBtn" type="button" aria-label=Loc.T("rs_122", "Пауза")>▶</button>
                <button id="backBtn" type="button" aria-label=Loc.T("rs_076", "Назад на 10 с")>⏪</button>
                <span id="cur" class="time">0:00</span>
                <input id="seek" type="range" min="0" max="1000" value="0" step="1" aria-label=Loc.T("rs_124", "Перемотка")>
                <span id="dur" class="time">0:00</span>
                <button id="fwdBtn" type="button" aria-label=Loc.T("rs_025", "Вперёд на 10 с")>⏩</button>
                <button id="muteBtn" type="button" aria-label=Loc.T("rs_049", "Звук")>🔊</button>
                <input id="vol" type="range" min="0" max="1" step="0.02" value="1" aria-label=Loc.T("rs_039", "Громкость")>
            </div>
            <div class="row extra">
                <button id="speedBtn" type="button" aria-label=Loc.T("rs_147", "Скорость воспроизведения")>1×</button>
                <button id="loopBtn" type="button" class="{{loopClass}}" aria-label=Loc.T("rs_128", "Повтор")>🔁</button>
                <button id="pipBtn" type="button" class="{{pipClass}}" aria-label=Loc.T("rs_067", "Картинка в картинке")>🖼</button>
                <button id="fsBtn" type="button" class="{{fullscreenClass}}" aria-label=Loc.T("rs_075", "На весь экран")>⛶</button>
            </div>
        </div>
        <div id="speedMenu" class="hidden">
            <button type="button" data-rate="0.5">0,5× быстрее</button>
            <button type="button" data-rate="0.75">0,75×</button>
            <button type="button" data-rate="1">Обычная (1×)</button>
            <button type="button" data-rate="1.25">1,25×</button>
            <button type="button" data-rate="1.5">1,5×</button>
            <button type="button" data-rate="2">2× быстрее</button>
        </div>
    </div>
    <script>
        (function () {
            var player = document.getElementById('player');
            var stage = document.getElementById('stage');
            var bigPlay = document.getElementById('bigPlay');
            var playBtn = document.getElementById('playBtn');
            var backBtn = document.getElementById('backBtn');
            var fwdBtn = document.getElementById('fwdBtn');
            var seek = document.getElementById('seek');
            var cur = document.getElementById('cur');
            var dur = document.getElementById('dur');
            var muteBtn = document.getElementById('muteBtn');
            var vol = document.getElementById('vol');
            var speedBtn = document.getElementById('speedBtn');
            var loopBtn = document.getElementById('loopBtn');
            var pipBtn = document.getElementById('pipBtn');
            var fsBtn = document.getElementById('fsBtn');
            var speedMenu = document.getElementById('speedMenu');
            var rates = [0.5, 0.75, 1, 1.25, 1.5, 2];
            var rateNames = {
                '0.5': '0,5×', '0.75': '0,75×', '1': '1×',
                '1.25': '1,25×', '1.5': '1,5×', '2': '2×'
            };
            var isVideo = !document.body.classList.contains('audio-mode');
            var isCircle = document.body.classList.contains('circle-mode');
            var seeking = false;

            function fmt(value) {
                if (!isFinite(value) || value < 0) { value = 0; }
                var total = Math.floor(value);
                var h = Math.floor(total / 3600);
                var m = Math.floor((total % 3600) / 60);
                var s = total % 60;
                return (h > 0 ? h + ':' + String(m).padStart(2, '0') : String(m)) + ':' + String(s).padStart(2, '0');
            }

            // localStorage недоступен для data-URI на части WebView — заворачиваем в try/catch.
            function store(key, value) {
                try { window.localStorage.setItem(key, value); } catch (err) { }
            }
            function load(key) {
                try { return window.localStorage.getItem(key); } catch (err) { return null; }
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

            function paintVol() {
                var percent = Math.round(player.volume * 100);
                var line = player.muted ? 0 : percent;
                vol.value = String(percent / 100);
                vol.style.background = 'linear-gradient(to right, #0a84ff 0%, #0a84ff ' + line + '%, rgba(255,255,255,0.25) ' + line + '%, rgba(255,255,255,0.25) 100%)';
            }

            function rateLabel(value) {
                var keys = Object.keys(rateNames);
                for (var i = 0; i < keys.length; i++) {
                    if (Math.abs(parseFloat(keys[i]) - value) < 0.001) {
                        return rateNames[keys[i]];
                    }
                }
                return '1×';
            }

            function markSpeed(value) {
                var buttons = speedMenu.querySelectorAll('button[data-rate]');
                for (var i = 0; i < buttons.length; i++) {
                    var rate = parseFloat(buttons[i].getAttribute('data-rate'));
                    buttons[i].classList.toggle('on', Math.abs(rate - value) < 0.001);
                }
            }

            function hideSpeedMenu() {
                speedMenu.classList.add('hidden');
                speedBtn.classList.remove('on');
            }

            function setVolume(value) {
                player.volume = Math.max(0, Math.min(1, value));
                if (player.volume > 0) { player.muted = false; }
                muteBtn.textContent = '🔊';
                paintVol();
                store('sc_vol', String(player.volume));
                store('sc_muted', '0');
            }

            function toggleMute() {
                player.muted = !player.muted;
                muteBtn.textContent = player.muted ? '🔇' : '🔊';
                paintVol();
                store('sc_muted', player.muted ? '1' : '0');
            }

            function toggleLoop() {
                player.loop = !player.loop;
                loopBtn.classList.toggle('on', player.loop);
                store('sc_loop', player.loop ? '1' : '0');
            }

            function applySpeed(value) {
                player.playbackRate = value;
                speedBtn.textContent = rateLabel(value);
                markSpeed(value);
                store('sc_speed', String(value));
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

            function skip(delta) {
                var d = player.duration;
                var target = player.currentTime + delta;
                player.currentTime = Math.max(0, Math.min(isFinite(d) && d > 0 ? d : target, target));
                paint();
            }

            player.addEventListener('loadedmetadata', function () { paint(); paintVol(); });
            player.addEventListener('durationchange', paint);
            player.addEventListener('timeupdate', paint);
            player.addEventListener('volumechange', function () {
                muteBtn.textContent = player.muted ? '🔇' : '🔊';
                paintVol();
                if (!player.muted) { store('sc_vol', String(player.volume)); }
            });
            player.addEventListener('play', syncIcon);
            player.addEventListener('pause', syncIcon);
            player.addEventListener('ended', function () { paint(); syncIcon(); });
            player.addEventListener('click', function (event) { event.stopPropagation(); toggle(); });
            stage.addEventListener('click', function (event) {
                hideSpeedMenu();
                if (event.target === stage || event.target.id === 'art') { toggle(); }
            });
            // Двойной тап/клик по половинам кадра: −10 с / +10 с, как в Telegram.
            stage.addEventListener('dblclick', function (event) {
                var hit = event.target;
                if (!isVideo ||
                    (hit !== stage && hit !== player && (!hit || hit.id !== 'art'))) {
                    return;
                }
                var rect = stage.getBoundingClientRect();
                skip((event.clientX - rect.left) < rect.width / 2 ? -10 : 10);
            });

            bigPlay.addEventListener('click', function (event) { event.stopPropagation(); toggle(); });
            playBtn.addEventListener('click', function (event) { event.stopPropagation(); toggle(); });
            backBtn.addEventListener('click', function (event) { event.stopPropagation(); skip(-10); });
            fwdBtn.addEventListener('click', function (event) { event.stopPropagation(); skip(10); });
            muteBtn.addEventListener('click', function (event) { event.stopPropagation(); toggleMute(); });
            loopBtn.addEventListener('click', function (event) { event.stopPropagation(); toggleLoop(); });

            speedBtn.addEventListener('click', function (event) {
                event.stopPropagation();
                var hidden = speedMenu.classList.toggle('hidden');
                speedBtn.classList.toggle('on', !hidden);
            });
            speedMenu.addEventListener('click', function (event) {
                event.stopPropagation();
                var rate = event.target.getAttribute ? event.target.getAttribute('data-rate') : null;
                if (rate) { applySpeed(parseFloat(rate)); }
                hideSpeedMenu();
            });

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

            vol.addEventListener('input', function () { setVolume(Number(vol.value)); });

            if (fsBtn && !fsBtn.classList.contains('hidden')) {
                fsBtn.addEventListener('click', function (event) {
                    event.stopPropagation();
                    if (player.requestFullscreen) {
                        player.requestFullscreen().catch(function () { });
                    } else if (player.webkitEnterFullscreen) {
                        player.webkitEnterFullscreen();
                    }
                });
            }
            if (pipBtn && !pipBtn.classList.contains('hidden')) {
                var pipOk = window.document.pictureInPictureEnabled &&
                    typeof player.requestPictureInPicture === 'function';
                if (!pipOk) {
                    pipBtn.classList.add('hidden');
                } else {
                    pipBtn.addEventListener('click', function (event) {
                        event.stopPropagation();
                        if (window.document.pictureInPictureElement) {
                            window.document.exitPictureInPicture().catch(function () { });
                        } else {
                            player.requestPictureInPicture().catch(function () { });
                        }
                    });
                }
            }

            document.addEventListener('keydown', function (event) {
                if (event.code === 'Space') {
                    event.preventDefault();
                    toggle();
                } else if (event.code === 'ArrowRight') {
                    skip(5);
                } else if (event.code === 'ArrowLeft') {
                    skip(-5);
                } else if (event.code === 'ArrowUp') {
                    event.preventDefault();
                    setVolume(player.volume + 0.1);
                } else if (event.code === 'ArrowDown') {
                    event.preventDefault();
                    setVolume(player.volume - 0.1);
                } else if (event.code === 'KeyM') {
                    toggleMute();
                } else if (event.code === 'KeyL') {
                    toggleLoop();
                }
            });

            // Восстановление настроек прошлой сессии.
            var savedVol = parseFloat(load('sc_vol') || '1');
            if (!isNaN(savedVol) && savedVol >= 0 && savedVol <= 1) { player.volume = savedVol; }
            var savedMuted = load('sc_muted');
            if (savedMuted === '1') { player.muted = true; }
            var savedLoop = load('sc_loop');
            player.loop = (savedLoop === '1') || isCircle;
            loopBtn.classList.toggle('on', player.loop);
            var savedSpeed = parseFloat(load('sc_speed') || '1');
            if (!isNaN(savedSpeed) && rates.indexOf(savedSpeed) >= 0) {
                player.playbackRate = savedSpeed;
            }
            muteBtn.textContent = player.muted ? '🔇' : '🔊';
            paintVol();
            applySpeed(player.playbackRate);

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

    /// <summary>Сохранить видео или аудио на устройство.</summary>
    private async void OnSaveClicked(object? sender, EventArgs e)
    {
        try
        {
            var fileName = string.IsNullOrWhiteSpace(Title) ? "media" : Title;
            var bytes = await File.ReadAllBytesAsync(_filePath);
            var contentType = ResolveMime(_contentType, _filePath);

            var result = await AppServices.Get<MediaSaveService>()
                .SaveAsync(fileName, contentType, bytes);

            if (string.IsNullOrEmpty(result))
            {
                return;
            }

            await DisplayAlert(
                result.StartsWith(Loc.T("uc_022", "Не удалось"), StringComparison.Ordinal) ? Loc.T("common_error", "Ошибка") : Loc.T("dg_064", "Сохранено"),
                result,
                "OK");
        }
        catch (Exception ex)
        {
            await DisplayAlert(Loc.T("common_error", "Ошибка"), string.Format(Loc.T("uc_045"), ex.Message), "OK");
        }
    }
}
