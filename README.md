# EliteRec для Linux

Запись звука видеопрезентаций (Zoom, Яндекс Телемост): микрофон менеджера и звук
системы — общим миксом и раздельными каналами для расшифровки.

План, решения и этапы — [docs/PLAN.md](docs/PLAN.md).

## Состояние разработки

Реализован R2: два потока libpulse, синхронизация, общий WAV-микс и разделённое
стерео, ручная запись/пауза/стоп и живые уровни в окне. Часовой прогон на
виртуальных источниках пройден: максимальный сдвиг 14,188 мс, потерь нет.
MP3, восстановление после сбоя и управление из трея пока не подключены.

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

На Linux нужны .NET SDK 10, `libpulse0`, `libmp3lame0` и
`pulseaudio-utils` (для диагностического каталога и smoke-скрипта).
Продуктовый каталог устройств работает напрямую с libpulse и не требует pactl.
