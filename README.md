# EliteRec для Linux

Запись звука видеопрезентаций (Zoom, Яндекс Телемост): микрофон менеджера и звук
системы — в один стерео-файл (слева менеджер, справа клиент) для расшифровки;
по желанию ещё общий моно-файл.

План, решения и этапы — [docs/PLAN.md](docs/PLAN.md).

## Состояние разработки

Реализован R2: два потока libpulse, синхронизация, общий WAV-микс и разделённое
стерео, ручная запись/пауза/стоп и живые уровни в окне. Часовой прогон на
виртуальных источниках пройден: максимальный сдвиг 14,188 мс, потерь нет.
R3: окно пишет MP3, FLAC, Opus или WAV и после падения предлагает восстановить
запись ([docs/R3-ENCODING.md](docs/R3-ENCODING.md)). R4: пропавшее во время
записи устройство заменяется системным и возвращается, когда появится
([docs/R4-DEVICES.md](docs/R4-DEVICES.md)). R5: окно по макету 6 «Стеклянная волна», трей, уведомление о старте,
название записи после «Стоп», настройки с самопроверкой; по умолчанию
пишется один стерео-файл
([docs/R5-INTERFACE.md](docs/R5-INTERFACE.md)).

Алгоритм и текущие результаты — [docs/R2-AUDIO.md](docs/R2-AUDIO.md).

Состав каркаса, проверки и снимок окна — [docs/R1-SCAFFOLD.md](docs/R1-SCAFFOLD.md).

Результаты и оставшиеся проверки — [docs/R0-RISKS.md](docs/R0-RISKS.md).

```bash
dotnet restore EliteRec.Linux.slnx
dotnet build EliteRec.Linux.slnx -c Release --no-restore
dotnet test EliteRec.Linux.slnx -c Release --no-build --no-restore
dotnet run --project src/EliteRec.App -c Release --no-build
dotnet tools/EliteRec.Probe/bin/Release/net10.0/EliteRec.Probe.dll help
```

На Linux нужны .NET SDK 10, `libpulse0`, `libmp3lame0`, libFLAC (`libflac14` в Debian 13), `libopus0` и
`pulseaudio-utils` (для диагностического каталога и smoke-скрипта).
Продуктовый каталог устройств работает напрямую с libpulse и не требует pactl.

## Установка

Пакет собирается `packaging/build-deb.sh --version 0.1.0` (или берётся из
артефакта CI `eliterec-deb`) и ставится на Linux Mint 22, Ubuntu 24.04 или
Debian 13:

```bash
sudo apt install ./artifacts/EliteRec-0.1.0.deb
```

Дальше программа обновляется сама с канала `update.elitesip.vip` (служба
`eliterec-updater`, раз в 10 минут, никогда во время записи). Выпуск —
`packaging/release.sh`, выкладка — `packaging/publish.sh`. Подробности —
[docs/R6-PACKAGING.md](docs/R6-PACKAGING.md).
