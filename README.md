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
