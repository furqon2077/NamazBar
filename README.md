# NamazBar

Время намаза как на [islom.uz](https://islom.uz/taqvim) (угол 15.5°, мазхаб Ханафи) — два отдельных проекта в одном репозитории.

| Папка | Что это | Сборка |
|---|---|---|
| [`windows/`](windows) | Панель задач Windows + виджет экрана блокировки (C#, .NET Framework, MSIX) | `windows\build.bat` — собрать и запустить; `windows\build.bat /pack` — один установщик `windows\release\NamazBar.exe` |
| [`mac/`](mac) | Строка меню macOS 13+ (Swift) | GitHub Actions (`.github/workflows/mac.yml`) собирает `NamazBar-*.dmg`; на Mac — `mac/build.sh` |
| [`fonts/`](fonts) | Google Sans (OFL), общие для обоих проектов | — |

Расчёт времени в обоих проектах одинаковый (порт adhan-js); сверка — `mac/out/preview/times.txt` против Windows-версии.
