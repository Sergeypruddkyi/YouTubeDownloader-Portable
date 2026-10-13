# REAL_PLAYER_PLAN.md — Real Video Preview для Cutter

## 1. Назначение

Этот документ является рабочим master-plan для замены текущего центрального Preview в Cutter проекта:

`E:\Projects\YouTubeDownloader-Net10`

Цель — заменить текущий FFmpeg-based frame preview на настоящий embedded video player, чтобы центральное окно Cutter вело себя как реальный видеоплеер при воспроизведении и scrubbing, в том числе на 4K AV1.

План основан на последнем техническом анализе текущего Cutter и архитектурном плане LibVLC, но адаптирован под реальный режим работы проекта: небольшие задачи, модели уровня GLM 5.3 / DeepSeek 4.1, обязательные checkpoints и регулярный проверяемый результат.

---

## 2. Выбранное направление

Основное направление:

- `LibVLCSharp`
- `LibVLCSharp.WinForms`
- `VideoLAN.LibVLC.Windows`

Используем embedded LibVLC runtime внутри portable-приложения.

Не предполагаем, что VLC отдельно установлен в Windows.

Точные версии NuGet/runtime должны быть проверены непосредственно перед SPIKE. Версии из старых документов нельзя копировать вслепую.

Если актуальная совместимая комбинация пакетов не подтверждается — остановиться и сообщить проблему, не угадывать версии.

---

## 3. Почему это делается

Текущий Preview фактически работает как:

```text
Timeline seek
    ↓
FFmpeg process
    ↓
seek/decode одного кадра
    ↓
raw BGRA
    ↓
Bitmap
    ↓
PictureBox
```

Для 4K кадр BGRA очень большой, а новый seek может создавать отдельный FFmpeg decode path.

В результате при scrubbing 4K AV1 пользователь может долго видеть промежуточное/размытое изображение.

Цель нового решения:

```text
Timeline
    ↓
real player seek
    ↓
persistent media player
    ↓
VideoView
```

При этом Timeline остаётся визуальной картой всего видео.

---

## 4. Что НЕ меняем

В процессе внедрения real player не переделывать без отдельной задачи:

- `FilmstripTimeline`
- thumbnail pipeline
- Cut
- Undo
- HybridCut
- `TrimJob`
- Save Video
- Save Frame
- `MediaProbe`
- Open Video
- Open Image
- существующую систему папок/settings
- существующий SelfTest
- release-структуру `dist`

Особенно важно:

### Save Frame

Остаётся извлечение точного кадра исходного видео через FFmpeg.

Не заменять Save Frame на screenshot `VideoView`.

### Save Video

Остаётся существующий HybridCut pipeline.

Новый player не должен знать детали монтажа/кодирования результата.

---

## 5. Архитектура

Целевая схема:

```text
EditorForm
    │
    ├── VideoView
    │       │
    │       └── LibVlcPlayer
    │
    └── FilmstripTimeline
            │
            └── Position / seek
```

На следующем архитектурном этапе допускается:

```text
IVideoPlayer
    ↑
LibVlcPlayer
```

Но abstraction не создаётся заранее только ради архитектурной полноты.

Сначала должен быть доказан рабочий LibVLC SPIKE.

---

## 6. Главный принцип внедрения

Не пытаться сделать весь переход одним большим task.

Работа выполняется маленькими последовательными задачами.

Каждая задача:

1. имеет одну конкретную цель;
2. имеет ограниченный набор изменяемых файлов;
3. имеет явный PASS/FAIL;
4. заканчивается checkpoint;
5. не запускает следующую задачу автоматически.

Модель уровня GLM 5.3 / DeepSeek 4.1 не должна самостоятельно выполнять весь roadmap.

Master-plan является картой проекта.

Конкретный TASK выдаётся модели отдельно.

---

# PHASE 0 — Baseline

## TASK 0 — зафиксировать текущую архитектуру Preview

Только read-only анализ.

Найти и зафиксировать:

- `_preview`
- `_filmstrip`
- `_decoder`
- `_scrubber`
- Timeline seek flow
- `StartAt`
- `StepForward`
- обновление preview
- Open Video
- Close
- Dispose

Не менять код.

### PASS

Получен короткий список фактических точек текущего pipeline.

---

# PHASE 1 — LibVLC SPIKE

## TASK 1 — проверить LibVLC до интеграции

Создать минимальный изолированный WinForms SPIKE.

Проверить:

