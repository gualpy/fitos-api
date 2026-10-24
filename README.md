# FitOS API (.NET 8)

Backend multi-tenant de FitOS. La facturación electrónica se delega a un servicio externo (SICOFAC) por API.

```
src/
  FitOS.Domain          Enums y reglas puras. Sin dependencias.
  FitOS.Application     Casos de uso y puertos (ITenantContext, IFiscalProvider). Depende de Domain.
  FitOS.Infrastructure  EF Core/Npgsql, RLS por tenant, cliente SICOFAC (HttpClient + resiliencia).
  FitOS.Api             ASP.NET Core: JWT, middleware de tenant, endpoints. Compone todo.
  FitOS.Migrator        Aplica los scripts SQL (DbUp). Dueño del esquema.
tests/
  FitOS.UnitTests         Sin I/O (proveedor SICOFAC con handler falso, mapeos).
  FitOS.IntegrationTests  API en memoria + PostgreSQL real (RLS) si hay FITOS_TEST_PG.
```
Dependencias: Api → Application, Infrastructure · Infrastructure → Application → Domain.

## Decisiones
- **Esquema SQL-first** (`src/FitOS.Migrator/Scripts/NNNN_*.sql`), no migraciones de EF: RLS, triggers y FKs
  compuestas por tenant viven en SQL. Un script por cambio, inmutable una vez aplicado.
- **Dos roles de BD:** el migrador corre como dueño; la API como `fitos_app` (sin BYPASSRLS). Jamás conectar la API como dueño.
- **Tenant** sale solo del claim `tenant_id` del JWT validado (`TenantMiddleware`). Sin tenant → 0 filas.
- **SICOFAC:** `IFiscalProvider`; la API key y el `company_id` de cada gimnasio se pasan por llamada (`FiscalAccount`).
  Reintentos del HTTP son seguros por `Idempotency-Key`.

## Comandos
```bash
docker compose up -d db
export POSTGRES_PASSWORD="$(openssl rand -base64 18)"            # y úsala en las cadenas de conexión
export FITOS_MIGRATE_CONNECTION="Host=localhost;Username=postgres;Password=$POSTGRES_PASSWORD;Database=fitos"
dotnet run --project src/FitOS.Migrator -- migrate
dotnet run --project src/FitOS.Migrator -- create-platform-admin <usuario> "Nombre"   # contraseña temporal (FITOS_ADMIN_PASSWORD o aleatoria, se muestra una vez)
psql ... -c "ALTER ROLE fitos_app LOGIN PASSWORD '...'"      # contraseña real, fuera del repo
./scripts/dev-secrets.sh                                     # genera claves aleatorias de desarrollo en user-secrets (fuera del repo)
# en producción, por variables de entorno / gestor de secretos: ConnectionStrings__Default, Jwt__SigningKey,
# Secrets__EncryptionKey (openssl rand -base64 32), Sicofac__BaseUrl, Tenancy__BaseDomain
dotnet run --project src/FitOS.Api

dotnet test                                                  # unitarias + integración sin BD
FITOS_TEST_PG="Host=localhost;Username=postgres;Password=$POSTGRES_PASSWORD;Database=postgres" dotnet test   # + pruebas de RLS
```
`FITOS_TEST_PG` debe ser un superusuario: cada corrida crea y borra su propia base temporal.

## Login (hecho)
`POST /auth/login {username, password}` → JWT de acceso (claims `sub`, `role`, `name`, `tenant_id`, `customer_id`). `GET /auth/me`.
- **Tenant por host:** `titan.fitos.com` → gym `titan`; `fitos.com` o `admin.fitos.com` → administradores de plataforma.
  En desarrollo se acepta la cabecera `X-Tenant-Slug` (`platform` para plataforma); la API se niega a arrancar con eso fuera de Development.
- **Funciones SQL `SECURITY DEFINER`** (`0002_auth.sql`): el login ocurre antes de conocer el tenant y RLS ocultaría `users`,
  así que la app solo puede ejecutar `auth_find_user / auth_register_*`, no leer `users`. Requiere que el rol de migraciones sea
  superusuario o miembro de `fitos_platform`.
