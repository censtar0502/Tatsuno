# Диагностика зависания WPF приложения

## Симптомы
- Проект собирается успешно
- При запуске приложение зависает (UI не отвечает)

## Возможные причины

### 1. Блокировка Dispatcher.Invoke()
**Проблема:** В `MainViewModel.cs` используется `Application.Current.Dispatcher.Invoke()` который может блокироваться если:
- Вызов происходит из UI thread (deadlock)
- Долгая операция в логировании
- Бесконечный цикл в событиях

**Решение:** Заменить на `Dispatcher.BeginInvoke()` для асинхронного выполнения

### 2. Бесконечный цикл в PropertyChanged
**Проблема:** Новые свойства `BackgroundBrush` могут вызывать циклические обновления:
```csharp
BackgroundBrush = Brushes.LightGreen; // Вызывает SetProperty → Raise PropertyChanged
// Если XAML binding имеет TwoWay mode → обратно вызывается setter → бесконечный цикл
```

**Решение:** Убедиться что binding в XAML имеет `Mode=OneWay`

### 3. Проблема с новыми моделями данных
**Проблема:** Модели `PumpAddressState`, `NozzleState` созданы, но не используются.
Где-то может быть попытка доступа к несуществующим данным.

### 4. CloseFileLog() в StartPolling()
**Проблема:** Строка 272 вызывает `CloseFileLog()` каждый раз при старте polling.
Это может вызывать проблемы если файл еще используется.

## План диагностики

### Шаг 1: Проверить XAML bindings
Убедиться что все binding для read-only свойств имеют `Mode=OneWay`:
```xml
Binding="{Binding BackgroundBrush, Mode=OneWay}"
```

### Шаг 2: Добавить логирование входа/выхода
Добавить Debug.WriteLine в критические места:
```csharp
System.Diagnostics.Debug.WriteLine(">>> ApplyPostStatus START");
// ... код ...
System.Diagnostics.Debug.WriteLine("<<< ApplyPostStatus END");
```

### Шаг 3: Заменить Invoke на BeginInvoke
Заменить все `Dispatcher.Invoke()` на `Dispatcher.BeginInvoke()` для неблокирующего выполнения.

### Шаг 4: Убрать CloseFileLog()
Удалить вызов `CloseFileLog()` из `StartPolling()`.

## Быстрый фикс

Попробовать заменить в MainViewModel.cs:

**До:**
```csharp
Application.Current.Dispatcher.Invoke(() => AddLog("TX", RenderBytes(poll)));
```

**После:**
```csharp
Application.Current.Dispatcher.BeginInvoke(() => AddLog("TX", RenderBytes(poll)));
```

И аналогично для всех остальных вызовов.
