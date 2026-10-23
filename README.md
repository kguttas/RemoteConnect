# RemoteConnect

[![Descargar RemoteConnect.exe](https://img.shields.io/github/v/release/kguttas/RemoteConnect?label=Descargar%20RemoteConnect.exe&style=for-the-badge&logo=windows)](https://github.com/kguttas/RemoteConnect/releases/latest/download/RemoteConnect.exe)

**[⬇ Descargar la última versión (RemoteConnect.exe)](https://github.com/kguttas/RemoteConnect/releases/latest/download/RemoteConnect.exe)** · [Todas las versiones](https://github.com/kguttas/RemoteConnect/releases) · Windows 10/11 x64, sin instalar nada más

Escritorio remoto para Windows: rápido (H.264 / HEVC / AV1 por GPU de punta a punta, ~60–120 fps, bitrate adaptativo), cifrado (TLS / QUIC), y que
**no depende de la IP**: cada equipo se identifica por un ID estable y se localiza por la red aunque el DHCP
le cambie la dirección. Si la conexión se cae, se reconecta solo. **También por Internet**, P2P y gratis:
sin abrir puertos en el router ni montar servidores (ver [Conexión por Internet](#conexión-por-internet-p2p)).

## Instalación (2 minutos)

Un único archivo: [`RemoteConnect.exe`](https://github.com/kguttas/RemoteConnect/releases/latest/download/RemoteConnect.exe) (autocontenido, no necesita .NET instalado; al compilarlo tú queda en `dist\RemoteConnect.exe`).

**En el PC remoto (el que quieres controlar):**
1. Ejecuta `RemoteConnect.exe` → pestaña **Este equipo**.
2. Escribe una contraseña (dos veces) → **Guardar contraseña**.
3. **Instalar en este equipo** (pide permiso de administrador una vez). Esto:
   - copia el exe a `C:\Program Files\RemoteConnect`,
   - abre el firewall solo para RemoteConnect,
   - registra el **servicio de Windows `RemoteConnect`** (LocalSystem, arranque automático). El servicio lanza el
     host como SYSTEM dentro de la sesión de consola activa, así que funciona **en la pantalla de bloqueo, en el
     inicio de sesión, en Ctrl+Alt+Supr y en los avisos UAC**, y también con ventanas elevadas,
   - activa la directiva `SoftwareSASGeneration=1` para que el servicio pueda enviar Ctrl+Alt+Supr,
   - protege `C:\ProgramData\RemoteConnect` (contraseña y clave TLS) para SYSTEM, administradores y tu usuario,
   - añade el acceso directo al menú Inicio.

   Si ya tenías la versión anterior instalada, pulsa **Reinstalar / actualizar**: sustituye la tarea de inicio de
   sesión por el servicio y conserva el ID, la contraseña y el certificado (los clientes siguen confiando en el equipo).

**En tu PC (desde el que te conectas):** ejecuta `RemoteConnect.exe`. El equipo remoto aparece solo en la
pestaña **Conectar** (punto verde). Pulsa **Conectar**, mete la contraseña (se puede recordar, cifrada con DPAPI).
Instalarlo en el cliente es opcional.

## Uso

| Función | Cómo |
|---|---|
| Pantalla completa | Botón ⛶ o `Ctrl+Alt+Intro`. Lleva el ratón al borde superior para ver la barra. |
| Teclas de sistema (Win, Alt+Tab, Ctrl+Esc…) | Botón **Teclas Win** (activo por defecto). `Ctrl+Alt+Inicio` libera el teclado. |
| Texto perfectamente nítido para programar | **Texto nítido** (color 4:4:4 sin submuestreo; NVENC o x264). |
| Píxel exacto / ajustar a ventana | **Ajustar** ↔ **1:1** |
| Calidad y fluidez | Selectores de Mbps (5–200) y fps (30–144). Con **Auto** (por defecto) los Mbps son el techo y el host ajusta el bitrate a lo que la red entrega de verdad; desactívalo para un bitrate fijo. |
| Códec | **Auto** (H.264 en la LAN; AV1 o HEVC por Internet si ambos equipos los codifican/decodifican por hardware), o fíjalo a H.264 / HEVC / AV1. |
| Varios monitores | Selector de monitor. |
| Portapapeles | Automático en ambos sentidos (texto e imágenes). |
| Enviar archivos/carpetas | Arrástralos a la ventana o **Acciones → Enviar archivos…**. Llegan a `Descargas\RemoteConnect` del remoto (configurable). |
| Recibir archivos/carpetas del remoto | **Acciones → Recibir archivos del remoto…**: explorador de las unidades y carpetas del remoto; selecciona (varios con Ctrl/Mayús) y **Descargar**, o doble clic en un archivo. Se guardan en `Descargas\RemoteConnect` de este equipo (**Cambiar…**). Se puede cancelar a medias (lo incompleto se borra). |
| Audio | **Audio** (el sonido del remoto se oye en local). |
| Ctrl+Alt+Supr (desbloquear, pantalla de seguridad) | **Acciones → Ctrl+Alt+Supr** (requiere el servicio instalado). |
| Administrador de tareas remoto | **Acciones → Administrador de tareas**. |
| Encender un PC dormido | **Despertar** (Wake-on-LAN; debe estar activado en la BIOS/adaptador). |
| Sin descubrimiento (otra subred, VPN) | Escribe IP o nombre (`equipo` o `equipo:puerto`) arriba y **Conectar**. |
| Desde fuera de casa / la oficina | Escribe el **ID de Internet** del remoto (`123 456 789`, pestaña *Este equipo*) arriba y **Conectar**. |

Estadísticas en la barra: fps, Mbps y latencia de red; pasa el ratón por encima para ver resolución, códec, si la captura
va por GPU, el bitrate que ha elegido el host, la cola de red estimada y los tiempos de codificación y decodificación.

## Cómo resuelve el cambio de IP

1. Cada host genera un **ID** y un **certificado TLS** propios y los anuncia por broadcast UDP (puertos 47801/47802) cada 3 s.
2. El cliente guarda el equipo por ID, no por IP. Al conectar (y en cada reconexión) busca primero ese ID en la red,
   luego prueba la última IP conocida y por último el nombre de equipo (DNS/LLMNR/NetBIOS).
3. La identidad se verifica con la huella del certificado, fijada la primera vez (TOFU): si otra máquina ocupa la IP
   antigua, no se le envía la contraseña.
4. Si la conexión se corta (5 s sin respuesta), la ventana muestra "Reconectando…" y vuelve sola, sin cerrar nada.

## Conexión por Internet (P2P)

Cada host tiene un **ID de Internet** de 9 cifras (derivado de su ID estable, no cambia). Con ese ID y la contraseña
se conecta desde cualquier red. Los equipos ya conectados alguna vez en la LAN lo aprenden solos: si un día no están
en tu red, la reconexión pasa a Internet automáticamente.

Cómo funciona, sin servidor propio y sin coste:
1. Cada lado pregunta su IP:puerto público a servidores **STUN** públicos (Google, Cloudflare).
2. Se intercambian esas direcciones a través de **ntfy.sh** (pub/sub HTTP público). El nombre del tema y el contenido
   salen de una clave derivada de la **contraseña** + ID (PBKDF2, AES-GCM): ntfy solo ve datos cifrados, sin
   posibilidad de saber quién habla con quién, y nadie sin la contraseña puede ni encontrar el tema.
3. Ambos envían paquetes UDP al otro (*hole punching*), liberan el socket y abren **QUIC** (TLS 1.3, msquic) en el
   mismo puerto. Sobre ese flujo va exactamente el mismo protocolo que en la LAN (contraseña incluida).
4. La huella del certificado del host viaja dentro de la respuesta cifrada, así que hasta la primera conexión va fijada.

Para activarlo en un equipo que ya tenía contraseña: **vuelve a guardar la contraseña** (así se genera la clave de
señalización) y, si está instalado, pulsa **Reinstalar / actualizar**. Se puede desactivar con *Permitir por Internet*.
Para usar tu propio ntfy en vez del público, cambia `SignalServer` en `host.json` y `client.json`.

## Rendimiento medido (este equipo: i9-13980HX, RTX 4070 portátil + Intel UHD, panel 1920×1200)

Cliente y host en la misma máquina, NVENC H.264 1920×1200, techo 30 Mbps, 60 fps, animación continua. Los enlaces
lentos se simulan con `REMOTECONNECT_SIMLINK` (ancho de banda + retardo en el host).

| Enlace | Versión | fps | RTT | Latencia pantalla→imagen* (mediana / p90) | Captura→pantalla (mediana) |
|---|---|---|---|---|---|
| Local | anterior | 59,6 | 0,3 ms | 35 / 42 ms | — |
| Local | actual | 59,5 | 0,1 ms | 43 / 48 ms | 14 ms |
| 50 Mbps, 10 ms | anterior | 59,5 | 11 ms | 56 / 62 ms | — |
| 50 Mbps, 10 ms | actual | 57,4 | 13 ms | 63 / 69 ms | 33 ms |
| 20 Mbps, 20 ms | anterior | 38,3 | **1761 ms** | 261 / 1118 ms | — |
| 20 Mbps, 20 ms | actual H.264 | 56,4 | 36 ms | 78 / 85 ms | 48 ms |
| 20 Mbps, 20 ms | actual AV1 | 51,9 | 58 ms | 103 / 107 ms | 73 ms |
| 10 Mbps, 40 ms | anterior | 18,6 | **3564 ms** | (> 1 s; el contador de la prueba da la vuelta) | — |
| 10 Mbps, 40 ms | actual H.264 | 53,1 | 56 ms | 117 / 122 ms | 87 ms |
| 10 Mbps, 40 ms | actual AV1 | 57,6 | 42 ms | 98 / 104 ms | 70 ms |

\* Incluye la presentación de la ventana de prueba por WPF/DWM (1–2 refrescos). La versión anterior se midió con un
parche de color y la actual con una rejilla blanco/negro (más robusta), así que en local la diferencia de ±5 ms está
dentro del error de medida. "Captura→pantalla" usa la marca de tiempo de captura del host: es el pipeline puro.

Por componente (`--selftest`, 1920×1080):

| Paso | Antes | Ahora |
|---|---|---|
| Decodificar + convertir + mostrar (cliente) | 1,6–2 ms CPU + copia a WPF | 0,3 ms H.264 / 0,4 ms HEVC / 1,2 ms AV1 (D3D11VA + shader + swap chain) |
| Codificar NVENC H.264 | 6 ms (BGRA desde CPU) | 4,4 ms con la textura de captura en la GPU (si la pantalla y NVENC están en la misma GPU) |
| Audio | PCM sin comprimir ~1,5 Mbps | Opus 128 kb/s, tramas de 10 ms |

Sin cambios en pantalla no se envía vídeo (solo se refina la calidad unos instantes tras cada cambio).

## Transferencia de archivos

Va por un carril propio de baja prioridad (el mismo de los envíos), así que una descarga grande no frena el vídeo, el
audio ni el ratón, y usa todo el ancho de banda que quede libre (~280 MB/s en local, ~18 Mbps en un enlace de 20 Mbps).

**Seguridad.** Instalado como servicio, el host corre como SYSTEM, que podría leer los archivos de todos los usuarios.
Por eso el explorador remoto y las descargas se hacen **suplantando al usuario que tiene la sesión iniciada** en el
remoto: solo se ve lo que ese usuario puede abrir, y si no hay nadie con sesión iniciada no se sirve nada. El dueño del
remoto puede desactivarlo en *Este equipo* → **Permitir que el cliente explore y descargue archivos de este equipo**.

## Limitaciones conocidas

- **Pantalla de bloqueo / inicio de sesión / UAC** solo funcionan con RemoteConnect **instalado** (servicio). En modo
  portátil (`--host` sin instalar) el host corre como tu usuario y no puede ver esos escritorios seguros.
- Se controla la sesión de **consola** (la del monitor físico), no sesiones RDP paralelas.
- No hay copiar/pegar de archivos por el portapapeles (sí texto e imágenes): usa **Enviar archivos** / **Recibir archivos**.
- Recibir archivos necesita RemoteConnect **2.1** o posterior en los dos equipos (con un host 2.0 la opción avisa y no hace nada).
- AMD AMF y Media Foundation están soportados pero no probados en esta máquina (NVENC, Quick Sync y x264 sí).
- En portátiles híbridos (pantalla en la GPU integrada, NVENC en la dedicada) la captura sin copia no es posible y el
  host usa la copia por CPU, como antes. HEVC por Quick Sync no admite cambios de bitrate en caliente: se reabre.
- NVENC y Quick Sync aplican cada cambio de bitrate con un fotograma IDR (así lo hace FFmpeg), por eso el control
  adaptativo baja al momento cuando la red se satura pero sube como mucho cada 2 s. El tamaño de esos IDR está
  limitado por el búfer CBR de 2 fotogramas, así que no producen picos de latencia (por eso no se usa *intra-refresh*).
- **Internet sin relé**: si los dos lados están tras NAT simétrico (algunos CGNAT de operadoras, redes móviles,
  redes corporativas) el hole punching no pasa y no conecta. Si uno de los dos tiene IPv6 público, suele bastar.
- El modo Internet requiere QUIC: Windows 11 o Windows Server 2022 en ambos lados.

## Desarrollo

```
dotnet build src/RemoteConnect                         # debug
dotnet publish src/RemoteConnect -c Release -o dist    # exe único (~100 MB)
```

Diagnóstico (resultados en `%AppData%\RemoteConnect\selftest.txt`; registro del host y del servicio en
`%ProgramData%\RemoteConnect\remoteconnect.log`):
```
RemoteConnect.exe --selftest                                   # cada codificador: velocidad y fidelidad de color, captura
RemoteConnect.exe --testclient <ip[:puerto]> <contraseña> <seg> [--animate] [--444] [--fps N] [--mbps N] [--codec h264|hevc|av1]
RemoteConnect.exe --testclient <ID de Internet> <contraseña> <seg> ... [--signal URL]   # solo por la ruta P2P (STUN + ntfy + QUIC)
```
Para probar con un segundo host aislado en la misma máquina (sin tocar el servicio instalado), define
`REMOTECONNECT_HOSTDIR=<carpeta>` antes de `--set-password` y `--host`: usa su propia configuración, certificado e ID.
`REMOTECONNECT_SIMLINK=<Mbps>,<ms>` en el host simula un enlace lento (cola del tamaño del búfer de envío + retardo), y
`REMOTECONNECT_FFLOG=1` vuelca los mensajes detallados de FFmpeg al registro.
Otros modos: `--service` (lo usa el SCM), `--agent` (host como SYSTEM en la sesión, lanzado por el servicio),
`--host` (host portátil como usuario), `--start-service`, `--set-password <pwd>`, `--install <usuario>`, `--uninstall`, `--firewall`.

Estructura (`src/RemoteConnect`):
- `Core/` protocolo (marcos con 4 carriles de prioridad: control, audio, vídeo, archivos; sobre TLS o un flujo QUIC por
  carril), descubrimiento, ajustes, seguridad, instalador, recepción de archivos (`FileTransfer.cs`), Opus (`AudioCodec.cs`), simulador de enlace (`SimLink.cs`);
  `P2p.cs`: ID de Internet, STUN, hole punching, señalización ntfy cifrada, opciones QUIC.
- `Host/` captura DXGI Desktop Duplication, codificador FFmpeg (NVENC → QSV → AMF → x264 → MF; entrada D3D11 sin copia
  cuando puede), control de congestión por retardo (`RateControl.cs`: acuses por fotograma, RTT base, cola estimada),
  inyección de entrada (`SendInput` por scancode), audio WASAPI loopback, servidor y sesión;
  `FileServer.cs`: explorador y descargas remoto→local suplantando al usuario de la sesión.
- `Client/` sesión con reconexión (candidatos LAN / DNS / Internet en paralelo, gana el primero), pipeline de vídeo
  (D3D11VA → shader YUV→RGB → swap chain flip en una ventana nativa `VideoHost`; solo se dibuja el último frame),
  audio, hook de teclado.
- `UI/` ventanas WPF (tema Fluent de Windows 11).

Notas técnicas que costó encontrar:
- Windows completa `send()` en cuanto los datos caben en el búfer del socket: con 4 MB de `SendBufferSize` se
  acumulaban segundos de vídeo en enlaces lentos (RTT de 1,7 s a 20 Mbps). Ahora el búfer es de 256 KB y el host solo
  envía cuando el cliente confirma fotogramas (`FrameAck`), con una ventana calculada a partir del RTT base.
- El NVENC de FFmpeg reinicia el codificador con un IDR en cada cambio de bitrate, y el HEVC de Quick Sync falla al
  reiniciar ("incompatible video parameters"): de ahí la histéresis del control adaptativo.
- La entrada D3D11 de NVENC exige que la textura esté en la GPU NVIDIA (`OpenEncodeSessionEx: no encode device` si
  no); en portátiles híbridos el escritorio vive en la integrada, así que se comprueba el fabricante del adaptador.
- En portátiles híbridos (Intel + NVIDIA), `DuplicateOutput` devuelve `DXGI_ERROR_UNSUPPORTED` si el driver de NVIDIA
  (cargado por NVENC) se inicializa antes que DXGI; el host abre una captura al arrancar para evitarlo.
- `SWS_FAST_BILINEAR` emborrona el texto de 1 px al pasar BGRA→YUV; se usa `SWS_BILINEAR`.
- swscale no tiene ruta SIMD para YUV 4:4:4→RGB (~15 ms/frame); hay un conversor propio paralelo (~1 ms).
- Pantalla de bloqueo: la sesión 0 (servicios) no ve ninguna pantalla, así que el servicio duplica su token, le
  cambia el `TokenSessionId` y lanza `--agent` con `CreateProcessAsUser` en `winsta0\default`. Los hilos de captura y
  de entrada siguen al escritorio de entrada (`OpenInputDesktop` + `SetThreadDesktop`): `Default` ↔ `Winlogon`.
  `SendInput` no falla, simplemente no hace nada, si el hilo está en otro escritorio.
- Ctrl+Alt+Supr no se puede inyectar con `SendInput`: el agente señala el evento `Global\RemoteConnect.SAS` y el
  servicio llama a `SendSAS`.
- QUIC en exe único: el runtime marca `msquic.dll` como `DropFromSingleFile`, y sin ella `QuicListener.IsSupported`
  es `false` sin más explicación. El target `BundleMsQuic` del csproj la vuelve a meter en el bundle, pero
  `System.Net.Quic` la busca por ruta completa junto al exe; el constructor estático de `P2p` la resuelve en el evento
  `AssemblyLoadContext.ResolvingUnmanagedDll` (ni la precarga por nombre ni `SetDllImportResolver` sirven aquí).
- Hole punching + QUIC: msquic no acepta un socket ya abierto, así que se hace STUN y el envío de paquetes con un
  socket UDP propio, se cierra y QUIC se enlaza al mismo puerto; el mapeo del NAT sigue vivo.

## Licencia

[MIT](LICENSE) © 2026 kguttas: puedes usarlo, copiarlo, modificarlo y redistribuirlo (también con fines comerciales) siempre que conserves el aviso de copyright y la licencia. Se ofrece sin garantía.
