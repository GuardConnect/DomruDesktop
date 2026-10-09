# Анализ «Умный Дом.ру» 9.11.0 и устройство ПК-клиента


## 1. Идентификация APK

| Параметр | Значение |
|---|---|
| SHA-256 | `2db8c97ef873eddf72670171061d6e35e0a9bbd50768461e45a81c9021208072` |
| Размер | 126 560 642 байта |
| Package | `com.ertelecom.smarthome` |
| versionName / versionCode | `9.11.0` / `91100000` |
| minSdk / targetSdk | 23 / 37 |
| DEX | `classes.dex`, `classes2.dex`, `classes3.dex` |
| ABI | arm64-v8a, armeabi-v7a, x86, x86_64 |

JADX 1.5.6 обработал 15 488 классов. Декомпиляция завершилась с 265 ошибками: часть Kotlin-корутин и обфусцированных методов восстановлена неточно. Такие фрагменты не принимались за достоверную исходную логику без дополнительного подтверждения. Имена классов в `defpackage` отражают обфускацию R8, а не исходные имена разработчиков.


## 2. Архитектура Android-приложения

Это модульное Kotlin/Android-приложение. Метаданные показывают модули `core`, `shared` и `feature`: сеть и авторизация, синхронизация адресов/домофонов, события, камеры, видеоплеер, скачивание архива, уведомления, звонки, чат и дополнительные услуги. Встречаются Android Views и Compose, coroutine/Flow, Retrofit-подобные аннотации API, OkHttp, Moshi и kotlinx.serialization. Локальная история завершённых звонков содержит `callId` и время для подавления повторных уведомлений.

Нативная часть содержит Linphone, mediastreamer2, ortp, bctoolbox, SRTP и WebRTC-компоненты. Это Android `.so`; их нельзя непосредственно загрузить в Windows как DLL. Поэтому ПК-клиент повторяет необходимые сетевые протоколы на C#, а видео воспроизводит через Windows LibVLC.

В APK присутствуют Firebase, AppMetrica/Yandex, рекламные и служебные интеграции. Они не переносились в ПК-клиент. У него нет рекламного потока или отправки аналитики.

## 3. REST API и авторизация


Основные HTTP-заголовки:

- `Authorization: Bearer <accessToken>`;
- `Operator: <operatorId>`;
- User-Agent с моделью, Android-версией, `erth`, версией приложения, installationId и контекстом адреса.

### Вход по договору

`POST /auth/v2/auth/{login}/password` передаёт `login`, `timestamp`, `hash1`, `hash2`.

`hash1` — Base64(SHA-1(password)), совместимый код использует Latin-1. `hash2` — MD5 UTF-8 строки:

```text
DigitalHomeNTKpassword + login + password + UTC(yyyyMMddHHmmss) + 789sdgHJs678wertv34712376
```

`timestamp` в JSON имеет формат UTC `yyyy-MM-ddTHH:mm:ssZ`. Константа в этой формуле извлекается из клиента и не является паролем пользователя. Реальные значения пароля и токенов не входят в опубликованные материалы.

### Вход по телефону

1. `GET /auth/v2/login/{phone}` — поиск вариантов аккаунта, включая ответ HTTP 300 при нескольких договорах.
2. `POST /auth/v2/confirmation/{phone}` — запрос кода для выбранного договора.
3. `POST /auth/v3/auth/{phone}/confirmation` — код и контекст договора: operatorId, accountId, profileId, subscriberId, login, confirm1, confirm2.

Старые интеграции по-разному описывают эти шаги; реализация не трактует GET confirmation как обязательную отправку SMS. Этот способ входа не проверялся с отправкой реального SMS в данной сессии.

### Обновление сессии

`GET /auth/v2/session/refresh`, заголовок `Bearer: <refreshToken>`, плюс Operator. Сервер может менять токены; ПК-клиент сохраняет их обновлённые значения. При 401 выполняется одно обновление и повтор запроса. Сетевой таймаут команды открытия не вызывает автоматическое повторное открытие.

## 4. Адреса, домофоны и дверь

| Операция | Endpoint |
|---|---|
| Адреса | `GET /rest/v3/subscriber-places` |
| Домофоны адреса | `GET /rest/v1/places/{placeId}/accesscontrols` |
| Профиль | `GET /rest/v1/subscribers/profiles` |
| SIP-учётная запись установки | `POST /rest/v1/places/{placeId}/accesscontrols/{id}/sipdevices` |
| Основная дверь | `POST /rest/v1/places/{placeId}/accesscontrols/{id}/actions` |
| Отдельная точка доступа | `POST /rest/v1/places/{placeId}/accesscontrols/{id}/entrances/{entranceId}/actions` |
| FORPOST-дверь | `POST /rest/v1/forpost/cameras/{cameraId}/devices/{externalDeviceId}/open` |

