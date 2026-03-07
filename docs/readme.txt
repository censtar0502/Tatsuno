Итог: сейчас проект — это рабочий C#-прототип опроса Tatsuno по COM, с декодером кадров и простым WPF-экраном. Для боевой версии под вашу задачу критично вынести link-layer/FSM/transaction scheduler в C++ и оставить WPF только как UI.

Сразу честно: я сделал статический разбор исходников и эталонного лога `docs/tatsuno.txt`. Полную сборку WPF/MSVC здесь не подтверждаю, потому что окружение не Windows.

Что уже есть по факту.

Проект разбит логично:
`Tatsuno.Model` — модели статуса,
`Tatsuno.Protocol` — кодек/декодер/парсер,
`Tatsuno.Transport.Serial` — работа с COM,
`Tatsuno.WpfApp` — UI и poll loop,
`Tatsuno.Native` — пока пустой C++ DLL.

По протоколу у вас уже правильно схвачена основа: polling/selecting, control bytes, текстовые команды и ответные кадры действительно строятся вокруг `EOT / ENQ / STX / ETX / ACK0 / ACK1 / NAK`, а команды консоли включают `10/11/12/13/14/15/20/21`; это видно в приложенном PDF на страницах 5-22. 

По эталонному логу `docs/tatsuno.txt` видно следующее:
`<EOT>@Q<ENQ>` — обычный опрос;
при отсутствии данных приходит `<EOT>`;
при наличии данных приходит кадр `@Q...`, затем мастер отвечает `<DLE>1`;
для action/select используется цепочка:
`<EOT>@A<ENQ>` -> RX `<DLE>0` -> TX `<STX>@A20<ETX><BCC>` -> RX `<DLE>1` -> затем снова polling.
То есть link-layer у Tatsuno уже явно двухрежимный: poll и select, и их нельзя смешивать в одном бесконтрольном цикле.

Теперь полный разбор по проектам и файлам.

`Tatsuno.Model`

`Class1.cs` — пустой мусорный файл, удалить.
`Enums.cs` — enum `PostOperationalState` есть, но он нигде не используется как реальная FSM.
`PostStatus.cs` — модель слишком бедная для реального протокола. Сейчас в ней только:
`Address`, `Code`, `VolumeLiters`, `PricePerLiter`, `Amount`, `Nozzle`, `Timestamp`, `RawPayload`.
Для боевого ядра этого недостаточно. Не хватает как минимум:
`Condition`, `Product`, `UnitPriceFlag`, `IndicationType`, `RawNumericFields`, `Totals`, `ErrorCode`, `SwitchStatus`, `DecimalsConfig`.

`Tatsuno.Protocol`

`Class1.cs` — пустой, удалить.

`TatsunoControlBytes.cs` — нормальный минимальный набор control bytes. База правильная.

`TatsunoCodec.cs`
Сильные стороны:

* `BuildPollRequest()` = `<EOT>@Q<ENQ>` — совпадает с логом.
* `BuildActionHandshake()` = `<EOT>@A<ENQ>` — совпадает с логом.
* `BuildFrame()` и `ComputeBcc()` по сути верные: XOR по ASCII payload + `ETX`.
  Проверка по вашему логу подтверждает:
  `@A20` даёт BCC `0x00`,
  `@Q61100037221250004650112` даёт `0x23` (`#`),
  `@Q651...` даёт `0x0E` (`SO`).
  То есть BCC сейчас реализован правильно.

Проблема:

* кодек умеет только generic frame builder. Нет builders для прикладных команд:
  `AuthorizeAmount`, `AuthorizeVolume`, `CancelAuthorize`, `PumpLock`, `ReleaseLock`, `RequestStatus`, `RequestTotals`, `SetPrice`.
  Без этого транзакции делать нечем.

`TatsunoStreamDecoder.cs`
Сильные стороны:

* умеет выделять `EOT`, `DLE0`, `DLE1`, `STX...ETX+BCC`.
* для текущего лога этого хватает.

Проблемы:

