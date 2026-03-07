# Tatsuno project update

Сделано:
- переработан `Tatsuno.Model` под `Q60 / Q61 / Q62 / Q65`;
- добавлен `TatsunoControllerEngine` с очередью команд и link-layer FSM;
- переработан WPF UI под вертикальные посты, активный пост, отдельные цены по каждому пистолету, пресеты по сумме/объёму, кнопки `Status / Totals / Cancel / Lock / Release`;
- добавлен стартовый C++ DLL skeleton `Tatsuno.Native`.

Поддерживаемые команды:
- `A10` — authorize single price;
- `A14` — release lock;
- `A15` — request status;
- `A19` — cancel authorization;
- `A20` — request totals;
- `A43` — lock pump.

Поддерживаемые ответы:
- `Q60` — controllability;
- `Q61` — live status;
- `Q62` — receive error;
- `Q65` — totals by nozzle.

Текущие допущения:
- форматирование цены и суммы в UI сделано по эталонному логу и скриншотам: `protocolRaw * 10`;
- форматирование объёма: `raw / 100`;
- адрес поста в UI — логический адрес оператора; в имеющемся логе отдельного адресного байта поста в полезной нагрузке нет, поэтому ответы привязываются к последнему опрошенному посту.

Что проверить на Windows:
- сборку solution в Visual Studio;
- корректность XAML layout на вашем мониторе;
- соответствие реальных scaling rules цены/суммы вашему оборудованию;
- реальную последовательность команд `A10/A19/A20/A15` на железе.
