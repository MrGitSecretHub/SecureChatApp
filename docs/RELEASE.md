# Подготовка релиза SecureChat 1.1

Этот файл — чек-лист и список артефактов для публикации версии 1.1.
Документ на русском; текст для GitHub Release лежит в
[RELEASE_NOTES_1.1.md](RELEASE_NOTES_1.1.md).

## Опубликованные релизы

- 1.2: https://github.com/MrGitSecretHub/SecureChatApp/releases/tag/v1.2 —
  ассеты `SecureChat-v1.2.apk`, `SecureChat-1.2-win.zip`, `SecureChat.App.exe`.
- 1.1: https://github.com/MrGitSecretHub/SecureChatApp/releases/tag/v1.1

Полная папка `publish\win` поставляется ZIP-архивом — `exe` один не запустится
без DLL.

## Версия

- `ApplicationDisplayVersion` — `1.2` (`src/SecureChat.App/SecureChat.App.csproj`).
- `ApplicationVersion` (Android versionCode) — `3`. При повторной публикации
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

## Проверка артефакта из GitHub Release

Скачайте APK из Release и сверьте его с локальной сборкой:

```powershell
$apk = "C:\Users\Administrator\Desktop\SecureChat-APK\SecureChat-1.1.apk"
Get-FileHash $apk -Algorithm SHA256
```

Ожидаемый хеш APK 1.2:
`B9A5E3873340AEAB97BFDAC0B61049A7B42FB378CD0C383F4F65059BB547E5C2`.
Для сравнения, APK 1.1:
`6CB00B1D2DAA72CA399581C8DE1BFB4F1B310098CF62392A8A72A1105D4B4B31`.

Подпись должна совпадать с сертификатом из `keys/securechat-release.keystore`:

```powershell
& "C:\Program Files (x86)\Android\android-sdk\build-tools\35.0.0\apksigner.bat" `
  verify --verbose --print-certs $apk
```

Ожидаемый `Signer #1 certificate SHA-256 digest`:
`dee444519e73b76bf940cb63d4bbf13783198308447d2eac1ed28030002c4664`.
Версия пакета: `versionCode=2`, `versionName=1.1`, подпись v2 и v3.
Если сертификат отличается, APK собран отладочным ключом — он не встанет
поверх уже установленного приложения.

Хеш Windows `publish\win\SecureChat.App.exe`:
`904CA121A60FB653C9B1A08D4AC2388B78E678AFAD37A684DD02E024D74BDD92`.

## Что осталось заблокированным

1. **Push-уведомления в фоне** — нужны Firebase-проект (FCM) или публичный
   ntfy-сервер и адрес, доступный с телефона.
2. **Скриншоты для README** — нужны изображения от пользователя
   (список чатов, переписка, пространства, истории, настройки).
3. **Секреты CI для подписи APK** — `ANDROID_KEYSTORE_BASE64`,
   `ANDROID_KEYSTORE_PASS`, `ANDROID_KEY_ALIAS`. Если Release уже собран
   без них, проверьте подпись по инструкции выше; при несовпадении добавьте
   секреты и перезапустите workflow.