* нет inter-byte timeout / frame timeout. Если кадр оборвётся после `STX`, декодер останется в `InFrame`.
* не обрабатываются `NAK`, `ACK0`, `ACK1` как отдельные состояния протокола верхнего уровня.
* нет resync logic, если пришёл мусор или новый `STX` внутри битого кадра.
  Для прототипа терпимо, для боевой линии нет.

`TatsunoQueryParser.cs`
Это сейчас самое слабое место protocol layer.
Парсер жёстко предполагает формат:
`@Q6CC + volume(6) + price(6) + amount(5) + nozzle(3)`.
На эталонном lift/lower-логе это даёт визуально похожий nozzle marker (`112/212/312/012` -> `1/2/3/0`), но decimal scaling у вас явно неверный.

Прямой пример:
кадр `@Q61100037221250004650112`
текущий код читает как:
`3.72 L`, `212.500`, `46.50`.
Но эталонный UI на скриншоте показывает:
`3,72`, `12 500`, `46 500`.
Значит текущая формула масштаба для цены и суммы неверна. И это логично: в PDF отдельно указано, что положение десятичной точки для `Volume`, `U.Price`, `Amount` соответствует индикации на колонке, то есть нельзя жёстко зашивать один scale на все поля. 

Ещё проблемы парсера:

* `Q651...` сохраняется только raw, а totals не разбираются.
* нет разбора error/status/switch кадров.
* комментарий про `Q613` как “fuelling in logs” не подтверждён вашим текущим логом. В приложенном `tatsuno.txt` я вижу `Q610`, `Q611`, `Q651`; живого отпуска с ростом объёма/суммы и отдельного `Q613` в этом логе нет.

Вывод по `Tatsuno.Protocol`:
как диагностический прототип слой годится;
как основа для FSM транзакций — нет, его надо переразобрать.

`Tatsuno.Transport.Serial`

`Class1.cs` — пустой, удалить.

`SerialPortSettings.cs` — нормальный простой DTO.

`SerialPortSession.cs`
Плюсы:

* отдельный RX task;
* простой API `Open/Close/Send`;
* для прототипа хватает.

Минусы:

* нет единого TX scheduler/queue. Как только добавятся команды транзакции параллельно с poll loop, записи в порт начнут гоняться друг с другом.
* нет логики direction control для RS-485, если адаптер этого не делает автоматически.
* `ReadTimeoutMs=50`, `WriteTimeoutMs=500` — нормальны как стартовые, но в FSM это должно быть параметризовано.
* нет понятия “сеанс selecting сейчас занят, poll запрещён”.

`Tatsuno.WpfApp`

`Infrastructure/ObservableObject.cs` и `RelayCommand.cs` — нормальные минимальные утилиты.

`ViewModels/LogLine.cs` — норм.

`MainWindow.xaml`
Сейчас это не тот UI, который нужен.
Имеется:

* выбор COM,
* настройки порта,
* количество постов,
* `DataGrid` статусов,
* лог.

Отсутствует всё критичное под вашу задачу:

* вертикальные посты по адресу;
* визуальное выделение активного поста;
* поля preset по сумме/объёму;
* отдельная цена для каждого пистолета;
* выбор режима оплаты/транзакции;
* кнопка запуска транзакции на активный пост;
* отображение текущего хода отпуска в стиле эталонного экрана.

`ViewModels/PostViewModel.cs`
Сейчас это display-model, а не FSM.
Что работает:

* хранит `State`, `LastCode`, `VolumeLiters`, `PricePerLiter`, `Amount`, `ActiveNozzle`;
* умеет ловить переход `0 -> nozzle` и `nozzle -> 0`;
* подкрашивает строку.

Что плохо:

* состояние вычисляется только по `Code`, без link-layer и без transaction context;
* нет пер-пистолетных цен;
* нет preset amount / preset volume;
* нет признака `IsSelected`, `IsActive`, `IsAutoActiveByLift`;
* нет разделения “адрес поста” и “номер пистолета внутри поста”;
* после idle может держать stale значения;
* логика цвета примитивная и не соответствует вашему UI.