- **Hash:** PBKDF2-HMAC-SHA512 con sal (hasher de ASP.NET Core Identity). Todas las fallas devuelven el mismo 401 tras trabajo comparable.
- **Bloqueo:** 5 fallos → 15 min (configurable); una cuenta bloqueada ni siquiera se verifica. **Límite:** 10 intentos/min por IP.

## Suscripciones, correo y panel de plataforma (hecho)
- **Vencimiento.** Cada gimnasio tiene `subscription_ends_on` (último día con servicio, inclusive). El alta lo deja en hoy + `Subscription:TrialDays` (30) con estado `trial`. Al pasar ese día el gimnasio deja de entrar **solo**, sin ningún proceso: el estado efectivo (`expired`) se calcula al iniciar sesión y al renovar. Sin días de gracia.
- **Login de un gimnasio vencido o suspendido:** `403 tenant_inactive`, solo con la contraseña correcta (con una incorrecta sigue siendo el `401` de siempre, para no revelar qué cuentas existen). Los datos se conservan; al renovar, el acceso vuelve solo.
- **Aviso.** `GET /subscription` (personal del gimnasio) → `{endsOn, daysLeft, showReminder}`; `showReminder` es `true` desde `Subscription:ReminderDays` (3) días antes.
- **Plataforma** (solo `platform_admin`): `GET /platform/tenants` (estado, días restantes, socios), `POST /platform/tenants/{id}/subscription {months, amount}` (suma meses desde el vencimiento, o desde hoy si ya venció, y guarda lo cobrado), `POST .../suspend` y `.../reactivate`, `POST .../resend-welcome`, `GET /platform/revenue` (suma por mes de lo cobrado a los gimnasios). Todo pasa por funciones `SECURITY DEFINER`: el rol de la API no lee `subscription_payments` ni otros gimnasios.
- **Alta de gimnasio:** pide `ownerEmail` y ya no devuelve contraseña temporal. El dueño recibe un correo con un enlace de un solo uso (vence en `Auth:SetupLinkHours`, 48 h) que apunta a `App:PublicUrl` con el token en el fragmento (`/#setup=...`, nunca llega a un log del servidor); solo se guarda su hash. El front lo canjea con `POST /auth/set-password {token, password}`. Si el correo falla, el gimnasio se crea igual (`welcomeEmailSent: false`) y se reenvía desde el panel.
- **Correo.** La configuración vive en la base de datos y se edita en el panel de plataforma (pestaña "Correo"): servidor, puerto, seguridad (`ssl_on_connect` = puerto 465, `start_tls` = 587), usuario, remitente y contraseña. La contraseña se guarda **cifrada** (AES-GCM con `Secrets:EncryptionKey`; un hash no serviría, el servidor SMTP necesita la real), no vuelve nunca por la API (solo `••••abcd`) y hay un botón de prueba. El envío usa MailKit.
  - **Orden:** primero lo guardado en la base; si no hay nada, o falla o no se puede descifrar, se reintenta UNA vez con las variables `Email__Host`, `Email__Port`, `Email__Security`, `Email__Username`, `Email__Password`, `Email__From` (respaldo, para que un error de configuración no bloquee el login de plataforma; el fallo queda en la bitácora de seguridad como `email_settings_fallback`). Si hay base pero la clave de cifrado cambió, hay que guardar la contraseña de nuevo.
  - **Rescate sin panel:** `FitOS.Migrator clear-email-settings` borra lo guardado y entran a regir las variables.
  - **Remitente:** el nombre visible es el del gimnasio (`Gimnasio Titan <no-reply@...>`); la dirección es siempre la del buzón configurado.
  - En Development, sin nada configurado, el mensaje (con su enlace) se escribe en el log de la API. Fuera de Development la API exige `App:PublicUrl` en https (los enlaces apuntan al front).