Обычная команда открытия имеет тело `{"name":"accessControlOpen"}`. Для FORPOST передаётся `X-Payment-PlaceId`. Учитываются `allowOpen`, `allowVideo`, `allowCallMobile`, тип устройства, openMethod, externalCameraId, externalDeviceId и список entrances.


## 5. События

APK 9.11.0 содержит `POST /rest/v1/events/search?page={page}&sort=...`: фильтры placeIds, occurredAtFrom, occurredAtTo, sources, eventTypes; ответ content и last. ПК-клиент использует этот поиск и поддерживает следующую страницу. Для совместимости с сервером существует `GET /rest/v1/places/{placeId}/events?allowExtentedActions=true`.


Двойной клик по событию в GUI выбирает его время в архиве выбранного домофона. Если для события нет доступной записи или выбран неверный домофон, сервер может отклонить просмотр. Наличие записи определяется сервером, а не только наличием события.

## 6. Камера и архив

Перед получением видеоссылки обновляется медиасессия:

```text
PUT /api/mh-camera-personal/mobile/v1/video/refresh-user-session?externalCameraId=...
```

Прямая камера (версия ПК 0.1.1, непрерывный HTTP-FLV):

```text
GET /rest/v1/forpost/cameras/{cameraId}/video?LightStream=0
```

Архив добавляет `TS` (Unix seconds) и `TZ` (смещение камеры в секундах). APK также передаёт Speed при выборе скорости. Ответ — `data.URL`, а при ошибке — Error/ErrorCode. Это временная подписанная ссылка на сервер видеонаблюдения. ПК-клиент не публикует её и обновляет прямой эфир при длительном просмотре.

История камеры:

```text
GET /rest/v2/forpost/cameras/{cameraId}/events?LowerDate=...&UpperDate=...&Count=200&orderByTime=DESC
```

В проверенном ответе поля `ID`, `Time`, `Duration`, `isAvailable`, `Message`, `CameraID` и дополнительные идентификаторы. Первый запрос на сутки вернул 200 записей, следующий — ещё 54 без повторных ID. «Загрузить ещё» запрашивает более раннюю часть того же дня с новой UpperDate и подавляет повторные ID. Время архива можно выбрать вручную независимо от этого списка.

Снимки SIP-домофона: `GET /rest/v1/places/{placeId}/accesscontrols/{id}/videosnapshots`. FORPOST: `/rest/v1/forpost/cameras/{cameraId}/snapshots`.

Проверено: серверные ответы прямого эфира и архива HTTP 200; LibVLC декодировал 308 видеокадров / 497 аудиоблоков прямого эфира и 346 / 555 архива за проверочный интервал. Готовый EXE в отдельном запуске декодировал 476 видеокадров и 755 аудиоблоков; запуск и выход завершились с кодом 0. JPEG-снимок успешно получен. Это подтверждение воспроизведения, а не только выдачи ссылки.

## 7. WSS / STOMP

В декомпилированном `defpackage/v1b.java` построение адреса видно как `wss://` + сохранённый stomp_domain + `/events`. Подписка — `/user/queue`; библиотека Android — Krossbow. Внешний объект — StompEventRaw(type, payload), причём payload может быть JSON-строкой внутри JSON. `placeEvent` разбирается в поля placeId, eventTypeName, value, source.id. Другие типы включают availableFeatures, AccessControlActivationEvent и AccessKeysUpdateEvent.

Авторизация передаётся в заголовке WebSocket handshake. После CONNECT следует SUBSCRIBE. Для запроса возможностей используется `GET /rest/v1/stomp/available-features`.

ПК-клиент реализует TLS WebSocket, STOMP CONNECT/SUBSCRIBE, фрагментированные сообщения, переподключение и обновление авторизации. В текущем аккаунте получено подтверждение CONNECTED. Поддержан разбор известных входящих call-событий, если сервер их отправит.

Однако исследование `gentslava/elektronny-gorod` наблюдало отсутствие звонков в этом WSS-канале при реально подключённом STOMP. APK отдельно обрабатывает FirebasePushService и `PushType=CALL_INCOMING`. Поэтому обещать приём звонков только через WSS нельзя: основной путь реализован через FCM и SIP.

## 8. FCM и цикл звонка

Публичная Firebase-конфигурация именно этого APK:

- проект `myhome-3b9cc`;
- sender `1024394110794`;
- Android app ID `1:1024394110794:android:aeead8f4e9e57a23`.

Они отличаются от конфигурации «Электронного города» в подсказке. Дополнительно APK `t41` выбирает `r1d.ERTH`: для REST-регистрации установки это `appId=4`, для SIP Contact — `app-id=erth`. В сторонних NTK-интеграциях встречаются `appId=2` и `com.novotelecom.domophone`; их нельзя без изменения переносить на этот APK.

