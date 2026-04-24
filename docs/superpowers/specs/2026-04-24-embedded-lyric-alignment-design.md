# Embedded Lyric Alignment Design

## Objetivo

Reemplazar el `auto-sync` heurístico basado en un `offset` global por un motor interno de alineación de letras que opere completamente dentro de la app, sin servicios externos ni programas de terceros, y que priorice no degradar canciones que ya están bien sincronizadas.

## Problema actual

El sistema actual intenta corregir la sincronización usando observaciones de reproducción y un `offset` persistido por canción. Ese enfoque falla en casos reales:

- intros instrumentales largas
- pausas entre frases
- canciones con desfase variable a lo largo del tema
- activaciones tempranas o tardías de líneas LRC
- sobrescritura de sincronías que ya eran aceptables

El defecto estructural es que el modelo asume que toda la canción puede corregirse con un solo desplazamiento temporal.

## Alcance

Esta iteración corrige la arquitectura de raíz dentro de la app:

- eliminar la dependencia funcional del `offset` automático global
- introducir un motor de alineación por segmentos
- persistir una sincronización calculada por línea
- aplicar solo resultados con alta confianza
- conservar el ajuste manual como override explícito

No entra en alcance:

- backends
- herramientas externas
- reconocimiento fonético pesado
- dependencia de librerías nativas de terceros para forced alignment

## Enfoque recomendado

Se implementará un `LyricAlignmentEngine` embebido que analice el audio local y la letra LRC para estimar una línea de tiempo más fiable por segmentos.

La idea central es esta:

1. Analizar el audio local para detectar actividad, onsets y pausas.
2. Dividir la canción en bloques temporales utilizables.
3. Comparar la estructura temporal del audio con la estructura temporal de las líneas LRC.
4. Recalcular tiempos por tramo, no mediante un único `offset`.
5. Guardar el resultado solo si supera un umbral de confianza alto.

## Arquitectura

### 1. `LyricAlignmentEngine`

Nuevo servicio interno responsable de:

- extraer una representación liviana del audio
- calcular energía por ventanas
- detectar transiciones relevantes
- generar segmentos candidatos de voz/frase
- producir una propuesta de alineación por línea
- calcular una puntuación de confianza final

Este motor no debe depender de `MainViewModel` ni de la UI.

### 2. `AudioFeatureExtractor`

Componente interno para obtener señales simples del audio local:

- RMS / energía por ventana
- cambios de energía
- onsets aproximados
- pausas largas

Estas señales bastan para construir un alineador conservador sin prometer precisión fonética total.

### 3. `LyricStructureAnalyzer`

Componente que inspecciona la letra:

- distancia entre líneas
- densidad de líneas por tramo
- bloques de líneas cortas o largas
- silencios esperados entre frases

Su función es convertir la LRC en una estructura comparable contra el audio.

### 4. `LyricAlignmentRepository`

Persistencia de resultados verificados.

Debe guardar por canción:

- identificador estable de canción
- hash de la letra
- modo de sincronización
- confianza
- tiempos finales por línea
- fecha de cálculo

## Modelo de datos

Se agregará una nueva entidad persistente, separada del esquema viejo de offsets:

- `SongKey`
- `LyricHash`
- `Mode` (`Original`, `Manual`, `Aligned`)
- `ConfidenceScore`
- `LineTimingsJson`
- `CreatedUtcTicks`
- `UpdatedUtcTicks`

Reglas:

- `Manual` siempre tiene prioridad sobre `Aligned`.
- `Aligned` nunca se reescribe con un resultado de menor confianza.
- si cambia la letra, el alineamiento previo se invalida por `LyricHash`.

## Flujo de ejecución

Cuando una canción carga letras:

1. Se busca un alineamiento persistido que coincida con `SongKey + LyricHash`.
2. Si existe y es confiable, se usa directamente.
3. Si no existe, la app reproduce con los tiempos actuales mientras dispara un análisis local en segundo plano.
4. El motor calcula una propuesta de tiempos por línea.
5. Si la confianza es alta, se persiste y se usa en la siguiente reproducción o se refresca en caliente si el salto es seguro.
6. Si la confianza no alcanza el umbral, no se aplica ninguna corrección automática.

## Estrategia de alineación

El motor será conservador y por segmentos:

- detecta una intro probable y evita alinear antes de actividad sostenida
- identifica bloques temporales con actividad y pausas
- asigna líneas LRC a bloques compatibles por densidad y duración
- interpola tiempos dentro de cada bloque
- ajusta fronteras con eventos locales del audio

No intentará inventar precisión cuando la señal sea ambigua.

## Reglas de seguridad

- nunca usar un `offset` automático global como fuente principal
- nunca corregir una canción si la confianza es baja
- nunca sobrescribir un ajuste manual
- nunca “mejorar” una canción ya alineada con una estimación peor
- si el análisis falla, mantener la sincronía original

## Compatibilidad y migración

El sistema actual de `LyricTimingOverride` se mantendrá solo para compatibilidad y para el modo manual.

Cambios:

- `AutoSyncLyricsAsync()` dejará de modificar offsets persistidos como mecanismo principal
- los `AutoOffsetMs` existentes pasarán a considerarse legado no confiable
- la reproducción consultará primero alineaciones verificadas por línea
- el ajuste manual seguirá funcionando como override

## Integración en código existente

### `AndroidMusicService`

Cambios esperados:

- dejar de depender de `_currentSongTimingOffsetMs` para auto-corrección
- cargar tiempos alineados si existen
- disparar el análisis local en segundo plano al obtener letras
- aplicar override manual por encima de la alineación calculada

### `DatabaseService`

Cambios esperados:

- nueva tabla para alineaciones verificadas
- métodos para guardar, leer e invalidar alineaciones
- soporte para `LyricHash`

### `MainViewModel`

Cambios esperados:

- dejar de presentar el auto-sync como corrección central
- reflejar estado de análisis y confianza si aporta valor
- mantener controles manuales como fallback humano

## Rendimiento

El análisis puede tardar unos segundos por canción. Eso es aceptable.

Mitigaciones:

- ejecutar fuera del hilo principal
- trabajar con ventanas y features livianas
- cachear resultados por canción y letra
- no recalcular si ya existe alineación válida

## Manejo de errores

- si el audio no puede analizarse, no se bloquea reproducción
- si la letra no tiene estructura utilizable, no se alinea
- si el análisis se cancela por cambio de canción, se descarta
- si la persistencia falla, no se pierde reproducción actual

## Estrategia de pruebas

Casos mínimos:

- canción con intro instrumental larga
- canción con pausas largas entre líneas
- canción ya bien sincronizada desde origen
- canción con desfase uniforme
- canción con desfase variable por tramos
- cambio de canción durante análisis
- letra modificada respecto a una alineación guardada

Validaciones:

- no degradar canciones ya buenas
- no sobrescribir manuales
- no bloquear la UI
- persistir y reutilizar alineaciones correctas

## Plan de implementación

Fase 1:

- congelar la heurística automática actual
- introducir nuevo modelo de persistencia
- enrutar reproducción para soportar tiempos por línea calculados

Fase 2:

- implementar extracción de features de audio
- implementar segmentación y puntuación de confianza
- persistir alineaciones verificadas

Fase 3:

- integrar refresco en caliente seguro
- limpiar deuda del offset automático legado
- validar con canciones reales y ajustar umbrales

## Decisiones cerradas

- todo debe vivir dentro de la app
- no se usarán servicios externos
- no se usarán programas de terceros
- se prioriza exactitud práctica y no degradación por encima de “corregir siempre”
- se acepta un costo extra de análisis local por canción