`ViewModels/MainViewModel.cs`
Это главный узел, и именно здесь сейчас архитектурный тупик.

Что сделано нормально:

* открытие порта;
* приём байтов;
* декодирование;
* лог в UI;
* автоматический ACK `<DLE>1` на валидный frame.

Критические дефекты:

1. Poll loop бесконечно шлёт `@Q` каждые 250 мс вообще без учёта состояния link-layer.
   Для Tatsuno так нельзя. Во время select/transaction poll должен быть остановлен или управляться FSM, иначе команды будут смешиваться.

2. `StartPolling()` делает `CloseFileLog()`.
   Это баг.
   `Connect()` открывает файл лога через `OpenFileLog()`, а потом `StartPolling()` тут же его закрывает.
   В итоге `LogFilePath` сбрасывается и постоянного file log у вас фактически нет.

3. Нет TX arbitration.
   Сейчас один поток опроса пишет в порт, и будущая команда транзакции тоже будет писать в порт. Это приведёт к гонке.

4. `ApplyPosts()` жёстко зашивает модель:
   “все посты обычно имеют адрес `6`, а mapping делаем по номеру пистолета”.
   Это противоречит вашей новой задаче “вертикально расположить посты соответственно адресу”.
   Значит текущую модель надо ломать.

5. `ApplyPostStatus()` для `status.Nozzle == 0` чистит первый найденный пост с активным nozzle.
   Это ненадёжно и легко очистит не тот пост.

6. Нет понятия активного поста/курсора.
   Есть только строка `LiftedInfo`.

7. Нет transaction pipeline вообще:
   нет очереди команд,
   нет ожидания `DLE0`,
   нет отправки text frame команды,
   нет ожидания `DLE1/NAK/EOT`,
   нет retries.

`Tatsuno.Native`

Здесь по факту ничего нет:
`dllmain.cpp`, `pch.*`, `framework.h`.
Ни одной экспортируемой функции, ни одного класса, ни FSM, ни parser, ни serial state machine.
Плюс WPF проект на него вообще не ссылается.
То есть “работа с C++” ещё даже не начиналась.

Что видно по эталонному логу и что это значит для будущей FSM.

По логу lift/lower у вас наблюдаются как минимум такие реальные события:

* idle poll без данных;
* подъём пистолета с кадром `@Q611...112/212/312`;
* action handshake `@A20` и ответ `@Q651...`;
* опускание пистолета с кадром `@Q610...012`.

То есть минимальная FSM уже должна иметь не меньше двух слоёв.

Первый слой — link-layer FSM:
`Idle`
`SendPoll`
`WaitPollReply`
`PollFrameReceived`
`SendAck1`
`SendSelectEnq`
`WaitDle0`
`SendCommandText`
`WaitDle1OrNak`
`ResumePolling`
`Retry`
`Fault`

Второй слой — post/nozzle FSM:
`Unknown`
`Idle`
`NozzleLifted`
`PricePending`
`PresetPending`
`Authorized`
`Fuelling`
`Finished`
`TotalsRequested`
`Completed`
`Error`

Сейчас у вас ни один из этих слоёв отдельно не выделен. Всё размазано по `MainViewModel` и `PostViewModel`.

Как я бы это строил дальше под ваш проект.

Правильный путь — не переписывать весь UI в C++, а вынести только protocol core в `Tatsuno.Native`.

Архитектура должна быть такой:

`Tatsuno.Native`:

* чистый C++ core;
* parser кадров;
* link-layer FSM;
* post FSM;
* transaction scheduler;
* модель постов/пистолетов;
* экспорт C API для WPF.

`Tatsuno.WpfApp`:

* только UI, binding, команды пользователя;
* P/Invoke в `Tatsuno.Native`;
* отображение snapshot состояния.

Минимальный C API, который нужен:

* `tm_create_controller(...)`
* `tm_destroy_controller(...)`
* `tm_open_port(...)` или оставить COM в C# и передавать RX/TX в native core
* `tm_feed_rx_bytes(...)`
* `tm_tick(...)`
* `tm_select_post(address)`
* `tm_set_nozzle_price(address, nozzle, price_raw, decimals)`
* `tm_start_amount(address, nozzle, amount_raw)`
* `tm_start_volume(address, nozzle, volume_raw)`
* `tm_cancel_transaction(address)`
* `tm_request_totals(address)`
* `tm_get_snapshot(...)`

Что надо изменить в модели данных.

Нужен не `PostStatus`, а минимум два уровня:
`PumpAddressState`
и внутри `NozzleState[]`.

`PumpAddressState`:

* `Address`
* `IsSelectedByCursor`
* `IsAutoActiveByLift`
* `ActiveNozzle`
* `CurrentDisplayAmountRaw`
* `CurrentDisplayVolumeRaw`
* `CurrentDisplayPriceRaw`
* `AmountDecimals`
* `VolumeDecimals`
* `PriceDecimals`
* `LastTextCode`
* `LastCondition`
* `LastError`
* `CommState`
* `TransactionState`

`NozzleState`:

* `NozzleNumber`
* `ProductCode`
* `PriceRaw`
* `PriceDecimals`
* `IsLifted`
* `IsAuthorized`
* `PresetType`
* `PresetRaw`
* `LastTotalsVolumeRaw`
* `LastTotalsAmountRaw`

Что надо сделать в UI по вашей постановке.

Посты должны идти вертикально по адресу, а не по “индексу строки”.
То есть слева/по центру нужен список card-блоков:
`Address 1`, `Address 2`, `Address 3` ...
внутри каждого — свои пистолеты и цена каждого пистолета.

Активный пост определяется так:

* если пользователь передвинул курсор/выделение — активен выбранный пост;
* если на каком-то посту поднят пистолет — этот пост автоматически становится активным;
* если одновременно есть user selection и lifted nozzle, надо задать жёсткий приоритет. Я бы сделал так:
  поднятый пистолет имеет приоритет над ручным выбором, потому что это физически активная колонка.

Команда транзакции отправляется только активному посту и только его активному пистолету.
Это должно проверяться в native FSM до формирования TX.

Поле цены должно быть отдельно на каждый пистолет, не на пост целиком.
Для Tatsuno это логично и соотносится с командами авторизации/price indication из PDF. 

Поля хода отпуска нужно держать как raw + formatted.
То есть в snapshot должны храниться raw integer значения и `decimals`.
UI уже форматирует:
`46500` -> `46 500`,
`372` -> `3,72`,
и т.д.
Именно так вы уйдёте от нынешней ошибки с жёсткими `/100` и `/1000`.

Что я считаю критическим планом работ.

1. Удалить мусорные `Class1.cs`.
2. Зафиксировать новую модель:
   `Address -> Nozzles[] -> Prices/Presets/LiveState`.
3. Переписать parser так, чтобы он возвращал raw fields, а не “сразу литры/суммы/цены”.
4. Вынести link-layer FSM из `MainViewModel`.
5. Ввести единый TX queue/scheduler.
6. Перенести protocol core в `Tatsuno.Native`.
7. Переделать WPF UI под вертикальные посты и активный курсор.
8. После этого уже добавлять реальные transaction commands:
   authorize by amount,
   authorize by volume,
   cancel,
   totals,
   set price.

Самые важные выводы по текущему состоянию.

Первое: код сейчас пригоден только как sniffing/diagnostic prototype.

Второе: BCC и базовый frame transport у вас уже правильные.

Третье: вся логика транзакций сейчас отсутствует.

Четвёртое: основной архитектурный долг — отсутствие FSM и то, что polling никак не координируется с selecting.

Пятое: parser сейчас неправильно масштабирует как минимум цену и сумму.

Шестое: `Tatsuno.Native` пустой, значит следующий реальный шаг — именно проектирование C++ ядра, а не косметические правки WPF.