## Personal, acceso de socios y "olvidé mi contraseña" (hecho)
- **Login por correo o usuario.** El campo acepta las dos cosas: con `@` se busca por correo, si no por usuario (los usuarios no llevan `@`). El correo es **único por gimnasio** (índice `users_email_uq`, sin distinguir mayúsculas); el mismo correo puede existir en dos gimnasios. Los administradores de plataforma entran solo por usuario.
- **Equipo** (`/users`, dueño y administrador): `GET` lista, `POST {name, email, role}` invita, `PATCH /{id} {isActive?, role?}`, `POST /{id}/resend-invite`. El dueño crea administradores y recepcionistas; el administrador solo recepcionistas (`403 role_not_allowed`). Reglas: nadie se desactiva a sí mismo, no se puede desactivar al último propietario (`last_owner`), solo el dueño cambia roles y nunca el del propietario. Desactivar cierra las sesiones. Nadie recibe contraseña: la invitación es un enlace de un solo uso (48 h) y el usuario interno se genera solo a partir del correo (`carla`, `carla2`…); nadie tiene que conocerlo.
- **Acceso de socios** (`/customers/{id}/access`, recepción, administrador y dueño): `GET` estado, `POST {email?}` crea el login del socio (por defecto con el correo de su ficha) y le envía la invitación, `POST /resend`. Un socio solo ve lo suyo, como siempre.
- **Olvidé mi contraseña:** `POST /auth/forgot-password {identifier}` (con `X-Tenant-Slug` en el modo de un solo dominio). **Responde siempre `202`**, exista o no el gimnasio o la cuenta, y el correo sale **en segundo plano** (cola en proceso), así ni la respuesta ni su tiempo revelan qué cuentas existen. Solo cuentas activas, con correo, de un gimnasio vigente; los administradores de plataforma se restablecen con el Migrator. El enlace vence en `Auth:ResetLinkMinutes` (60) y no se emite otro dentro de `Auth:ResetMinGapSeconds` (60). Se canjea con `POST /auth/set-password`, que además cierra las sesiones abiertas y limpia el bloqueo. Lo escrito en el campo no se registra (la gente a veces teclea ahí su clave).
- **Correos a nombre del gimnasio:** remitente `Gimnasio Titan <no-reply@…>`, asunto y texto con su nombre. `POST /auth/setup-info` devuelve gimnasio, nombre y propósito (`welcome` o `reset`) para la página del enlace.

## Confirmación por correo del login de plataforma (hecho)
Un administrador de plataforma puede todo, así que la contraseña sola no le da sesión:
1. `POST /auth/login` con usuario y contraseña correctos → `202 {status: "confirmation_required", challengeSecret, expiresAt}` y **ningún token**. Un correo con un enlace de un solo uso (vence en `Auth:ConfirmationMinutes`, 10; trae IP y navegador de quien lo pidió) sale al correo guardado en la cuenta.
2. El enlace (`{App:PublicUrl}/#confirm=...`) **no basta**: `POST /auth/confirm-login {token, secret}` necesita además el `challengeSecret` que recibió el navegador que hizo el login (el front lo guarda en ese navegador). Abrir el correo en otro dispositivo, o que alguien lo lea, no inicia sesión.
3. Un intento con el secreto equivocado **quema** el enlace; un login nuevo anula el anterior; solo se guardan hashes. Con la contraseña mal no se envía nada.
- Sin correo en la cuenta, el login responde `403 confirmation_unavailable`. Se asigna con el Migrator: `set-platform-email <usuario> <correo>`, o `FITOS_ADMIN_EMAIL` al hacer `create-platform-admin`. El correo vive en la base, nunca en el código ni en el repo.
- **Contraseña perdida:** "¿Olvidaste tu contraseña?" excluye a los administradores de plataforma a propósito. Con acceso al servidor: `FitOS.Migrator reset-platform-password <usuario>` (la contraseña sale de `FITOS_ADMIN_PASSWORD`, o se genera una y se muestra una sola vez). Debe cambiarse en el próximo ingreso, cierra todas las sesiones, levanta un bloqueo por intentos y anula un enlace de confirmación pendiente; queda registrado como `platform_password_reset`.
- `Auth:SkipPlatformConfirmation=true` apaga el paso, **solo en Development**: en cualquier otro entorno se ignora.

