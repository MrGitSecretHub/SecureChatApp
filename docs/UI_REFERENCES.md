# UI-референсы других мессенджеров

Только для визуального изучения. **Скриншоты не копировать в приложение** — у них свои лицензии
(у Element/Signal — AGPL, у Telegram — GPLv3 на клиенты). Смотрим паттерны, берём идеи.

## Как пользоваться этим файлом

Для каждого элемента ниже указано: где посмотреть → что именно смотреть → где в нашем коде это реализовано.
Перед правкой открыть референс, сравнить, потом делать точечное изменение.

---

## 1. Список чатов + stories-полоса (Telegram)

- **Где:** https://en.wikipedia.org/wiki/Telegram_(software)
  (на странице есть скриншоты, раздел Features → Stories, Messaging)
- **Дополнительно:** https://commons.wikimedia.org/wiki/Category:Telegram_(software)
- **Что смотреть:**
  - компактная строка чата: аватар слева, жирное имя, превью серым, время/счётчик справа;
  - stories-полоса над папками, кружки с градиентом у непросмотренных;
  - плавающая кнопка `+` внизу справа;
  - вкладки папок в ряд под поиском.
- **Наш код:** `Pages/ChatsPage.xaml`, `Pages/ChatsPage.xaml.cs`
- **Статус:** компактный список сделан, stories-полоса есть, кнопка `+` есть.

## 2. Контекстное меню сообщения (Telegram)

- **Где:** та же страница Wikipedia, раздел Messaging; скриншот «secret chat confirmation notice» —
  пример того, как выглядит диалог подтверждения ключа.
- **Что смотреть:**
  - по тапу на сообщение появляется компактный ряд иконок (копировать, ответить, реакция, переслать);
  - кнопки полупрозрачные, не перекрывают текст;
  - меню появляется только у выбранного сообщения.
- **Наш код:** `Pages/SpaceChatPage.xaml` (панель `SpaceActionButton` + `IsVisible="{Binding IsSelected}"`),
  `Pages/SpaceChatPage.xaml.cs` (`OnBubbleTapped`)
- **Статус:** сделано по тапу.

## 3. Лента канала и комментарии (Telegram)

- **Где:** Wikipedia, раздел «Groups and channels» — Channels.
- **Что смотреть:**
  - пост канала с подписью «канал через @админ»;
  - счётчик просмотров 👁 и кнопка комментариев под постом;
  - ветка комментариев открывается отдельным экраном с превью поста вверху.
- **Наш код:** `Pages/SpaceChatPage.xaml`, `Pages/PostCommentsPage.xaml(.cs)`
- **Статус:** есть, комменты сейчас дорабатываются (кэш + кнопка обновления).

## 4. Полная цветовая схема светлая/тёмная (Element X)

- **Где (прямые скриншоты, 8 штук):**
  - https://raw.githubusercontent.com/element-hq/element-x-android/develop/docs/images-lfs/screen_1_light.png
  - https://raw.githubusercontent.com/element-hq/element-x-android/develop/docs/images-lfs/screen_2_light.png
  - https://raw.githubusercontent.com/element-hq/element-x-android/develop/docs/images-lfs/screen_3_light.png
  - https://raw.githubusercontent.com/element-hq/element-x-android/develop/docs/images-lfs/screen_4_light.png
  - то же с `_dark.png` вместо `_light.png`
  - галерея по экранам: https://element-hq.github.io/element-x-android/
- **Что смотреть:**
  - как выглядит E2E-чат: бейджи верификации, иконки замка, участники комнаты;
  - аккуратные пустые состояния и отступы;
  - как одна и та же схема работает в светлой и тёмной теме.
- **Наш код:** `App.xaml` (цвета), `Pages/SettingsPage.xaml`, `Services/LocalizationService.cs`
- **Статус:** темы есть, бейджей верификации в шапке пока нет — хороший следующий шаг.

## 5. Ключи и безопасность (Signal / Element)

- **Где:**
  - Signal: https://signal.org/android/ (скриншоты на странице), https://support.signal.org/
  - Signal GitHub: https://github.com/signalapp/Signal-Android (AGPL — только изучать)
  - Element X: https://github.com/element-hq/element-x-android (AGPL — только изучать)
- **Что смотреть:**
  - как подан safety number / QR-код верификации;
  - как выглядит предупреждение «ключ собеседника изменился»;
  - как показано, что сообщение зашифровано, не перегружая интерфейс.
- **Наш код:** `Pages/QrVerifyPage` (или equivalent), `Services/ChatManager.cs` (TOFU), `Views/MessageVm.cs`
- **Статус:** QR-верификация есть; бейджа «ключ подтверждён/изменён» в шапке нет.

## 6. Поиск пользователей (Telegram)

- **Где:** Wikipedia, раздел Features → Messaging; скриншоты поиска людей.
- **Что смотреть:**
  - results: аватар, жирное отображаемое имя, синий `@username`, статус онлайна;
  - недавние поиски при пустом вводе;
  - состояние «ничего не найдено».
- **Наш код:** `Pages/SearchUsersPage.xaml(.cs)`
- **Статус:** живой поиск, подсказки, недавние и «не найдено» сделаны.

## 7. Отложенные/запланированные сообщения (Telegram)

- **Где:** Wikipedia, Features → Messaging (uprecated «schedule message»).
- **Что смотреть:** как кнопка отправки превращается в кнопку «отправить позже» при долгом нажатии.
- **Наш код:** `Pages/SpaceChatPage.xaml` (кнопка ⏰ рядом с ➤), `Scheduling/`
- **Статус:** отдельная кнопка ⏰ сделана; долгое нажатие на ➤ — не реализовано.

---

## Итог: что из референсов реально недоделано

1. Бейдж верификации ключа в шапке чата (Signal/Element).
2. Долгое нажатие на кнопку отправки → выбор времени (Telegram).
3. Аккуратные пустые состояния в стиле Element X.

Остальное визуально уже совпадает с ориентирами.
