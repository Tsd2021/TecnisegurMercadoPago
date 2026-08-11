# Ticket para soporte de MercadoPago

**Preparado el:** 11 de agosto de 2026
**Para pegar tal cual.** La evidencia de respaldo está en `ANALISIS-COBROS.md`
§2 ter y los snapshots JSON en `Herramientas/reportes/`.

---

## Asunto

Las suscripciones (preapproval) no completan la vinculación del medio de pago — cuenta 3521850855

## Cuerpo

Buenas.

Tenemos una integración de suscripciones que **no logra que ninguna suscripción
quede autorizada**. El cliente abre el link, ingresa la tarjeta, y el checkout lo
devuelve al inicio de mercadopago.com en lugar de a nuestra `back_url`. Segundos
después el preapproval figura `cancelled`, sin haber pasado nunca por
`authorized` y sin ningún medio de pago asociado.

Van 10 intentos de 10, con distintos clientes, distintas tarjetas, distintos
importes y en distintos días. Ningún cliente pudo suscribirse todavía.

### Datos de la integración

```
Cuenta cobradora   : 3521850855  (TECNISEGURURUGUAY)  ·  MLU
Aplicación         : 437871649677590
Modelo             : preapproval SIN plan asociado
                     POST /preapproval con status "pending", sin card_token_id
                     el cliente define el medio de pago en el init_point
```

### Caso concreto

```
preapproval  498c7bbb05124f908ed0eb6bafd85b8c
external_reference  COT-36  ·  $15,00 UYU  ·  free_trial 15 días
```

Secuencia, en UTC:

```
16:07:37.763   POST /preapproval devuelve 201, status "pending"
               notificación subscription_preapproval / created  (version 0)

16:07:49.213   consultamos GET /preapproval/{id}  →  status "pending"

   (en algún punto de esta ventana el cliente ingresa la tarjeta
    y el checkout lo redirige al inicio de mercadopago.com)

16:08:03.385   notificación subscription_preapproval / updated  (version 2)

16:08:19.163   consultamos GET /preapproval/{id}  →  status "cancelled"
```

Del alta a la cancelación pasaron **25,6 segundos**.

### Lo que devuelve el recurso hoy

```
status                : cancelled
payer_id              : 1858717677
card_id               : el campo no viene en la respuesta
payment_method_id     : null
summarized            : todo en null (quotas, charged_quantity, semaphore, …)
date_created          : 2026-08-11T16:07:37.763Z
last_modified         : 2026-08-11T16:08:03.385Z
back_url              : https://www.tecnisegur.com.uy/
notification_url      : https://mpapi.tecnisegur.com.uy/api/webhook
```

`last_modified` coincide con la notificación de la cancelación: esa fue la última
modificación del recurso. Nunca hubo tarjeta vinculada.

### El mismo flujo funciona en una cuenta de prueba

Reprodujimos el escenario con el **mismo payload**, cambiando únicamente la
cuenta cobradora por una cuenta de prueba (3572201273, también MLU):

```
preapproval  79f90fd0c82846b5ae22bc963aed465e
resultado    autorizó correctamente  ·  payment_method_id "master"  ·  card_id 9835956553
```

El contraste completo:

```
Cuenta de prueba (3572201273)   10 preapproval   4 con medio de pago asociado
Cuenta productiva (3521850855)  10 preapproval   0 con medio de pago asociado
```

### Y no es la aplicación: lo probamos con una segunda

Como en la prueba anterior cambiaban la cuenta **y** la aplicación al mismo
tiempo, repetimos el experimento cambiando **sólo la aplicación**: creamos una
segunda aplicación bajo la misma cuenta cobradora y mandamos el mismo payload
con sus credenciales de producción.

```
preapproval        085af30debdd43b2b3f425ba02432bc5
application_id     709858592631421      (aplicación nueva)
collector_id       3521850855           (misma cuenta)
date_created       2026-08-11T20:01:50.915Z
last_modified      2026-08-11T20:02:37.792Z     (46,9 s después)
status             cancelled
card_id            null
payment_method_id  null
```

El cliente completó el checkout con una tarjeta real y el resultado fue
idéntico: sin medio de pago asociado, sin ningún cobro, cancelada sola. En el
mismo momento, `GET /users/me` devuelve `status.billing.allow: true` con
`codes: []`.

