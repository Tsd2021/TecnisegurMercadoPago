# Pendientes — retomar acá

**Cerrado el:** 31 de julio de 2026
**Estado general:** la API está en producción y operando con credenciales reales
de TECNISEGURURUGUAY. El circuito de suscripciones está probado de punta a punta.
El de pagos únicos (Checkout Pro) está implementado pero **nunca se ejecutó**.

> Lo que sigue está ordenado por urgencia. Los tres primeros bloquean el uso real.

---

## 0. BLOQUEANTE — el alta de suscripciones está fallando en producción (31/07)

`POST /preapproval` devuelve **500** `{"message":"Internal server error"}` con
este payload, sacado del log del servidor:

```json
{"reason":"TECNISEGUR ALARMAS - MARIO CORTEZ","external_reference":"COT-31",
 "payer_email":"NOTIENE@NOTIENE.COM",
 "back_url":"https://www.tecnisegur.com.uy?cotizacion=31","status":"pending",
 "auto_recurring":{"frequency":1,"frequency_type":"months",
   "transaction_amount":1870.00,"currency_id":"UYU",
   "start_date":"2026-08-01T12:00:00.000-03:00",
   "end_date":"2026-09-30T16:44:59.255-03:00"}}
```

Sospechosos, en orden:

1. **`payer_email` = `NOTIENE@NOTIENE.COM`** — el relleno que pone EmpleadoWeb
   cuando la cotización no tiene `Correo`. Dominio inexistente.
2. **`back_url` sin barra antes del `?`** — válido según la RFC, rechazado por
   muchos validadores.

Para aislarlo: `Herramientas/DiagnosticarAltaSuscripcion.ps1 -Pedir`. Manda el
payload que falló y después una variante por campo; la primera que devuelva 201
señala al culpable. Crea los preapproval en `pending` (no cobran nada) y los
cancela solo.

### Dos arreglos que corresponden igual, salga lo que salga

- **Validar el correo en `SuscripcionServicio.CrearAsync`.** Una cotización sin
  `Correo` no debería poder generar una suscripción: el `payer_email` es la
  cuenta contra la que se asocia, y todos los avisos de MercadoPago (cobro,
  rechazo, cancelación) irían a un buzón inexistente. Que el link se mande por
  WhatsApp no lo salva. Devolver 400 con mensaje claro en vez de dejar que
  MercadoPago conteste un 500 indescifrable.

- **`end_date` se calcula desde hoy, no desde `start_date`**
  (`SuscripcionServicio.cs:100`):

  ```csharp
  EndDate = solicitud.PlazoMeses.HasValue
      ? DateTimeOffset.Now.AddMonths(solicitud.PlazoMeses.Value)   // ← mal
  ```

  Con `PlazoMeses = 2` y adhesión el 01/08, la suscripción termina el 30/09: dos
  meses contados desde el 31/07. El desfasaje crece cuanto más lejos esté la
  fecha de adhesión. Debería ser `(fechaInicio ?? DateTimeOffset.Now)`.

---

## 1. Desplegar lo que quedó compilado y sin subir

### 1.1 API

> **Publicada el 31/07.** Incluye la captura de liberación, comisión y
> retenciones, y el repaso periódico. La base ya tiene el DDL correspondiente
> (`10_EstadoLiberacionInformado.sql`, ejecutado el 31/07).

```powershell
$sitio = "C:\inetpub\wwwroot\TecnisegurMP Api"
$pool  = "TecnisegurMercadoPago"

Stop-WebAppPool -Name $pool
Start-Sleep -Seconds 3
robocopy "<origen>\publish" $sitio /E /XF web.config
Start-WebAppPool -Name $pool

curl.exe https://mpapi.tecnisegur.com.uy/health
```

⚠️ El `/XF web.config` es lo único que protege los secretos del servidor.

### 1.2 EmpleadoWeb

Acumula varios cambios sin publicar:

- Botón único **Pagos** en el listado (reemplaza los dos anteriores)
- Diálogo con correo y días de prueba **obligatorios**, teléfono precargado
- Columna **Cobro** con estado y enlace **Cancelar**
- Reversión del disparo automático al guardar
- Mensajes de error que muestran el `detalle` de MercadoPago

---

## 2. Hueco conocido: los pagos únicos no se pueden cancelar

**Es el pendiente funcional más importante.**

El índice `UX_PagoUnico_PendientePorCotizacion` impide dos pagos pendientes para
la misma cotización. Si se genera el link del equipamiento y el cliente nunca
paga, **no se puede generar otro link para esa cotización nunca más**: queda
trabado sin salida por la interfaz.

Solución acordada, pendiente de implementar:

- `POST /api/pagos/{id}/cancelar` que marque la fila local como `cancelled` y
  libere el índice.
- Botón equivalente al de suscripciones en la columna **Cobro**.

Del lado de MercadoPago no hay nada que dar de baja: una preferencia no es un
cobro hasta que alguien paga. Y si el cliente igual pagara con el link viejo, el
webhook lo registraría como `approved`, que es lo correcto — la plata entró.

---

## 3. Probar el Checkout Pro por primera vez

