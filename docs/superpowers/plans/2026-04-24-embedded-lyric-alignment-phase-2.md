# Embedded Lyric Alignment Phase 2 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implementar el motor interno que analice audio local y produzca alineaciones persistidas por línea sin depender de servicios externos.

**Architecture:** Esta fase agrega un `ILyricAlignmentEngine` embebido y un pipeline Android-only que decodifica una señal PCM ligera desde el archivo local, extrae energía/onsets/regiones activas y ajusta las líneas LRC contra esa estructura temporal. `AndroidMusicService` lo invoca en segundo plano solo cuando la letra tiene timestamps reales y aún no existe una alineación verificada confiable.

**Tech Stack:** .NET MAUI, C#, SQLite-net, Android `MediaExtractor`, Android `MediaCodec`

---

### Task 1: Contratos del motor de alineación

**Files:**
- Create: `Spomusic/Services/LyricAlignmentModels.cs`
- Create: `Spomusic/Services/ILyricAlignmentEngine.cs`
- Modify: `Spomusic/MauiProgram.cs`

- [ ] Definir `LyricAlignmentResult`, `LyricAlignmentDiagnostics` y `ILyricAlignmentEngine`.
- [ ] Registrar el engine en DI.
- [ ] Mantener un fallback no-op para plataformas no Android.

### Task 2: Extracción de features de audio

**Files:**
- Create: `Spomusic/Services/EmbeddedLyricAlignmentEngine.cs`

- [ ] Implementar decodificación PCM con `MediaExtractor` + `MediaCodec`.
- [ ] Convertir a mono y reducir a una envolvente por ventanas.
- [ ] Calcular RMS, deltas de energía y onsets aproximados.

### Task 3: Alineación por líneas

**Files:**
- Modify: `Spomusic/Services/EmbeddedLyricAlignmentEngine.cs`
- Modify: `Spomusic/Services/IMusicService.cs`

- [ ] Marcar si una línea tiene timestamp explícito.
- [ ] Rechazar letras planas sin tiempo real.
- [ ] Ajustar cada línea a candidatos de onset/región activa con restricción monótona.
- [ ] Calcular `ConfidenceScore` y descartar resultados débiles.

### Task 4: Integración con `AndroidMusicService`

**Files:**
- Modify: `Spomusic/Services/AndroidMusicService.cs`

- [ ] Inyectar `ILyricAlignmentEngine`.
- [ ] Lanzar análisis en segundo plano al cargar letras si no existe alineación verificada.
- [ ] Persistir alineaciones con confianza suficiente.
- [ ] Cancelar análisis cuando cambie la canción.
- [ ] No sobrescribir overrides manuales ni alinear si ya existe una alineación confiable.

### Task 5: Verificación

**Files:**
- Modify: `Spomusic/Services/EmbeddedLyricAlignmentEngine.cs`
- Modify: `Spomusic/Services/AndroidMusicService.cs`

- [ ] Compilar `net9.0-android`.
- [ ] Revisar el diff para confirmar que la fase se limita al motor, DI e integración.
- [ ] Validar que el flujo manual siga intacto.
