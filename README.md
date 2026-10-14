# Task API

Небольшой API задач на .NET 9, ASP.NET Core Minimal API, PostgreSQL, EF Core и RabbitMQ.Client 7.

## Запуск

Нужны .NET 9 SDK и работающий Docker с Linux containers. Из каталога с `TaskApi.sln`:

```powershell
docker compose up -d --wait
dotnet restore TaskApi.sln
dotnet run --project TaskApi.csproj --urls http://localhost:5080
```

При запуске применяется начальная миграция. Для этого пользователь БД должен иметь права на изменение схемы. Автоматическое применение можно отключить настройкой `Database__ApplyMigrations=false` и выполнять миграции отдельно:

```powershell
dotnet tool restore
$env:Database__ApplyMigrations = 'false'
dotnet ef database update --project TaskApi.csproj
```

В отдельном терминале можно выполнить запросы из `TaskApi.http` или воспользоваться PowerShell:

```powershell
$task = Invoke-RestMethod -Method Post -Uri http://localhost:5080/tasks `
  -ContentType 'application/json' -Body '{"title":"Buy milk","priority":"High"}'

Invoke-RestMethod -Method Put -Uri "http://localhost:5080/tasks/$($task.id)/complete"
Invoke-RestMethod -Uri http://localhost:5080/tasks
Invoke-WebRequest -Method Delete -Uri "http://localhost:5080/tasks/$($task.id)"
```

Остановка API — Ctrl+C. Остановка контейнеров — `docker compose down`. Данные PostgreSQL сохраняются в Docker volume.

Без Docker достаточно запустить PostgreSQL и RabbitMQ отдельно и переопределить настройки:

```powershell
$env:ConnectionStrings__Postgres = 'Host=localhost;Database=taskapi;Username=taskapi;Password=taskapi'
$env:RabbitMq__Uri = 'amqp://taskapi:taskapi@localhost:5672/'
dotnet run --project TaskApi.csproj --urls http://localhost:5080
```

Пароли в `appsettings.json` и Compose предназначены для локального запуска.

## API

| Метод | Путь | Успех | Ошибки |
| --- | --- | --- | --- |
| POST | `/tasks` | 201, созданная задача | 400 при некорректном JSON, title или priority |
| GET | `/tasks` | 200, все задачи | 500 при сбое БД |
| PUT | `/tasks/{id}/complete` | 200, завершённая задача | 400 для неверного Guid, 404, 409 |
| DELETE | `/tasks/{id}` | 204 | 400 для неверного Guid, 404 |

`priority` передаётся строкой: `Low`, `Medium`, `High`. По умолчанию — `Medium`. При создании `isCompleted=false`, `completedAt=null`. Пробельный title отклоняется, длина исходной строки ограничена 200 символами, пробелы по краям допустимого значения удаляются. Поля состояния и времени назначает сервер; они не входят в модель запроса.

Повторное завершение возвращает 409, время завершения не меняется, повторного события нет. GET возвращает все задачи без пагинации.

## Конкурентность

`TaskItem.RowVersion` — `uint`, настроенный через `IsRowVersion()`. Npgsql сопоставляет его системному столбцу PostgreSQL `xmin`. Отдельного пользовательского столбца `RowVersion` в таблице нет.

При обновлении EF Core включает исходный `xmin` в условие `WHERE`. Если другой запрос уже изменил строку, обновляется ноль строк и возникает `DbUpdateConcurrencyException`. API возвращает 409. Предварительная проверка `IsCompleted` обрабатывает последовательные повторные вызовы, а `xmin` защищает от гонки между чтением и сохранением.

## RabbitMQ и остановка

Событие отправляется **после успешного `SaveChangesAsync`** в durable topic exchange `task.events` с routing key `task.completed`. JSON содержит `taskId`, `title`, `completedAt`, `priority`; priority сериализуется строкой. Время хранится в UTC с точностью до микросекунд PostgreSQL.

Соединение и канал создаются при первой публикации и переиспользуются. API может стартовать при недоступном RabbitMQ. Автоматическое восстановление клиента отключено: при закрытом канале следующая публикация пересоздаёт соединение. Неотправленное событие автоматически не повторяется.

Публикация асинхронная, без `Task.Run` и fire-and-forget. HTTP-запрос ожидает попытку отправки; отдельный таймаут 3 секунды включает ожидание доступа к каналу, подключение и публикацию. `SemaphoreSlim.WaitAsync` защищает общий канал от одновременных публикаций и не блокирует поток. Эта синхронизация не участвует в защите данных задачи: за неё отвечает `xmin`.

Если публикация бросает исключение, API пишет ошибку и полный JSON события в лог и возвращает успешно завершённую задачу. Отмена HTTP-запроса после сохранения не отменяет попытку отправки: у публикации собственный таймаут.

При остановке новые публикации отклоняются, начатая получает до 3 секунд, затем закрываются и освобождаются канал и соединение. Общий бюджет остановки publisher — 5 секунд. ASP.NET Core управляет Kestrel и временем завершения запросов отдельно.

Publisher confirms не включены. Успешный вызов клиента не доказывает надёжное сохранение сообщения брокером. Между commit в БД и отправкой остаётся окно потери события; persistent-сообщение и durable exchange это окно не закрывают. Если потребуется гарантированная доставка, следующий шаг — transactional outbox, фоновые повторы, confirms и обработка дублей на стороне потребителя.

Приложение объявляет обменник и только публикует. Для сохранения сообщений нужна очередь, заранее связанная с обменником. Для ручной проверки откройте [RabbitMQ Management](http://localhost:15672), войдите как `taskapi` / `taskapi`, создайте topic exchange `task.events` с Durable=true, очередь `task.debug` и binding с ключом `task.completed`. После завершения новой задачи сообщение можно прочитать в очереди через Get messages. Создать очередь и binding нужно **до** завершения задачи.

## Тесты

```powershell
dotnet test TaskApi.sln
```

По умолчанию Testcontainers поднимает отдельный PostgreSQL; требуется работающий Docker. RabbitMQ для тестов не нужен: в интеграционных тестах подменяется `ITaskEventPublisher`, как разрешено в задании. Для существующего PostgreSQL можно задать административное подключение:

```powershell
$env:TASKAPI_TEST_POSTGRES = 'Host=localhost;Database=postgres;Username=taskapi;Password=taskapi'
dotnet test TaskApi.sln
```

Тесты создают БД с уникальным именем `taskapi_test_*` и удаляют её после выполнения; нужны права CREATE DATABASE. Рабочую БД приложения они не очищают.

Основной интеграционный тест создаёт задачу через HTTP, завершает её и проверяет вызов publisher с правильными полями. В момент публикации он отдельно читает БД и проверяет, что завершение уже сохранено. Дополнительные проверки покрывают валидацию, удаление, повторное завершение, ошибку publisher и одновременное завершение. В тесте гонки оба запроса удерживаются перед сохранением, чтобы проверить конфликт одной версии строки. Отдельные тесты RabbitMqService проверяют JSON, переиспользование соединения и закрытие транспорта после активной публикации через подмены интерфейсов клиента.

Локальная проверка: .NET SDK 9.0.319, PostgreSQL 17.11, 15 тестов пройдено, сборка без предупреждений. Дополнительно API запущен через Kestrel с настоящим клиентом RabbitMQ: при недоступном брокере задача завершилась, событие попало в лог, повторное завершение вернуло 409. Доставка в живой RabbitMQ и запуск через Compose в этой среде не проверялись; тесты транспорта используют подмены. Для проверки живого брокера приведён сценарий выше.

## Файлы

- `Program.cs` — настройки, DI, маршруты и применение миграций.
- `TaskEndpoints.cs` — четыре обработчика HTTP.
- `TaskItem.cs`, `TaskDbContext.cs`, `Migrations/` — сущность, mapping и схема БД.
- `Contracts.cs` — модели запросов, ответов и события, интерфейс publisher.
- `RabbitMqService.cs` — подключение, асинхронная публикация и остановка.
- `Tests/` — интеграционные тесты API и проверки publisher.

Пагинация, consumer, аутентификация и Swagger не добавлены: они не требуются в задании. Для небольшого API отдельные слои, repository и mediator не понадобились.

Справка: [Npgsql о xmin](https://www.npgsql.org/efcore/modeling/concurrency.html), [RabbitMQ .NET API](https://www.rabbitmq.com/client-libraries/dotnet-api-guide).
