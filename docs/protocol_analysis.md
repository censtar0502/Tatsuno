# Анализ проблем текущей реализации Tatsuno Protocol

## Критические проблемы

### 1. Неправильное масштабирование числовых значений

**Текущая реализация (TatsunoQueryParser.cs):**
```csharp
// Строки 52-54
VolumeLiters = volCentiliters / 100.0,           // 372 → 3.72 L ✓ (вроде верно)
PricePerLiter = priceMilli / 1000m,              // 212500 → 212.500 ✗ (НЕВЕРНО!)
Amount = amountCents / 100m                       // 4650 → 46.50 ✗ (НЕВЕРНО!)
```

**Проблема:**
- Для кадра `@Q61100037221250004650112`:
  - Текущий код: Volume=3.72L, Price=212.500, Amount=46.50
  - Ожидаемое значение из эталонного UI: Volume=3.72L, Price=12.500, Amount=46500
  
**Причина:**
Парсер предполагает фиксированное масштабирование, но в реальности:
- Положение десятичной точки зависит от настроек колонки
- Нужно хранить **сырые значения** и **decimals** отдельно

### 2. Бедная модель данных (PostStatus.cs)

**Отсутствующие поля:**
- `Condition` - статус состояния колонки
- `Product` - тип продукта
- `UnitPriceFlag` - флаг формата цены
- `IndicationType` - тип индикации
- `RawNumericFields` - сырые числовые поля
- `Totals` - данные о суммах (для Q651)
- `ErrorCode` - коды ошибок
- `SwitchStatus` - статус переключателей
- `DecimalsConfig` - конфигурация десятичных знаков

### 3. Проблемы с обработкой разных типов кадров

**Поддерживаются:**
- Q610, Q611 - частично (только volume/price/amount)
- Q651 - только raw payload (не разбирается!)

**Не поддерживаются:**
- Q612, Q613, Q614 - статусы во время отпуска
- Q615 - специальные статусы
- Кадр с ошибками
- Кадр с totals/sums

### 4. Проблема в MainViewModel.cs

**Баг с логированием (строка 276):**
```csharp
private void StartPolling()
{
    if (_session is null) return;
    
    StopPolling();
    _pollCts = new CancellationTokenSource();
    
    _ = Task.Run(async () =>
    {
        byte[] poll = TatsunoCodec.BuildPollRequest();
        
        while (!_pollCts!.IsCancellationRequested)
        {
            try
            {
                _session.Send(poll);
                Application.Current.Dispatcher.Invoke(() => AddLog("TX", RenderBytes(poll)));
                
                await Task.Delay(250, _pollCts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Application.Current.Dispatcher.Invoke(() => AddLog("SYS", $"Poll error: {ex.Message}"));
                await Task.Delay(500);
            }
        }
    }, _pollCts.Token);
}
```

**Проблема:** В Connect() вызывается OpenFileLog(), но этот метод нигде не вызывается явно.
Нужно проверить, что файловое логирование работает корректно.

### 5. Отсутствие FSM (State Machine)

**Критическая архитектурная проблема:**
- Poll loop работает постоянно без учета состояния link-layer
- Нет координации между poll и select режимами
- Команды транзакций будут смешиваться с poll запросами

**Необходимые состояния Link-Layer FSM:**
1. Idle
2. SendPoll
3. WaitPollReply
4. SendAck1
5. SendSelectEnq
6. WaitDle0
7. SendCommandText
8. WaitDle1OrNak
9. ResumePolling
10. Retry
11. Fault

### 6. Проблема с mapping постов

**Текущее решение (MainViewModel.ApplyPostStatus):**
```csharp
// Все посты имеют адрес "6"
// Mapping по номеру пистолета: Nozzle 1→Post 1, Nozzle 2→Post 2
if (Posts.Count > 1 && status.Nozzle >= 1 && status.Nozzle <= Posts.Count)
{
    var post = Posts[status.Nozzle - 1];
    post.ApplyStatus(status);
}
```

**Проблема:**
- Не соответствует новой задаче "вертикально расположить посты соответственно адресу"
- Все посты имеют одинаковый адрес "6", что неверно для multi-address конфигурации

## Рекомендации по рефакторингу

### Приоритет 1: Исправить масштабирование
1. Хранить **сырые integer значения** (например, 46500)
2. Хранить **decimals** отдельно (например, 2 = 2 знака после запятой)
3. Форматировать в UI: `46500` + `2 decimals` → `"46 500"`

### Приоритет 2: Обогатить модель данных
1. Добавить `PumpAddressState` с полями:
   - Address, IsSelectedByCursor, IsAutoActiveByLift
   - Raw values: CurrentDisplayAmountRaw, CurrentDisplayVolumeRaw, CurrentDisplayPriceRaw
   - Decimals: AmountDecimals, VolumeDecimals, PriceDecimals
   - Status: LastTextCode, LastCondition, LastError
   - State: CommState, TransactionState

2. Добавить `NozzleState[]` per address:
   - NozzleNumber, ProductCode
   - PriceRaw, PriceDecimals
   - IsLifted, IsAuthorized
   - PresetType, PresetRaw
   - Totals: LastTotalsVolumeRaw, LastTotalsAmountRaw

### Приоритет 3: Переписать парсер
1. Извлечь все numeric fields как raw integers
2. Извлечь decimals configuration из фрейма
3. Парсить все типы Q6xx кадров
4. Обрабатывать totals frames (Q651)
5. Парсить error/status codes

### Приоритет 4: Реализовать FSM
1. Link-Layer FSM для координации poll/select
2. Post/Nozzle FSM для отслеживания состояний
3. Transaction Scheduler для управления командами

## Пример правильного разбора кадра

**Кадр:** `@Q61100037221250004650112`

**Сырые поля:**
```
Volume:  000372  (raw=372, decimals=2 → 3.72 L)
Price:   212500  (raw=212500, decimals=4 → 21.2500 или decimals=3 → 212.500)
Amount:  04650   (raw=4650, decimals=2 → 46.50)
Nozzle:  112     (raw=112 → nozzle=1)
```

**Проблема:** Без знания конфигурации decimals невозможно определить правильное масштабирование!
**Решение:** Хранить raw + decimals из конфигурации колонки.
