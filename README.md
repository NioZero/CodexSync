# CodexSync

Aplicación de escritorio para Windows que permite fusionar dos historiales locales de Codex en una nueva carpeta `.codex`.

El proyecto está construido con .NET 10 y Windows Forms. Está pensado para el caso en que dos PC hayan trabajado con historiales diferentes y sus carpetas `.codex` se hayan copiado previamente a una ubicación accesible.

## Funcionalidades

- Selección de dos carpetas `.codex` de origen.
- Selección de una carpeta de salida independiente.
- Selector opcional para `sqlite3.exe` cuando la CLI de SQLite no está en `PATH`.
- Fusión recursiva de `sessions/` y `archived_sessions/`.
- Deduplicación de archivos idénticos mediante SHA-256.
- Conservación de colisiones de archivos con nombres alternativos (`-from-a` y `-from-b`).
- Fusión de líneas únicas de `session_index.jsonl`.
- Copia consistente y fusión de `state_5.sqlite` mediante la CLI de SQLite.
- Las carpetas de origen nunca se modifican.

La carpeta de salida debe estar vacía. Para conflictos de filas en SQLite se conserva la versión del origen A (`INSERT OR IGNORE`). Las tablas nuevas de B se incorporan cuando SQLite puede leer un esquema compatible.

## Requisitos

- Windows 10/11.
- .NET SDK 10.0 o superior.
- `sqlite3.exe` instalado y disponible en `PATH`, o indicar su ubicación desde la aplicación si se va a fusionar `state_5.sqlite`.

## Ejecutar desde VS Code

Desde la raíz del repositorio:

```powershell
dotnet build CodexSync.slnx
dotnet run --project .\CodexSync.App\CodexSync.App.csproj
```

En la ventana, seleccione las dos carpetas de origen, una carpeta de salida vacía y, si hace falta, el ejecutable de SQLite. Después pulse **Fusionar en carpeta de salida**.

## Estructura

```text
CodexSync.slnx
└── CodexSync.App
    ├── MainForm.cs              # Interfaz Windows Forms
    ├── CodexHistoryMerger.cs    # Fusión de archivos, JSONL y SQLite
    └── Program.cs
```

## Verificación

La solución se verifica con:

```powershell
dotnet build CodexSync.slnx --no-restore
```