**Con dos aplicaciones distintas de la misma cuenta el comportamiento es el
mismo, y con la misma aplicación en otra cuenta el comportamiento cambia.** La
variable que determina el resultado es la cuenta cobradora.

### Por qué descartamos que la cancelación borre esos campos

En la cuenta de prueba hay preapproval **cancelados que conservan el medio de
pago**:

```
bc1ffe9b44bc40d2a06a3459f7d85bb4   cancelled   card_id 9851793766   payment_method_id master
79f90fd0c82846b5ae22bc963aed465e   cancelled   card_id 9835956553   payment_method_id master
ba520aa92c92432c99e7382a0d0df32b   cancelled   card_id —            payment_method_id account_money
```

O sea que cancelar no elimina `card_id` ni `payment_method_id`. Que en la cuenta
productiva no estén significa que **nunca se llegaron a asignar**.

### Lo que ya verificamos y descartamos de nuestro lado

- **El payload.** El mismo JSON exacto autoriza contra la cuenta de prueba.
- **La aplicación.** Una segunda aplicación de la misma cuenta falla igual.
- **`back_url`.** Probamos con y sin parámetros de query; hoy es una URL limpia
  (`https://www.tecnisegur.com.uy/`) y el comportamiento no cambió.
- **Fechas.** El caso citado va sin `start_date` y sin `end_date`.
- **El correo del pagador.** Es una casilla real y coincide con la que el cliente
  ingresa en el checkout.
- **Los webhooks.** Llegan, con firma válida, y los procesamos correctamente.
- **La dirección de la cuenta.** Se completó; `GET /users/me` devuelve
  `status.billing.allow: true` con `codes: []`.
- **Que la cancelación saliera de nuestro sistema.** Nuestros logs muestran, en
  toda la ventana, únicamente llamadas `GET /preapproval/{id}`. **No emitimos
  ningún `PUT`.** Nuestro propio rastro de auditoría lo registra así:

  ```
  AUDITORIA-PREAPPROVAL 2026-08-11T16:08:19.163Z operacion=SINCRONIZAR
    direccion=ENTRANTE origen=mercadopago preapproval=498c7bbb05124f908ed0eb6bafd85b8c
    external_reference=COT-36 estado_local_anterior=pending
    estado_informado=cancelled baja_ajena=True
  ```

  `baja_ajena=True` significa que el cambio de estado entró desde ustedes y no
  lo produjo ninguna operación nuestra.

### Un detalle que puede ayudar a ubicarlo

Al terminar el checkout el cliente es redirigido al inicio de mercadopago.com y
no a la `back_url` declarada en el preapproval, lo que sugiere que el flujo se
interrumpe antes de llegar al paso de redirección.

La redirección va al `callback_url` de la **aplicación** —que está sin
configurar— en lugar de al `back_url` del preapproval.

### Lo que necesitamos saber

**¿Por qué el checkout de suscripciones no completa la vinculación del medio de
pago en la cuenta 3521850855 / aplicación 437871649677590, si el mismo payload la
completa sin problemas en una cuenta de prueba?**

Concretamente, si pueden revisar de su lado:

1. Qué le ocurre al preapproval `498c7bbb05124f908ed0eb6bafd85b8c` entre las
   16:07:37Z y las 16:08:03Z del 11/08/2026 — la ventana en la que el cliente
   carga la tarjeta y el recurso termina `cancelled`.
2. Si hay alguna restricción, habilitación pendiente o configuración de la cuenta
   o de la aplicación que impida constituir débitos recurrentes.
3. Por qué el preapproval se cancela solo, sin intervención nuestra y sin haber
   llegado a `authorized`.
4. El panel muestra la aplicación 437871649677590 en **"Estado: Etapa 1 de 5"**,
   con los tres ítems de *"Prueba tu integración"* completos y sin forma de
   avanzar. ¿Ese estado limita de alguna manera la constitución de débitos
   recurrentes en producción, o es un recorrido que no aplica a las
   integraciones de Suscripciones?

Quedamos a disposición para cualquier dato adicional. Podemos reproducirlo
cuando lo necesiten.

Muchas gracias.
