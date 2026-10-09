# Сторонние компоненты и источники

В готовой сборке используются:

| Компонент | Версия | Источник / лицензия |
|---|---|---|
| .NET runtime / WPF | .NET 10 | Microsoft, MIT и notices дистрибутива |
| LibVLCSharp.WPF / LibVLCSharp | 3.9.5 | [VideoLAN](https://code.videolan.org/videolan/LibVLCSharp), LGPL-2.1 |
| VideoLAN.LibVLC.Windows | 3.0.23 | [VideoLAN VLC](https://www.videolan.org/vlc/), LibVLC LGPL-2.1, плагины и зависимости имеют собственные лицензии |
| NAudio | 2.2.1 | [naudio/NAudio](https://github.com/naudio/NAudio), MIT |

LibVLC поставляется как отдельные динамически загружаемые DLL и плагины. Они не модифицированы. Исходники движка доступны у VideoLAN; NuGet-пакет доступен через [официальную страницу пакета](https://www.nuget.org/packages/VideoLAN.LibVLC.Windows/3.0.23). Копии файлов лицензий поставляются в каталоге `licenses` дистрибутива.

При восстановлении протоколов изучены следующие проекты. Они не запускаются в готовом клиенте:

- `lolmaxlevel/domru-ha`, MIT;
- `z81/domru`, открытый исходный код; Rust-код не включён в приложение;
- `gentslava/elektronny-gorod`, MIT;
- `sdb9696/firebase-messaging`, MIT, описания регистрации FCM и MCS;
- `beeper/push-receiver`, MIT, описания checkin/MCS; публичные protobuf-схемы восходят к Chromium, BSD-style;
- `http_ece`, Mozilla, MIT — только для генерации независимых тестовых векторов.

ПК-клиент содержит собственную C#-реализацию протоколов. Android-код, изображения, рекламные SDK и Linphone `.so` из APK в дистрибутив не включены. Название сервиса используется для обозначения совместимости с аккаунтом пользователя.