## Un solo dominio en producción (`AllowLoginTenantHeader`)
Por defecto el gimnasio del login sale del subdominio (`titan.fitos.com`). Para servir todo desde **un solo dominio**, activa `Tenancy:AllowLoginTenantHeader`: solo en `POST /auth/login` se acepta el gimnasio en la cabecera `X-Tenant-Slug` (mismas reglas que un subdominio: slug bien formado y no reservado). En cualquier otro endpoint la cabecera se ignora y el tenant sale del claim `tenant_id` del JWT, como siempre. Sin cabecera, el login sigue resolviéndose por host (el dominio base es la plataforma).
`Tenancy:AllowTenantHeader` es distinta: solo para Development y la API se niega a arrancar con ella fuera de Development. Si ambas están activas, la de Development manda en todos los endpoints.
CORS: añade el origen del dominio único a `Cors:AllowedOrigins`; `X-Tenant-Slug` ya está en las cabeceras permitidas.
```json
// appsettings.Production.json
{
  "Tenancy": { "BaseDomain": "fitos.support-me.net", "AllowTenantHeader": false, "AllowLoginTenantHeader": true },
  "Cors": { "AllowedOrigins": [ "https://fitos.support-me.net" ] }
}
```

## Alta de gimnasios (hecho)
Solo administradores de plataforma (`role=platform_admin`, sin `tenant_id`). Orden operativo:
1. **En SICOFAC (manual):** crear la empresa y que su dueño genere la API key (SICOFAC no permite crear empresas por API).
2. **En FitOS:** `POST /platform/tenants` con slug, RUC (validado: cédula/RUC ecuatoriano con dígito verificador), datos fiscales,
   `providerCompanyId` + `providerApiKey` y el usuario del owner. Una sola transacción: tenant, ajustes, config fiscal,
   sede Matriz (establecimiento 001), punto de emisión, owner y auditoría. Devuelve la **contraseña temporal una sola vez** (`Cache-Control: no-store`).
3. `POST /platform/tenants/{id}/fiscal/test-connection`: consulta una referencia inexistente en SICOFAC; `ok` = la key sirve para esa empresa
   (no crea nada). Otros estados: `invalid_api_key`, `wrong_company`, `company_not_found`, `unreachable`, `not_configured`.
- La API key se guarda **cifrada** (AES-256-GCM, ligada al tenant; `Secrets:EncryptionKey`) con una pista de 4 caracteres; la auditoría no lleva secretos.
- RLS sigue activo: la transacción fija `app.tenant_id` al tenant NUEVO solo durante esa transacción.
- Slug: formato DNS y sin reservados (`www`, `admin`, `api`, `platform`), también con un CHECK en la base. Conflictos: `slug_taken`, `ruc_taken` (409).
- El owner (y los admins de plataforma creados por CLI) entran con contraseña temporal: hasta cambiarla con `POST /auth/change-password`
  solo pueden usar `/auth/*` (claim `pwd_change`); el resto devuelve 403.

## Clientes, planes y membresías (hecho)
Todo va por el token: el gimnasio sale del claim `tenant_id`, nunca de la URL. Roles: `owner`/`admin` (gestión), `reception` (operación), `customer` (solo lo suyo).

| Endpoint | Quién |
|---|---|
| `GET /plans`, `GET /plans/{id}` | cualquier miembro del gimnasio (el cliente solo ve planes activos) |
| `POST /plans`, `PUT /plans/{id}`, `PUT /plans/{id}/active` | owner, admin |
| `GET/POST /customers`, `PUT /customers/{id}` | owner, admin, reception |
| `GET /customers/{id}` | staff, o el propio cliente |
| `PUT /customers/{id}/status` | owner, admin |
| `GET /customers/{id}/memberships` | staff, o el propio cliente |
| `POST /customers/{id}/memberships` | owner, admin, reception |
| `POST /memberships/{id}/cancel` | owner, admin |