Todo el circuito de pagos únicos está implementado y desplegado, pero **nunca se
creó una preferencia real**. Falta:

- Generar un link de equipamiento desde el botón *Pagos*
- Verificar que llegue el webhook `payment` y escriba en `PagoUnico`
- Confirmar con `Database/02_VerificacionWebhook.sql`

Recordatorio: la aplicación de MercadoPago es de tipo Suscripciones, así que el
único evento de pagos disponible es **"Pagos (legacy)"**, ya tildado. Se verificó
que igual se entrega en formato moderno (JSON con `type: "payment"`).

⚠️ Son credenciales productivas: el primer link cobra dinero real. Usar una
cotización controlada, importe mínimo, y avisar a quien reciba el link.

---

## 3 bis. Liberación del dinero — lo que quedó abierto (31/07)

### 3bis.1 El pool de IIS no está configurado para hospedar el repaso

`ProcesadorNotificaciones` es un `BackgroundService`: **corre dentro del proceso
de la API**, no es un job. Con los valores por defecto de IIS —Idle Time-out 20
min, reciclado cada 29 h— el proceso se apaga por inactividad y el repaso se
apaga con él.

El campo `_proximoRepasoLiberacion` vive en memoria e inicia en `MinValue`, así
que cada arranque dispara un repaso. Eso amortigua, pero deja una forma
incómoda: **el día que no hay cobros nadie despierta el pool y el repaso no
corre**, que es justo el día en que querés enterarte de una liberación o una
reversión.

```
appcmd set apppool "TecnisegurMercadoPago" /startMode:AlwaysRunning
appcmd set apppool "TecnisegurMercadoPago" /processModel.idleTimeout:00:00:00
appcmd set site "TecnisegurMP Api" /applicationDefaults.preloadEnabled:true
```

Falta además documentarlo en `DEPLOY.md` §2.1, que hoy no lo menciona.

### 3bis.2 Un contracargo tardío sobre una cuota no lo detecta nadie

La ventana de gracia del repaso es de 10 días: pasada la liberación, la fila sale
del conjunto y no se vuelve a consultar. Los contracargos en Uruguay pueden
llegar hasta ~120 días.

Y la notificación `payment` que MercadoPago mande por ese contracargo **se
descarta**: `PagoServicio.cs:161-169` hace `return` sin escribir nada si el pago
tiene `preapproval_id`. El descarte es correcto para lo que fue escrito —evitar
contar el mismo cobro en `SuscripcionPago` y en `PagoUnico`— pero tira también
los cambios de estado posteriores.

> **Sin verificar:** si MercadoPago manda un `subscription_authorized_payment`
> nuevo al revertirse una cuota, el caso se cubre solo. De eso depende que esto
> sea un bug vivo o un hueco teórico.

Arreglo propuesto: que la rama `payment`, cuando el pago tenga `preapproval_id`,
en vez de `return` actualice `SuscripcionPago` por `MpPaymentId` — sólo estado y
datos de liberación, sin insertar fila ni tocar importes. No hay doble conteo
porque no inserta nada.

El pago único no tiene el problema: su notificación `payment` sí se procesa.

### 3bis.3 El repaso pregunta desde el día 0

Consulta cada cuota todos los días aunque la fecha prevista sea el día 21: ~20
llamadas por cuota que sabemos que van a decir `pending`. Con 100 suscripciones
activas satura el tope de 100 por corrida sin necesidad.

Arreglo: una condición más en el `WHERE` para no consultar antes de la fecha
prevista, salvo que falten datos. Baja de ~21 llamadas por cuota a 1 o 2.

### 3bis.4 Del lado del módulo TSD

- `LiberacionConfirmada` no la selecciona nadie todavía
  (`ServicioCobranzasMercadoPago.cs`, líneas 252 y 279). Sin ella, "confirmado
  por MercadoPago" y "deducido del almanaque" se muestran igual, en la misma
  celda verde.
- `ProximaLiberacion` (líneas 70-76) sigue filtrando por `FechaLiberacion >
  GETDATE()` puro. Un cobro con fecha vencida pero `pending` en MercadoPago suma
  en *MontoALiberar* y no aparece en *Próxima liberación*: la pantalla dice que
  hay plata por liberar sin decir cuándo.

### 3bis.5 El reporte de Liberaciones sigue sin consumirse

`money_release_status` confirma que MercadoPago liberó, no que el importe se
acreditó en el banco. Para conciliar contra el extracto hace falta el reporte
(`/v1/account/release_report`), hoy sólo accesible por
`Herramientas/ReporteLiberaciones.ps1`.

---

## 4. Seguridad — `CotizacionAlarmaController` no valida permisos

La lista de usuarios habilitados está **sólo en la vista**
(`Views/Menus/Menus.cshtml:39`, `idsPermitidos`). Eso esconde el botón del menú,
pero **no protege nada**: el controller no tiene `[Authorize]` ni verificación de
`NUMERO`, y la única barrera es `VerificarSession`, que sólo exige estar logueado.

Cualquier empleado con sesión válida que escriba la URL a mano puede llamar a
`GenerarLinkSuscripcion` y **crear un cobro real** contra un cliente.

