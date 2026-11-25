# Подготовка релиза SecureChat 1.1

Этот файл — чек-лист и список артефактов для публикации версии 1.1.
Документ на русском; текст для GitHub Release лежит в
[RELEASE_NOTES_1.1.md](RELEASE_NOTES_1.1.md).

## Версия

- `ApplicationDisplayVersion` — `1.1` (`src/SecureChat.App/SecureChat.App.csproj`).
- `ApplicationVersion` (Android versionCode) — `2`. При повторной публикации
  увеличивать на 1, иначе Android не примет обновление.
- Префикс тега: `v1.1`. Workflow `.github/workflows/release.yml` собирает APK
  и Windows-версию по любому тегу `v*`.

## Артефакты

| Платформа | Файл | Назначение |
|---|---|---|
| Android | `C:\Users\Administrator\Desktop\SecureChat-APK\SecureChat-v1.1.apk` | Подписанный APK для установки на устройство |
| Windows | `publish\win\SecureChat.App.exe` | Самостоятельный запуск без Visual Studio |
| Server | `src\SecureChat.Server` (исходники + `dotnet run`) | Сервер разворачивается из каталога проекта, чтобы найти `App_Data` и LAN-сертификат |

Размер APK (последняя сборка) — 66.26 МБ, подпись release-ключом из
`keys/securechat-release.keystore` (файл не хранится в git).

## Проверки перед публикацией

```powershell
dotnet test
dotnet build src/SecureChat.Server/SecureChat.Server.csproj -c Release
dotnet publish src/SecureChat.App/SecureChat.App.csproj -f net9.0-windows10.0.19041.0 -c Release -o publish/win
dotnet build src/SecureChat.App/SecureChat.App.csproj -f net9.0-android -c Release
```

Ожидаемый результат: Core **119/119**, Server **5/5**. Затем поднять сервер
из `src\SecureChat.Server` и проверить `https://localhost:5443/api/health`
→ `{"status":"ok"}`.

## Что осталось заблокированным

1. **GitHub Release** — в репозитории нет `remote`. Нужно сообщить URL,
   выполнить `git remote add origin <URL>` и запушить коммиты и тег `v1.1`.
2. **Секреты CI для подписи APK** — `ANDROID_KEYSTORE_BASE64`,
   `ANDROID_KEYSTORE_PASS`, `ANDROID_KEY_ALIAS`. Без них GitHub Actions
   подпишет APK отладочным ключом — такое обновление не встанет поверх
   уже установленного приложения.
3. **Push-уведомления в фоне** — нужны Firebase-проект (FCM) или публичный
   ntfy-сервер и адрес, доступный с телефона.
4. **Скриншоты для README** — нужны изображения от пользователя
   (список чатов, переписка, пространства, истории, настройки).