- актуальные совместимые версии пакетов;
- LibVLC creation;
- MediaPlayer;
- VideoView;
- обычное видео;
- 4K AV1;
- play;
- pause;
- seek;
- повторный seek;
- close;
- reopen;
- native runtime.

EditorForm не менять.

Timeline не менять.

Старый preview не менять.

### STOP / GO #1

После TASK 1 обязательно остановиться.

Дальше идти только после проверки результата.

---

# PHASE 2 — Player abstraction

## TASK 2

Создать минимальный `IVideoPlayer`, если SPIKE подтвердил жизнеспособность LibVLC.

Интерфейс должен быть маленьким.

Предполагаемые операции:

```text
Open(path)
Play()
Pause()
Seek(seconds)
Stop()
Close()

Position
Duration
IsPlaying

PositionChanged
```

Не создавать большой media framework.

### PASS

Интерфейс существует и имеет только необходимые для Cutter операции.

---

# PHASE 3 — LibVlcPlayer

## TASK 3

Создать production-реализацию:

```text
LibVlcPlayer
```

Она инкапсулирует LibVLC API.

EditorForm не должен зависеть от конкретных классов LibVLC там, где достаточно `IVideoPlayer`.

Не добавлять лишнюю многопоточную инфраструктуру.

Не строить собственный scheduler/decoder.

---

# PHASE 4 — VideoView

## TASK 4

Добавить `VideoView` в центральную область Cutter.

Пока не подключать Timeline.

Пока не удалять старый `_preview`.

Цель:

```text
EditorForm
    ↓
VideoView
    ↓
LibVlcPlayer
```

### STOP / GO #2

Проверить:

- приложение запускается;
- VideoView отображается;
- видео открывается;
- воспроизведение работает;
- 4K AV1 открывается;
- закрытие/повторное открытие работают.

---

# PHASE 5 — Open Video

## TASK 5

Подключить существующий `Open Video...` к LibVlcPlayer.

Не менять логику выбора файла и папок без необходимости.

При открытии:

```text
Open Video
    ↓
MediaProbe
    ↓
LibVlcPlayer.Open(path)
```

Старый preview pipeline пока сохраняется как fallback/резерв.

---

# PHASE 6 — Timeline → Player

## TASK 6

Подключить существующий Timeline к player.

Схема:

```text
FilmstripTimeline.Position
        ↓
player.Seek(position)
```

Использовать принцип latest-wins.

При быстром scrubbing старые цели не должны образовывать очередь.

Например:

```text
10s
12s
15s
20s
25s
30s
```

не должны обязательно обрабатываться последовательно.

Player должен стремиться к последней актуальной позиции.

Существующий throttle можно использовать как стартовую точку, но не считать 55 ms неизменяемым требованием.

---

# PHASE 7 — Player → Timeline

## TASK 7

Подключить позицию player обратно к Timeline:

```text
LibVLC PositionChanged
        ↓
FilmstripTimeline.Position
```

Playhead должен двигаться при обычном воспроизведении.

Обязательно исключить feedback loop:

```text
Timeline → Seek → PositionChanged → Timeline → Seek → ...
```

Не создавать для этого сложную event architecture.

---

# PHASE 8 — Playback

## TASK 8

Подключить нормальные:

- Play
- Pause

и синхронизацию playhead.

Цель — центральное окно Cutter теперь является настоящим видеоплеером, а Timeline продолжает выполнять роль визуальной карты видео.

---

# PHASE 9 — Frame stepping

## TASK 9

Не ломать существующий рабочий Prev/Next Frame.

Текущая логика использует:

```text
_frameDuration = 1 / VideoFps
```

и уже была проверена вручную.

Сначала сохранить её.

Отдельно исследовать, действительно ли LibVLC `NextFrame` даёт нужное поведение.

Не заменять рабочую frame-step реализацию на:

```text
Seek(position - frameDuration)
```

без проверки.

Особенно учитывать возможный VFR.

---

# PHASE 10 — Save Frame / Save Video verification

## TASK 10

Не переписывать эти механизмы.

Проверить, что после player integration:

### Save Frame

```text
Timeline.Position
    ↓
FFmpeg
    ↓
original source
    ↓
PNG
```

### Save Video

```text
Timeline cut range
    ↓
existing HybridCut
    ↓
output video
```

Оба должны продолжать работать независимо от VideoView.

---

# PHASE 11 — 4K AV1 acceptance

## TASK 11

Основной реальный acceptance.

Проверить на реальном 4K AV1:

```text
3840x2160
AV1
```

Проверить:

- Open
- first frame
- Play
- Pause
- seek вперёд
- seek назад
- быстрый scrubbing
- обратный scrubbing
- повторный seek
- несколько разных позиций
- Close
- Reopen

Особенно сравнить с текущей проблемой:

> исчезла ли длительная ситуация, когда центральный Preview после перемещения playhead остаётся размытым/низкокачественным десятки секунд.

Не объявлять успехом просто наличие VideoView.

---

# PHASE 12 — удалить старый Preview pipeline

Только после успешного acceptance.

Тогда можно отдельно рассмотреть удаление:

- `PreviewScrubber`
- старого proxy pipeline
- ненужного FFmpeg preview decode path
- связанного старого preview-кода

Удаление выполняется отдельной задачей.

До этого старый pipeline не уничтожать.

---

# PHASE 13 — Portable publish

После стабилизации:

```text
dotnet publish -c Release -r win-x64 --self-contained true
```

Канонический output:

```text
E:\Projects\YouTubeDownloader-Net10\dist\YouTubeDownloader
```

Именно этот output используется для:

- ручного запуска;
- UI Automation;
- визуальной проверки;
- acceptance.

Не считать `bin\...\publish` пользовательской production-сборкой.

После добавления LibVLC проверить фактический состав и размер native runtime.

Не предполагать заранее размер `dist`.

---

# 7. Performance measurement

Не устанавливать заранее жёсткие PASS-цифры вроде:

- `<200 ms`
- `<300 ms`
- CPU `<X%`
- RAM `<X MB`

пока они не измерены.

Сначала получить baseline текущего Preview.

Затем измерить LibVLC.

Минимально:

```text
mouse release
    ↓
visible new frame
```

для:

- 1080p;
- 4K AV1.

Цель — сравнить фактическое поведение, а не подтвердить заранее придуманный результат.

---

# 8. Fallback

До полного acceptance старый preview pipeline сохраняется.

Не удалять его только потому, что LibVLC успешно собрался.

Если LibVLC integration ломается, должна оставаться возможность вернуться к старому Preview без восстановления проекта из Git.

---

# 9. Работа с моделями

Этот план не передаётся модели как одна большая задача.

Модели уровня GLM 5.3 / DeepSeek 4.1 получают по одному TASK.

Запрещено:

- самостоятельно выполнять следующие TASK;
- делать большой рефакторинг;
- менять архитектуру вне текущего TASK;
- создавать «улучшения на будущее»;
- удалять старый pipeline до acceptance;
- самостоятельно создавать commit.

После каждого TASK модель сообщает:

```text
TASK
Изменённые файлы
Build
Tests
Manual verification
PASS / FAIL
Что осталось
```

и останавливается.

---

# 10. Checkpoint

После существенного блока:

```text
изменение
→ build
→ publish при необходимости
→ запуск canonical dist
→ реальная проверка
→ отчёт
→ STOP
```

Не считать:

- успешный build;
- успешные unit tests;
- наличие новых классов

достаточным пользовательским результатом.

Приоритет:

```text
код
→ сборка
→ запуск
→ реальный результат
→ ручная проверка
→ следующий шаг
```

---

# 11. Git

Во время выполнения отдельных TASK:

- не делать commit без явного указания пользователя;
- не делать push;
- не переписывать историю;
- не использовать destructive Git commands;
- не делать `reset`, `clean`, `stash`, `rebase` и аналогичные операции без явного разрешения пользователя.

После завершения серии TASK пользователь отдельно решает, что фиксировать в Git.

---

# 12. Критерий завершения проекта

Real Player считается внедрённым только когда одновременно выполнено:

1. LibVLC работает в portable build.
2. 4K AV1 нормально открывается.
3. Центральный Preview является настоящим VideoView/player.
4. Timeline управляет seek.
5. Playhead синхронизирован с playback.
6. Быстрый scrubbing не создаёт старую очередь FFmpeg frame-decode запросов.
7. Save Frame продолжает извлекать оригинальный кадр через FFmpeg.
8. Save Video продолжает работать через существующий HybridCut.
9. Cut/Undo продолжают работать.
10. Open Video/Open Image/Close продолжают работать.
11. Production `dist\YouTubeDownloader` запускается и проверен.
12. Только после этого старый PreviewScrubber/preview pipeline может быть удалён отдельным task.

---

## 13. Текущий статус

На момент создания этого документа:

```text
PHASE 0 — не выполнен
TASK 0 — не выполнен
TASK 1 — следующий
```

Следующий рабочий шаг:

**TASK 1 — LibVLC SPIKE.**

Не переходить к TASK 2 до проверки результата TASK 1.
