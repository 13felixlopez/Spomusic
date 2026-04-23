# Spomusic

Spomusic es un reproductor de música local hecho con .NET MAUI enfocado en Android, con biblioteca propia, cola editable, letras sincronizadas y un reproductor completo inspirado en flujos tipo Spotify.

## Stack Tecnológico

- .NET MAUI 9
- C#
- SQLite (`sqlite-net-pcl`)
- CommunityToolkit.Maui
- Android MediaPlayer / foreground service

## Funcionalidades Principales

- [x] Biblioteca local con escaneo de carpetas y favoritos
- [x] Playlists locales
- [x] Mini reproductor y reproductor completo
- [x] Modo aleatorio y repetición persistidos entre sesiones
- [x] Cola editable con mezcla inteligente para evitar artistas repetidos seguidos
- [x] Letras sincronizadas, descarga offline y ajuste de timing
- [x] Sleep timer con tiempos rápidos y minutos personalizados
- [x] Pausa automática por interrupciones de audio de otras apps
- [x] Reproducción foreground con controles desde la notificación

## UI y Adaptabilidad

- La pantalla principal y el reproductor completo recalculan portada y paneles laterales según el ancho real disponible para mantener una composición estable en distintos teléfonos.
- La app queda bloqueada en orientación vertical en Android e iOS para conservar la misma jerarquía visual del reproductor y evitar una variante horizontal no diseñada.

## Guía de Configuración

1. Instala las workloads necesarias de .NET MAUI para el target que vayas a compilar.
2. Restaura paquetes con `dotnet restore`.
3. Para Android, compila con `dotnet build Spomusic/Spomusic.csproj -f net9.0-android -c Debug`.
4. Otorga permisos de acceso a medios al iniciar la app en Android.

## Estado del Proyecto

En desarrollo.
