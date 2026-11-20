# Kryuger

**Español** · [English](README.en.md) · [中文](README.zh.md) · [日本語](README.ja.md)

Servidor privado para **Girls' Frontline** (cliente EN de Steam), escrito en C# (.NET 8) con base de datos LiteDB. Reproduce la lógica del servidor oficial a partir de las tablas del juego: los cambios se guardan, las misiones se ganan con sus objetivos reales y los enemigos actúan en su turno.

> Proyecto sin ánimo de lucro con fines educativos y de preservación. No está afiliado a SunBorn ni a ninguna distribuidora del juego. No incluye archivos del juego: necesitas tu propio cliente instalado.

## Cómo se usa: Kryuger GM Tools

**El servidor se usa a través de Kryuger GM Tools** (carpeta `gm-tools`). No hace falta compilar nada a mano:

1. Abre `kryuger-gm-tools.exe`.
2. Pulsa **Iniciar**. La primera vez descarga este repositorio, instala un .NET 8 SDK privado si el PC no lo tiene y compila el servidor. Después solo comprueba si hay un commit nuevo en `main`; si lo hay, se actualiza solo.
3. Pulsa **Jugar** (o abre Girls' Frontline desde Steam).
4. Al pulsar **Detener** o cerrar la aplicación se retira la redirección y el juego vuelve a conectar al servidor oficial.

Todo el servidor se guarda en `%LOCALAPPDATA%\Kryuger`: el código en `project`, el .NET privado en `dotnet` y los datos de los jugadores en `data`, separados del código que se actualiza.

### Qué hace Kryuger GM Tools

Disponible en español, inglés, chino y japonés.

- **Servidor:** iniciar, detener, buscar actualizaciones, abrir el juego, registro en vivo y estado de la instalación.
- **Centro de control:** resumen de cuentas y del catálogo.
- **Cuentas:** buscar por UID o nombre y ver recursos, gemas, muñecas, equipo y hadas.
- **Operaciones:** entregar muñecas, objetos, equipo, hadas, skins, gemas o recursos con un buscador del catálogo, y ajustar gemas, recursos, nivel o nombre. Funciona también con el jugador conectado.

La API GM del servidor (`/gm`) solo responde a `127.0.0.1` y exige el token de `KRYUGER_GM_TOKEN.txt`, que el servidor genera al arrancar. Cada cambio queda registrado en `gm_audit.log`.

## Requisitos

- Windows 10/11.
- Girls' Frontline EN instalado desde Steam (la carpeta se detecta sola).
- Conexión a internet la primera vez (descarga del servidor y de .NET) y para el inicio de sesión de Steam.

## Cómo funciona

```
src/
  Kryuger.Common/   cifrado AuthCode (RC4 + MD5 + gzip), base de datos LiteDB y modelos
  Kryuger.Data/     carga de las tablas del juego y fórmulas de construcción
  Kryuger.Server/   servidor ASP.NET Core: API del juego, lógica de combate y API GM
  Kryuger.Tests/    pruebas de cifrado y de persistencia
  gfl_redirect/     version.dll (Rust) que redirige el juego al servidor local
runtime/            version.dll ya compilado y plantilla de inicio de sesión
data/gf-data-us/    tablas del juego que usa el servidor
gm-tools/           Kryuger GM Tools (Tauri 2 + Astro + SolidJS)
```

El servidor escucha en `127.0.0.1` (puertos 5000, 80 y 443). Mientras está encendido coloca `version.dll` en la carpeta del juego para redirigir el tráfico; al apagarse lo retira.

## Hoja de ruta

### Funciona

**Cuenta y sesión**
- Inicio de sesión con Steam, cifrado AuthCode y respuestas con el formato oficial.
- Nombre, presentación, ayudantes, ajustes del juego, tutorial y skin de Kalina.
- Reanudar una misión en curso al volver a entrar.

**Fábrica y muñecas**
- Construcción de muñecas (normal, múltiple y rápida), de equipo y de hadas.
- Escuadrones, formación, mejora, enlaces, retiro, bloqueo, reparación con la vida máxima real, entrenamiento de habilidades, neuromejora, juramento y skins.
- Equipo y hadas: equipar, mejorar, retirar y bloquear.

**Combate**
- Inicio de misión, movimiento, combate, reabastecimiento, fin de turno y abandono, con todos los campos de la respuesta oficial.
- Experiencia, nivel, vida restante, rango y afinidad tras cada combate.
- Condiciones de victoria reales: cuartel general, eliminar a todos, eliminar a N, derrotar a un enemigo concreto, llegar a un punto, tomar puestos de mando, sobrevivir N rondas, límite de turnos, rescatar rehenes y objetivos de edificios.
- Turno enemigo con IA según las tablas (quietos, patrulla, alerta, expansión, persecución y avance al puesto de mando) y derrota si se pierde el puesto de mando.
- Rehenes (liberar, escoltar y rescatar) y jefes con vida persistente.
- Aliados y unidades de terceros con su IA, traiciones y transformaciones.
- Edificios: cambio de dueño, destrucción y defensa.
- Misiones nocturnas con niebla: solo se ve lo que alcanzan los escuadrones.
- Desbloqueo natural de misiones, capítulos, campañas y simulacros.

**Progreso y economía**
- Logística con duración y recompensas reales, y operaciones automáticas.
- Misiones diarias y semanales, y logros con progreso real.
- Correo (lista, cobrar, cobrar todo y borrar), tienda y pase de batalla.

### Pendiente

- Efectos activos de los edificios (misiles, torres y radar que atacan casillas).
- Combates entre aliados y enemigos.
- Confirmar con capturas oficiales el aviso de traición o transformación de aliados y el parámetro de IA calculado a partir del mapa.
- Dormitorio completo (muebles, visitas y batería) y amigos.
- Eventos, Teatro, ajedrez, equipos HOC, Sangvis, vehículos y exploración.
- Pantalla de mejora: el formato de `Gun/eatGunIndex` no está confirmado.

## Compilar a mano (opcional)

Servidor ([.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)):

```powershell
dotnet run --project src/Kryuger.Server
```

GM Tools ([Node.js](https://nodejs.org) 20+ y [Rust](https://rustup.rs)):

```powershell
cd gm-tools
npm install
npx tauri build --no-bundle
```

El ejecutable queda en `gm-tools\src-tauri\target\release\kryuger-gm-tools.exe`.