- `GET /customers?q=&status=&page=&pageSize=` busca por nombre, documento, correo o código; `%` y `_` se toman literales.
- **Membresías sin solapamiento:** si el socio ya está cubierto, la nueva se programa justo después. Además lo garantiza la base (restricción de exclusión), incluso ante peticiones simultáneas.
- Fechas de fin inclusivas y por meses de calendario (un mes que empieza el 31 termina antes: 31 oct → 29 nov). "Hoy" se calcula en la zona horaria del gimnasio.
- Un id de otro gimnasio responde 404, igual que uno inexistente. Cédula/RUC se validan con dígito verificador.

## Asistencia (hecho)
| Endpoint | Quién |
|---|---|
| `POST /attendance/check-in` (`customerId` o `clientCode` o `rfidCard`; `branchId` si hay varias sedes) | owner, admin, reception |
| `POST /attendance/check-out` | owner, admin, reception |
| `GET /attendance?from=&to=&customerId=&result=&page=&pageSize=` | owner, admin, reception |
| `GET /customers/{id}/attendance?from=&to=&result=&page=&pageSize=` | staff, o el propio socio |

- **Reglas de entrada** (`AccessPolicy`, función pura): socio inactivo → `customer_inactive`; día cerrado → `closed`; fuera de horario → `outside_hours` (abre incluido, cierra excluido); sin membresía vigente → `no_active_membership`, salvo que el gimnasio permita entrar sin plan (`tenant_settings.allow_without_plan`). Sin horarios configurados, la hora no restringe.
- Toda lectura se **registra**, también las denegadas (con su motivo). Por eso `check-in` responde 201 en ambos casos; el campo `result` dice `granted` o `denied`.
- **Una sola entrada abierta por socio**, garantizada por la base: una segunda lectura responde 409 `already_inside` (también si dos llegan a la vez). Una entrada olvidada de más de 24 h se cierra de forma visible (`autoClosed`, salida = entrada) para no inventar horas.
- Fechas y horarios se interpretan en la zona horaria del gimnasio: un filtro `from`/`to` cubre días locales completos.

## Dashboard (hecho)
| Endpoint | Quién |
|---|---|
| `GET /dashboard/summary` | owner, admin, reception (reception **no** recibe cifras de dinero: `sales` viene `null`) |
| `GET /dashboard/insights` | owner, admin |

- **Resumen:** socios registrados, socios con plan vigente, planes que vencen en 7 días sin renovación agendada, asistencia de hoy (socios distintos), quiénes siguen dentro, socios activos por género, asistencia de los últimos 7 días y ventas pagadas (hoy y 7 días). Todo se calcula en SQL, con el calendario del gimnasio (no UTC) y bajo RLS: cada consulta ya queda dentro del gimnasio del token.
- **Insights con IA** (opcional): la API llama a Gemini; la clave **solo existe en el servidor** (`Gemini:ApiKey`, por user-secrets o variable de entorno `Gemini__ApiKey`, nunca en el repositorio ni en el navegador). Sin clave responde 503 `insights_unavailable`; si el proveedor falla, 502 `insights_failed`. Al proveedor solo viajan **cifras agregadas** (sin nombres ni datos personales). La respuesta se guarda en memoria por gimnasio (`Gemini:CacheHours`, 6 por defecto) y es el usuario quien la pide con un botón.

## Ajustes, productos y ventas (hecho)
| Endpoint | Quién |
|---|---|
| `GET /settings` | owner, admin, reception |
| `PUT /settings` (`allowWithoutPlan` y, opcional, los 7 días de `hours`) | owner, admin |
| `GET /products?q=&includeInactive=` | owner, admin, reception |
| `POST /products`, `PUT /products/{id}`, `PUT /products/{id}/active` | owner, admin |
| `POST /products/{id}/stock` (`delta`, `reason`: `purchase` o `adjustment`) | owner, admin |
| `POST /sales` (`customerId?`, `lines[{productId, quantity}]`, `payments[{method, amount}]`) | owner, admin, reception |