Solución propuesta: un filtro sobre el controller que valide el `NUMERO` contra
la misma lista, movida a `Web.config` para no duplicarla entre vista y filtro.

---

## 5. Definiciones de negocio (siguen abiertas)

Ya no son teóricas: el sistema cobra dinero real.

- **Morosidad.** MercadoPago reintenta una cuota rechazada hasta 4 veces en 10
  días y tras **3 cuotas consecutivas rechazadas cancela la suscripción sola**,
  avisando sólo por mail al vendedor. Para un servicio de monitoreo de alarma
  eso significa que un cliente puede quedar sin cobertura contratada sin que
  nadie en Tecnisegur se entere. **Es la más urgente.**
- **Edición de cotización con suscripción viva.** ¿El cambio de importe se
  propaga con `PUT /api/suscripciones/{id}/monto` o se bloquea la edición?
- **Borrado de cotizaciones.** La FK impide eliminar una cotización con
  suscripción. ¿Se deja fallar con mejor mensaje o se permite?
- **`FechaAutorizacion`.** Hoy guarda cuándo el sistema detectó la autorización,
  no cuándo ocurrió. MercadoPago no expone un timestamp explícito.

---

## 6. Higiene y deudas menores

- **Suscripciones abandonadas.** Toda cotización cuyo link se genere y nunca se
  autorice deja una fila en `pending`. Conviene revisarlas periódicamente:

  ```sql
  SELECT IdSuscripcion, IdCotizacion, NombreCliente, MontoMensual, FechaCreacion
  FROM dbo.vw_SuscripcionesEstado
  WHERE Estado = 'pending' AND FechaCreacion < DATEADD(DAY, -7, GETDATE());
  ```

  Se habló de un proceso que las cancele solas pasados X días. Sin implementar.

- **Rotar el Auth Token de Twilio.** Quedó escrito en el historial de la
  conversación del 29/07. Si ese registro se comparte o exporta, conviene
  regenerarlo desde la consola de Twilio.

- **Rotar las credenciales de MercadoPago.** Dos exposiciones el 31/07:
  el **Access Token de producción** quedó en el historial de PowerShell
  (`ConsoleHost_history.txt`) al pasarse por línea de comando, y el **token de
  credenciales de prueba** estaba escrito dentro de `.mcp.json` —además mal, con
  el valor puesto donde va el nombre de la variable, así que nunca resolvió—.
  El archivo ya quedó corregido a `${MERCADOPAGO_MCP_TOKEN}`.

  Orden para rotar sin cortar el servicio: renovar en el panel → actualizar
  `MercadoPago__AccessToken` en el `web.config` del servidor (guardar recicla el
  pool solo) → verificar `/health` y una llamada contra MercadoPago → limpiar
  `(Get-PSReadlineOption).HistorySavePath`. **No tocar el `WebhookSecret`**: es
  independiente, y rotarlo sin querer deja todas las notificaciones en
  `FirmaValida = 0` sin ningún síntoma visible.

- **`ModoSandbox` es configuración muerta.** Está declarada en
  `MercadoPagoOpciones.cs` pero no la lee nadie. Borrarla o implementarla; hoy
  confunde, porque sugiere una protección que no existe.

- **`ObtenerBloqueoActivoPorIP` está roto** (EmpleadoWeb). Recibe una IP y se la
  manda al procedimiento como `@Usuario`, así que el bloqueo por IP **nunca
  funcionó**. Documentado en el código y **no corregido a propósito**: arreglarlo
  activaría un camino de bloqueo que jamás operó y, como toda la oficina sale por
  la misma IP pública, podría dejar a todos afuera de golpe.

- **`Views - copia\Perfil\Perfil.cshtml` no compila** (errores preexistentes,
  ajenos a esta integración). Sólo molesta si se activa `MvcBuildViews`.

- **`PruebaDeploy.http` quedó obsoleto.** Está marcado con una advertencia: sus
  bloques 5 en adelante crearían cobros reales.

---

## Referencias rápidas

| Qué | Dónde |
|---|---|
| Estado del servicio | `ESTADO.md` |
| Publicación en IIS | `DEPLOY.md` |
| Convenciones de código | `CLAUDE.md` |
| DDL suscripciones | `Database/01_CrearTablasSuscripciones.sql` |
| Consultas de diagnóstico | `Database/02_VerificacionWebhook.sql` |
| DDL pagos únicos | `Database/03_CrearTablaPagoUnico.sql` |
| Lado EmpleadoWeb | `EmpleadoWeb2022\EmpleadoWeb\MercadoPagoIntegration\ESTADO.md` |

**Datos del entorno**

```
API            https://mpapi.tecnisegur.com.uy   (191.239.244.138, misma VM que www)
Pool IIS       TecnisegurMercadoPago
Ruta física    C:\inetpub\wwwroot\TecnisegurMP Api
Cuenta MP      TECNISEGURURUGUAY · id 3521850855 · MLU
Base           TSD en 172.16.10.22   (usuarios y bloqueos: ENCUESTA en 10.0.0.4)
```
