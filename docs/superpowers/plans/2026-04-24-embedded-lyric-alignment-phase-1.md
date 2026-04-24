# Embedded Lyric Alignment Phase 1 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Congelar el auto-sync heurístico actual y preparar a Spomusic para usar alineaciones persistidas por línea dentro de la app.

**Architecture:** La Fase 1 no implementa todavía el motor completo de análisis de audio; introduce el nuevo almacenamiento de alineación verificada, cambia la carga de letras para que pueda usar tiempos por línea persistidos y desactiva la corrección automática agresiva basada en offsets globales. El override manual permanece intacto y con prioridad.

**Tech Stack:** .NET MAUI, C#, SQLite-net, Android `MediaPlayer`

---

### Task 1: Persistencia de alineación verificada

**Files:**
- Modify: `Spomusic/Services/DatabaseService.cs`

- [ ] **Step 1: Agregar la entidad de alineación**

```csharp
public class VerifiedLyricAlignment
{
    [PrimaryKey]
    public string SongKey { get; set; } = string.Empty;
    [Indexed]
    public string LyricHash { get; set; } = string.Empty;
    public string Mode { get; set; } = "Original";
    public double ConfidenceScore { get; set; }
    public string LineTimingsJson { get; set; } = string.Empty;
    public long CreatedUtcTicks { get; set; }
    public long UpdatedUtcTicks { get; set; }
}
```

- [ ] **Step 2: Crear la tabla en `Init()`**

```csharp
await _database.CreateTableAsync<VerifiedLyricAlignment>();
await EnsureColumnAsync("VerifiedLyricAlignment", "LyricHash", "TEXT NOT NULL DEFAULT ''");
await EnsureColumnAsync("VerifiedLyricAlignment", "Mode", "TEXT NOT NULL DEFAULT 'Original'");
await EnsureColumnAsync("VerifiedLyricAlignment", "ConfidenceScore", "REAL NOT NULL DEFAULT 0");
await EnsureColumnAsync("VerifiedLyricAlignment", "LineTimingsJson", "TEXT NOT NULL DEFAULT ''");
await EnsureColumnAsync("VerifiedLyricAlignment", "CreatedUtcTicks", "INTEGER NOT NULL DEFAULT 0");
await EnsureColumnAsync("VerifiedLyricAlignment", "UpdatedUtcTicks", "INTEGER NOT NULL DEFAULT 0");
```

- [ ] **Step 3: Agregar API de repositorio**

```csharp
public async Task<VerifiedLyricAlignment?> GetVerifiedLyricAlignmentAsync(string songKey, string lyricHash) { ... }
public async Task SaveVerifiedLyricAlignmentAsync(VerifiedLyricAlignment alignment) { ... }
public async Task DeleteVerifiedLyricAlignmentAsync(string songKey) { ... }
```

- [ ] **Step 4: Verificar compilación local del archivo**

Run: `dotnet build .\Spomusic\Spomusic.csproj -m:1 -p:TargetFrameworks=net9.0-android -f net9.0-android`
Expected: sin errores de tipos nuevos ni métodos faltantes en `DatabaseService`

### Task 2: Soporte de tiempos alineados por línea

**Files:**
- Modify: `Spomusic/Services/IMusicService.cs`
- Modify: `Spomusic/Services/AndroidMusicService.cs`

- [ ] **Step 1: Extender `LyricLine` para distinguir tiempo original y alineado**

```csharp
public class LyricLine
{
    public int Index { get; set; }
    public TimeSpan Time { get; set; }
    public TimeSpan OriginalTime { get; set; }
    public string Text { get; set; } = string.Empty;
}
```

- [ ] **Step 2: Al parsear LRC, llenar ambos tiempos**

```csharp
lines.Add(new LyricLine
{
    Index = lineIndex,
    Time = parsedTime,
    OriginalTime = parsedTime,
    Text = text
});
```

- [ ] **Step 3: Agregar helpers de hash y aplicación de alineación**

```csharp
private static string ComputeLyricHash(IEnumerable<LyricLine> lines) { ... }
private async Task ApplyVerifiedAlignmentIfAvailableAsync(SongItem song) { ... }
private static void ApplyAlignedTimes(List<LyricLine> lines, IReadOnlyList<long> alignedMs) { ... }
```

- [ ] **Step 4: Cargar alineación verificada después de `ApplyLyrics(...)`**

```csharp
ApplyLyrics(song, requestVersion, lyrics);
await ApplyVerifiedAlignmentIfAvailableAsync(song);
```

- [ ] **Step 5: Verificar que `UpdateCurrentLyric(...)` siga leyendo `Time`**

Run: `rg -n "CurrentLyrics\\[.*\\]\\.Time|FindLastIndex\\(l => l.Time" Spomusic\Services\AndroidMusicService.cs`
Expected: el motor de reproducción usa `Time`, por lo que tiempos alineados persistidos entran sin reescribir más UI

### Task 3: Congelar la heurística automática agresiva

**Files:**
- Modify: `Spomusic/Services/AndroidMusicService.cs`
- Modify: `Spomusic/ViewModels/MainViewModel.cs`

- [ ] **Step 1: Convertir `AutoSyncLyricsAsync()` en no-op seguro**

```csharp
public Task AutoSyncLyricsAsync()
{
    return Task.CompletedTask;
}
```

- [ ] **Step 2: Evitar mensajes engañosos de auto-sync aplicado**

```csharp
SyncStatus = "Auto-sync automático desactivado";
```

- [ ] **Step 3: Quitar el disparo implícito en `TryAutoSyncIfNeededAsync()`**

```csharp
private Task TryAutoSyncIfNeededAsync()
{
    return Task.CompletedTask;
}
```

- [ ] **Step 4: Mantener intacto el ajuste manual**

Run: `rg -n "SetLyricOffsetAsync|RegisterTimingTapAsync|ManualOffsetMs" Spomusic`
Expected: el flujo manual sigue existiendo y no depende del auto-sync heurístico

### Task 4: Verificación

**Files:**
- Modify: `Spomusic/Services/AndroidMusicService.cs`
- Modify: `Spomusic/Services/DatabaseService.cs`
- Modify: `Spomusic/ViewModels/MainViewModel.cs`

- [ ] **Step 1: Compilar Android**

Run: `dotnet build .\Spomusic\Spomusic.csproj -m:1 -p:TargetFrameworks=net9.0-android -f net9.0-android`
Expected: `0 Error(s)`

- [ ] **Step 2: Inspeccionar diff**

Run: `git diff -- Spomusic/Services/DatabaseService.cs Spomusic/Services/AndroidMusicService.cs Spomusic/ViewModels/MainViewModel.cs Spomusic/Services/IMusicService.cs`
Expected: cambios acotados a persistencia, aplicación de alineación y desactivación del auto-sync heurístico

- [ ] **Step 3: Commit**

```bash
git add docs/superpowers/plans/2026-04-24-embedded-lyric-alignment-phase-1.md Spomusic/Services/DatabaseService.cs Spomusic/Services/AndroidMusicService.cs Spomusic/ViewModels/MainViewModel.cs Spomusic/Services/IMusicService.cs
git commit -m "refactor: freeze heuristic lyric auto-sync and add verified alignment storage"
```