- **Ajustes:** el interruptor "entrar sin plan" y el horario semanal de la sede (lunes = 1). Los días que el gimnasio nunca guardó vienen con `configured: false` y no restringen el acceso. `PUT` exige los siete días, `HH:mm`, y que un día abierto cierre después de abrir; todo se guarda en una transacción.
- **Productos:** precio sin IVA (0, 5 o 15 %); nombre, código y código de barras únicos por gimnasio (409 `product_name_taken`, `sku_taken`, `barcode_taken`). Se desactivan, no se borran. **El stock solo cambia con movimientos** (`stock_movements`): venta, compra o ajuste, y nunca queda bajo cero (409 `insufficient_stock`).
- **Ventas:** el cliente solo dice qué producto y cuántos; **precios, IVA y totales los calcula el servidor** desde el catálogo (IVA sobre la suma de cada tarifa, como en la factura). Venta, líneas, pagos, descuento de stock y movimientos se escriben **en una transacción**: si falta stock, no queda nada a medias, y dos cajeros no pueden vender la última unidad. Los pagos deben sumar el total (409 `payment_mismatch`). El código SRI de cada forma de pago sale de la configuración `Sales:PaymentCodes`, no del código. Sin socio es consumidor final. Todavía no hay anulaciones ni notas de crédito.

## Membresías cobradas (hecho)
`POST /customers/{id}/memberships` (`planId`, `startsOn?`, `payments?`):

- **Con `payments`** (owner, admin, reception): el plan se cobra. En **una transacción** se escriben la venta (una línea de plan), sus pagos y la membresía enlazada por `memberships.sale_id`. El precio y el IVA salen del plan, nunca de la petición; los pagos deben sumar el total al centavo (409 `payment_mismatch`). Si dos peticiones chocan por los mismos días, una responde 409 `membership_overlap` y no queda ninguna venta suelta.
- **Sin `payments`** es una cortesía y **solo owner y admin** pueden darla (recepción recibe 403: si no, podría regalar planes sin dejar rastro). Una lista `payments` vacía es un error de validación, no una cortesía.
- Mismas reglas que antes: socio y plan activos, fecha no pasada, y una renovación se encola al terminar la membresía vigente. La respuesta incluye `saleId` y los totales.

## Peso y estatura del socio (hecho)

- `GET|POST /customers/{id}/measurements`, `DELETE /customers/{id}/measurements/{measurementId}`: historial de peso (kg) y estatura (cm) para la gráfica de evolución. Una medida por día: registrar de nuevo el mismo día la reemplaza; no se aceptan fechas futuras.
- Un socio solo lee y escribe lo suyo (el de otro se ve como inexistente); el personal del gimnasio lo hace para cualquier socio del gimnasio. Tabla `body_measurements` con RLS (migración 0009).
- La API solo guarda peso y estatura. El IMC y los rangos se calculan en el front.

## Base de datos de desarrollo
Una base propia (`fitos-dev`) en el PostgreSQL local, creada con `migrate` (ver Comandos). La API se conecta como `fitos_app` con la cadena en user-secrets (`ConnectionStrings:Default`); la contraseña del administrador solo se usa para migrar y nunca se guarda en el repositorio.

## Siembra de desarrollo
Datos de prueba **en la base**, nunca en el código de la app. Solo para bases de desarrollo (exige confirmación explícita):
```bash
export FITOS_MIGRATE_CONNECTION="Host=localhost;Username=postgres;Password=$POSTGRES_PASSWORD;Database=fitos"
FITOS_ALLOW_DEV_SEED=1 FITOS_SEED_PASSWORD='<10+ caracteres, letra y número>' \
  dotnet run --project src/FitOS.Migrator -- seed-dev <slug-del-gimnasio> <usuario> "<Nombre Apellido>" [días=60]
```
Crea (o reutiliza) un socio con usuario real, membresías seguidas que cubren los últimos *N* días y una asistencia por cada día que el gimnasio estuvo abierto según sus horarios (si no hay, escribe lun-vie 06-22, sáb 08-18, dom cerrado). Se puede repetir sin duplicar.

