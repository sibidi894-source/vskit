[English](#vskit-terminal) | [Русский](#vskit-terminal-ru)

# vskit-terminal

Small console client for any OpenAI-compatible chat API. Self-contained C# (.NET Framework 4.0) executable, no external dependencies, streaming SSE replies, full PC tool-calling support, and config saved in `api.conf`.

## Setup

**Option 1 — script:**
```powershell
powershell -ExecutionPolicy Bypass -File .\setup-api.ps1
```

**Option 2 — inside the terminal:**
```text
/url https://your-provider.com/v1
/key sk-...
/model deepseek-v4-flash
```

**Option 3 — command line:**
```powershell
vskit_terminal.exe --url https://your-provider.com/v1 --key sk-... --model deepseek-v4-flash
```

Everything is saved to `api.conf` next to the exe (see `api.conf.example`). The key is stored in plain text there — keep `api.conf` out of shared folders and repositories.

## Build from source

```bat
build.bat
```
Or directly with .NET Framework 4.0 `csc.exe`:
```powershell
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /optimize+ /target:exe /out:vskit_terminal.exe Program.cs
```

## Using it

Anything you type goes to the model as a message. Lines starting with `/` (or known commands) run locally:

| Command | What it does |
| --- | --- |
| `/model [name]` | Show current model, or switch to `<name>` |
| `/models` | Interactive model picker (`↑`/`↓` + `Enter`) |
| `/api <on\|off>` | Enable or disable sending requests to the API |
| `/url <url>` | Set API base URL (e.g. `https://host/v1`) |
| `/key <key>` | Set API key (saved in `api.conf`) |
| `/think <lvl>` | Thinking level: `on` \| `off` \| `none` \| `minimal` \| `low` \| `medium` \| `high` \| `default` |
| `/tools <on\|off>` | Enable/disable PC tools (`run_command`, `read_file`, `write_file`, `list_dir`) |
| `/approve <ask\|auto>` | Confirm each tool action or run automatically |
| `/workdir <path>` | Default working directory for relative paths |
| `/system <text>` | Set the system prompt |
| `/new` | Clear conversation context |
| `/status` | Show current configuration |
| `/clear` | Wipe the screen |
| `/clear-cont` | Wipe screen + conversation context and redraw banner |
| `/help` | Show command list |
| `/help api` | Full API guide, key info, and `/api` toggle |
| `/infovs` | Terminal info, purpose, backend details, and ASCII art |
| `/lain` | Open Lain GIF in default browser |
| `/exit` | Quit |

---

<a id="vskit-terminal-ru"></a>
# vskit-terminal (Русский)

Небольшой консольный клиент для любого OpenAI-совместимого chat API. Самодостаточный файл на C# (.NET Framework 4.0) без внешних зависимостей, со стримингом ответов (SSE), поддержкой инструментов управления ПК и сохранением настроек в `api.conf`.

## Настройка

**Вариант 1 — через скрипт:**
```powershell
powershell -ExecutionPolicy Bypass -File .\setup-api.ps1
```

**Вариант 2 — прямо в терминале:**
```text
/url https://your-provider.com/v1
/key sk-...
/model deepseek-v4-flash
```

**Вариант 3 — аргументами командной строки:**
```powershell
vskit_terminal.exe --url https://your-provider.com/v1 --key sk-... --model deepseek-v4-flash
```

Все параметры сохраняются в файл `api.conf` рядом с `.exe` (шаблон есть в `api.conf.example`). Ключ хранится там в открытом виде — не добавляй `api.conf` в публичные репозитории.

## Сборка из исходников

```bat
build.bat
```
Или напрямую через `csc.exe` из .NET Framework 4.0:
```powershell
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /optimize+ /target:exe /out:vskit_terminal.exe Program.cs
```

## Использование

Любой обычный текст отправляется модели как сообщение. Строки с `/` (и известные команды) выполняются локально:

| Команда | Описание |
| --- | --- |
| `/model [name]` | Показать текущую модель или переключиться на `<name>` |
| `/models` | Интерактивный выбор модели стрелками (`↑`/`↓` + `Enter`) |
| `/api <on\|off>` | Включить или выключить отправку запросов к API |
| `/url <url>` | Задать базовый URL API (например, `https://host/v1`) |
| `/key <key>` | Задать API-ключ (сохраняется в `api.conf`) |
| `/think <lvl>` | Уровень размышлений: `on` \| `off` \| `none` \| `minimal` \| `low` \| `medium` \| `high` \| `default` |
| `/tools <on\|off>` | Включить/выключить доступ модели к ПК (`run_command`, `read_file`, `write_file`, `list_dir`) |
| `/approve <ask\|auto>` | Спрашивать подтверждение перед действием или выполнять автоматически |
| `/workdir <path>` | Рабочая папка по умолчанию для относительных путей |
| `/system <text>` | Задать системный промпт |
| `/new` | Очистить контекст диалога |
| `/status` | Показать текущую конфигурацию |
| `/clear` | Очистить экран |
| `/clear-cont` | Стереть экран и контекст чата до заставочного экрана |
| `/help` | Показать список команд |
| `/help api` | Подробная справка по API, ключу и переключению `/api` |
| `/infovs` | Информация о создании терминала, его цели, бэкенде и ASCII-арт |
| `/lain` | Открыть гифку с Лэйн в браузере по умолчанию |
| `/exit` | Выйти |