ПК-приёмник реализует Google checkin, register3, Firebase Installations, Web Push registration и TLS MCS на `mtalk.google.com:5228`. Ключи Web Push P-256 и auth secret генерируются локально и сохраняются с DPAPI. Сообщения расшифровываются как aesgcm или aes128gcm; поддерживаются подтверждения persistent ID, heartbeat и переподключение. Повторная доставка подавляется.

FCM-токен привязывается к аккаунту через device-installations и `/rest/v1/subscriberNotifications`. На актуальном Google register3 потребовались параметры gmsv/scope/X-scope и повтор попытки при PHONE_REGISTRATION_ERROR. После этого регистрация и MCS LOGIN подтвердились. Полный GUI подтвердил «FCM: подключено» после привязки к оператору.

Типичный push содержит PlaceId, AccessControlId, GateName, Call-ID, CallStarted, CallInvalidated. `CALL_INCOMING` открывает окно и запускает короткую SIP-регистрацию конкретного домофона. Просроченное уведомление не открывает окно. `CALL_END...` завершает соответствующий вызов.

## 9. SIP и звук

После получения SIP login/password/realm клиент использует UDP/5060 и Digest MD5. Contact включает публичный/локальный адрес, transport=udp, а для push-режима — app-id, pn-type, pn-tok и Call-Id. SIP-порт стабилен для домофона, чтобы при перезапусках не создавать новые временные контакты.

Входящий INVITE получает 100 Trying и 180 Ringing. По кнопке ответа отправляется 200 OK с SDP; ожидается ACK. Ответ копирует все Via и Record-Route и корректно поддерживает компактные SIP-заголовки. Реализованы ретрансляция 200 OK до ACK, CANCEL/487, BYE, OPTIONS, NOTIFY и освобождение ресурсов.

Звук — G.711 PCMU или PCMA, 8 кГц, mono, 20 мс. NAudio получает PCM с выбранного микрофона и выводит декодированный PCM на выбранные динамики. RTP передаётся с исходного UDP-сокета в обе стороны; отправка пакетов начинается сразу, что позволяет серверному RTP-latching установить обратный NAT-маршрут. STUN используется для публичного адреса. Видео человека в окне звонка идёт через отдельный URL камеры, а не через SIP SDP.

Текущая реализация не добавляет WebRTC браузер или Android Linphone. Она поддерживает обычный RTP/AVP с G.711; SRTP/DTLS, Opus и альтернативные SIP-транспорты не реализованы. При этом исследованный APK в `iva` явно устанавливает `MediaEncryption.None`. Для имеющегося домофона проверена SIP-регистрация. Реальный SDP вызова и двусторонний звук с физической панелью ещё требуют контрольного звонка.

## 10. Архитектура ПК-версии

| Компонент | Назначение |
|---|---|
| `MainWindow` | Вход, выбор адреса/двери, камера, история, архив, устройства звука |
| `CallWindow` | Отдельное окно вызова, ответ, открытие, mute, завершение |
| `DomruApi` | HttpClient, авторизация, refresh, адреса, события, камера и дверь |
| `StompClient` | WSS/STOMP и события аккаунта |
| `FcmReceiver` | Google registration, TLS MCS, push-приём и подтверждения |
| `WebPushCrypto` / `Proto` | Расшифровка push и protobuf wire format |
| `SipClient` / `SipProtocol` | SIP-регистрация, диалог и жизненный цикл вызова |
| `RtpAudio` | G.711, микрофон, динамики, RTP, STUN |
| `VideoPlayer` | LibVLC для live и архива |
| `SessionStore` | DPAPI-хранилище токенов и installationId |

Приложение использует .NET 10, WPF, LibVLCSharp и NAudio. Основной GUI не требует браузера или отдельного локального HTTP-сервера. Кнопка открытия заблокирована на время своего запроса и учитывает allowOpen. Микрофон включается только после пользовательского ответа. При закрытии окна звонка освобождаются медиаресурсы.

## 12. Использованные открытые материалы

