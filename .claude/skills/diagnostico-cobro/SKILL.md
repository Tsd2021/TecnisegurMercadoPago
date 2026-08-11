---
name: diagnostico-cobro
description: Diagnostica por qué falla un alta de suscripción o un cobro de MercadoPago — POST /preapproval que devuelve 500 o 400, cliente que abre el link y no puede pagar, "Both payer and collector must be real or test users", cuota rechazada, link que no genera. Aísla el campo culpable recorriendo la cadena EmpleadoWeb → MPAPI → MercadoPago antes de proponer un arreglo. Usar cuando aparezca un error de alta, de adhesión o de pago contra MercadoPago.
---

# Diagnóstico de un cobro que falla

MercadoPago casi nunca dice qué campo rechazó: devuelve `500 {"message":"Internal
server error"}` sin cuerpo útil. El método es aislar por eliminación, no adivinar.

## 1. Ubicá el eslabón

```
diálogo del vendedor → EmpleadoWeb → MPAPI → MercadoPago
                                       ↓
                              webhook → base TSD → vista → TSD Desktop
```

Preguntá o deducí en cuál falla, porque el arreglo vive en distinto repo:

| Síntoma | Eslabón |
|---|---|
| No aparece el botón de cobro | EmpleadoWeb — reglas `SumarProductosMensual` / `TotalProductos > 0` |
| MPAPI responde 400/409 | `SuscripcionServicio.CrearAsync` o `PagoServicio.CrearAsync` |
| MPAPI responde 500 con mensaje de MP | El payload que MPAPI armó |
| Hay `init_point` pero el cliente no puede pagar | Cuenta / tipo de usuario / datos del pagador |
| Se cobró y no se ve en TSD | No es un problema de cobro: usá `forense-webhook` |

## 2. Traé el payload real

El log del servidor registra el JSON que se mandó. Sin el payload exacto no hay
diagnóstico: los campos que fallan son los que EmpleadoWeb rellena por defecto.

## 3. Aislá con la herramienta, no a mano

```bash
powershell -File Herramientas/DiagnosticarAltaSuscripcion.ps1 -Pedir
```

Manda el payload que falló y después una variante por campo; **la primera que
devuelve 201 señala al culpable**. Crea los `preapproval` en `pending` (no cobran
nada) y los cancela solos. Parámetros útiles: `-MailAlternativo`,
`-ExternalReference`, `-WebConfig` (lee el token del `web.config` del servidor).

`-Pedir` pide el token por consola. **Nunca pongas el access token en la línea de
comandos ni en un archivo del repo.**

## 4. Sospechosos habituales, en orden

1. **`payer_email` inválido o de relleno.** EmpleadoWeb pone
   `NOTIENE@NOTIENE.COM` cuando el contrato no tiene `Correo`. Además: el correo
   con que el cliente entra al checkout **tiene que coincidir con el
   `payer_email`** del preapproval.
2. **Tipo de cuenta cruzado.** Token real + email de prueba (o al revés) falla.
   El último segmento del access token es el ID de la cuenta dueña:
   `...-3521850855` = real, `...-3572201273` = vendedora de prueba.
3. **Fechas incoherentes.** `end_date` debe contarse desde `start_date`, no desde
   hoy. `start_date` en el pasado, o `end_date` anterior a `start_date`, rompen.
4. **Días de prueba y fecha de adhesión juntos.** Son **excluyentes** en la API.
5. **`back_url` mal formada** — sin barra antes del `?` es válida por RFC pero
   varios validadores la rechazan.
6. **Documento del pagador**, en pagos únicos: la cédula sale de
   `ContratoCotizacionAlarma.Documento` y tiene que ser válida.

Si ninguna variante levanta el 201, el bloqueo puede ser **de la cuenta de
MercadoPago y no del payload** — ya pasó. Verificá el estado de la cuenta
(`GET /users/me`, restricciones, `CacheEstadoCuenta`) antes de seguir tocando
código.

## 5. Reglas al arreglar

- Un pedido que no puede funcionar se rechaza en MPAPI con **400 y mensaje
  claro**, no se deja que MercadoPago conteste un 500 indescifrable.
- No inventes datos para que el alta pase. Un `payer_email` de relleno hace que
  todos los avisos de MP (cobro, rechazo, cancelación) vayan a un buzón
  inexistente; que el link se mande por WhatsApp no lo salva.
- Si el arreglo es en la validación, va en el servicio
  (`SuscripcionServicio` / `PagoServicio`), no en el controller.
- `dotnet build` al terminar.

## 6. Dejá registro

Estos diagnósticos son caros de repetir. Ofrecé anotar en `ANALISIS-COBROS.md`
qué se descartó y con qué evidencia — un candidato descartado vale tanto como el
culpable encontrado.
