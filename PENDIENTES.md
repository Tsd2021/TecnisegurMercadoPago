# Pendientes — retomar acá

**Cerrado el:** 29 de julio de 2026
**Estado general:** la API está en producción y operando con credenciales reales
de TECNISEGURURUGUAY. El circuito de suscripciones está probado de punta a punta.
El de pagos únicos (Checkout Pro) está implementado pero **nunca se ejecutó**.

> Lo que sigue está ordenado por urgencia. Los dos primeros bloquean el uso real.

---

## 1. Desplegar lo que quedó compilado y sin subir

Los dos proyectos compilan pero el servidor corre versiones viejas.

### 1.1 API — URGENTE, hay un bug activo

El paquete está en `publish\`. **Mientras no se suba, los WhatsApp salen mal**:
el binario desplegado tiene el mapeo viejo de la plantilla de Twilio, con el link
en `{{2}}` y el importe en `{{3}}`, cuando la plantilla aprobada espera
`{{2}}` = link y `{{3}}` = tipo. El cliente recibe los campos cruzados.

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

También incluye el envío automático de WhatsApp al crear (campo `telefono` en el
alta de suscripciones y pagos).

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