- [lolmaxlevel/domru-ha](https://github.com/lolmaxlevel/domru-ha) — авторизация, медиасессии и параметры SIP/FCM.
- [z81/domru](https://github.com/z81/domru) — архив, HLS, события и endpoints двери.
- [gentslava/elektronny-gorod](https://github.com/gentslava/elektronny-gorod) — различие FCM/STOMP, входящие SIP-звонки и RTP-latching.
- [sdb9696/firebase-messaging](https://github.com/sdb9696/firebase-messaging) и [beeper/push-receiver](https://github.com/beeper/push-receiver) — открытые описания checkin/MCS и регистрации Web Push.
- [JADX](https://github.com/skylot/jadx/releases/tag/v1.5.6) — декомпиляция APK.




## 13. Исправление задержки прямого эфира в версии 0.1.1



## 14. Режим минимальной задержки в 0.1.2

APK u5d вызывает live API с LightStream=0 и Format=H264. В 0.1.2 выбран такой же запрос. Сервер отдаёт FLV. RTSP-запрос при текущей проверке вернул HTTP 500, RTMP — HTTP 400; доступный более быстрый протокол этим не подтверждён. Пакеты FLV за 5,5 секунды наблюдения продвинулись по видеовремени на 5,4 секунды, без большого начального сброса накопленных кадров. В плеере добавлен режим с clock-synchro=0 и буфером 50 мс, доступен возврат к 150 мс и обычной синхронизации. Текущая камера в готовом EXE декодирует видео и звук; абсолютная задержка относительно движения у панели не измерена. Первый декодированный кадр диагностического плеера появился примерно через 2,4 секунды; это время запуска, а не полная задержка видео. Реальная нулевая задержка не обещается.


## 15. Архив четырёх дней в 0.1.3

На основе версии 0.1.2 реализована новая красно-белая тема. Версия 0.2.0 и её макеты удалены. Архив объединяет события выбранной двери из постраничного events/search и события/записи её камеры из forpost. Период — сегодня и три предыдущих календарных дня. Загружаются все страницы; повторные ID удаляются, прочие двери и события счёта исключаются. Переход к другому разделу отменяет устаревшую загрузку. В реальном аккаунте готовый EXE загрузил 786 записей в четыре группы дат и воспроизвёл запись с видео и звуком. Набор из 33 проверок включает границы периода, фильтрацию двери, устранение дублей, отмену загрузки и получение записей после первых 200.


## 16. Встроенное видео и переключение архива в 0.1.4

VLC получает HWND собственного дочернего VideoSurface до начала воспроизведения. При отсутствии HWND запуск запрещён, чтобы VLC не создавал автономное окно. Размер области вычисляется по пропорциям декодированного потока и доступному месту. Полноэкранный режим разворачивает тот же host внутри того же окна; native fullscreen VLC отключён. Двойной клик и Esc обрабатываются собственным контролом. Stop/Play/Dispose сериализованы и выполняются в рабочих задачах, а UI остаётся свободным для обработки оконных сообщений. Реальная проверка включает три цикла live → архив → live, привязку HWND, пропорции и heartbeat UI.

## 17. Архив по дням и сервисы аккаунта в 0.1.7

Архив запрашивает один выбранный календарный день («Сегодня», «Вчера», «Позавчера»). Нижняя граница — локальная полночь, верхняя — текущий момент для сегодня либо 23:59:59.999 для прошедшего дня. Сохраняются полная пагинация и отмена устаревших запросов. Кольцо загрузки реализовано векторной дугой с RotateTransform; Play/Pause/Stop — WPF Path/Rectangle без зависимости от иконочного шрифта.

В APK найдены интерфейсы `r7` (ключи), `pn4` (финансы), `pp8` (платёжная информация). Используются GET-запросы с существующей авторизацией:

- `api/mh-access-key/mobile/v1/rest/v1/access-keys?placeId=…`: массив объектов с `id`, `placeId`, `accessKey`. В `accessKey` находятся `accessKeyCode`, `name`, `state`, `accessKeyBind`, `notificationStatus`, `accessControls`.
- `api/mh-payment/mobile/v1/finance?placeId=…`: `balance`, `amountSum`, `targetDate`, `blockType`, `blocked`, `paymentLink`, `company`.
- `api/mh-payment/mobile/v1/payment/info?placeId=…`: `features`, `balance`, `paymentLink`, `managementCompany`.
- `rest/v1/subscribers/profiles`: `subscriber` с `name`, `nickName`, `accountId`.

Разделы ключей и аккаунта отображают серверные данные, не изменяя ключи, тарифы или состояние платежей. Кнопка оплаты адреса открывает `paymentLink`, возвращённый API, в браузере. Предусмотрены также официальные сайты Дом.ру и кабинеты Цифрал-Сервис. Ссылки Цифрал-Сервис сверены с https://cyfral-group.ru/ (https://lk.cyfral-group.ru/ и https://pay.cyfral-group.ru/); Дом.ру публикует кабинет и оплату на своём официальном сайте. Ссылка допускает только HTTPS. Пароль и Bearer-токен приложения браузеру не передаются. В диагностике сохраняются только количество ключей и факт доступности финансов/профиля/ссылки, без кодов ключей, договора, финансовых сумм или URL с параметрами. Реальное проведение платежа не входит в автоматические проверки.