## Hoja de ruta
- Listar, consultar y suspender gimnasios (panel de plataforma).
- Anulaciones y notas de crédito de ventas → control de acceso.
- Facturación: worker de reintentos (outbox) y descarga/envío del comprobante.
- Dockerfile (necesita `tzdata` para las zonas horarias) y CI.

## Sesiones y seguridad (hecho)
**Sesiones revocables.** El token de acceso dura 15 minutos (`Jwt:AccessTokenMinutes`) y no se puede revocar; por eso es corto. Para no pedir la contraseña
a cada rato hay un **token de renovación** opaco (256 bits) que **rota en cada uso**:
- `POST /auth/refresh {refreshToken}` devuelve un par nuevo. Inactividad máxima 8 h (`Auth:IdleMinutes`) y vida absoluta 12 h (`Auth:SessionHours`).
- Si un token ya usado vuelve a presentarse (copiado), **se revoca toda la sesión** (`refresh_token_reuse` en el registro de eventos). Dos peticiones simultáneas con el mismo token
  no cuentan como robo: la segunda recibe 409 `refresh_in_progress`.
- `POST /auth/logout` revoca exactamente esa sesión; **cambiar la contraseña cierra todas** las sesiones del usuario; un usuario desactivado o un gimnasio suspendido
  no pueden renovar.
- En la base solo se guarda el SHA-256 del token y las tablas solo se tocan mediante funciones `SECURITY DEFINER`: el rol de la API no puede leerlas.

**Contraseñas.** PBKDF2-HMAC-SHA512 con 210 000 iteraciones (`Auth:PasswordIterations`). Los hashes antiguos siguen funcionando y se **reemplazan por uno más fuerte en el
siguiente login correcto** (sin cerrar sesiones). Un hash corrupto en la base rechaza el login, no lo rompe.

**Registro de eventos de seguridad** (`security_events`): `login_success`, `login_failed` (con motivo), `account_locked`, `password_changed`, `password_change_failed`,
`refresh_token_reuse`, `logout`, con IP y navegador. Nunca contiene contraseñas ni el nombre de usuario que se tecleó. La API puede **añadir** eventos pero no leerlos ni
borrarlos; si el registro falla, el login sigue.

**Borde de la API.**
- Cabeceras defensivas en toda respuesta (`nosniff`, `X-Frame-Options: DENY`, `Referrer-Policy: no-referrer`, CSP `default-src 'none'`, `Cache-Control: no-store`), HSTS fuera de desarrollo y sin cabecera `Server`.
- **Detrás de un proxy hay que declararlo:** `Proxy:KnownProxies` (IPs) y/o `Proxy:KnownNetworks` (CIDR). Solo entonces se creen `X-Forwarded-For/Proto/Host`; si no, un atacante podría
  falsear su IP (saltarse el límite) o el host (hacerse pasar por otro gimnasio). Valores inválidos impiden arrancar.
- `Security:RequireHttps` redirige HTTP→HTTPS (308). Actívalo solo con el proxy declarado, o provocará un bucle.
- Límite de tamaño de cuerpo (`Security:MaxBodyBytes`, 1 MB) y de peticiones: por usuario autenticado (600/min) o, sin sesión, por IP (120/min); `/health` nunca se limita.
- CORS solo para los orígenes de `Cors:AllowedOrigins`; sin comodines.

**Reglas de oro:** nunca subas secretos (claves JWT/cifrado, cadenas con contraseña, certificados, `.env`); las claves de desarrollo viven en user-secrets (`scripts/dev-secrets.sh`).
Para reportar una vulnerabilidad, abre un aviso privado de seguridad en GitHub (Security → Report a vulnerability) en lugar de un issue público.
