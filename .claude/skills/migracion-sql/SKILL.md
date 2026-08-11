---
name: migracion-sql
description: Crea o modifica scripts SQL de la base TSD para esta integración — nueva columna, nueva tabla, cambio en vw_SuscripcionesEstado / vw_PagosUnicosEstado / vw_CobranzasMercadoPago, índice, o migración de datos existentes. Usar cuando el trabajo implique tocar el esquema de SuscripcionCotizacion, SuscripcionPago, PagoUnico o MercadoPagoNotificacion, o agregar un archivo a la carpeta Database/.
---

# Migraciones de la base TSD

Todo vive en la base **TSD** (`172.16.10.22`), junto a `CotizacionesComericales`
—el typo está en el esquema real, **no lo corrijas**—. La base ENCUESTA está en
otro servidor: no hay FK posible entre ambas.

## Convenciones de la carpeta `Database/`

1. **Numeración correlativa.** El próximo script es el número siguiente al mayor
   existente (mirá la carpeta antes de nombrarlo). `99_DatosPruebaModuloTSD.sql`
   queda siempre último y no cuenta.
2. **Se ejecutan en orden numérico.** Cada uno asume los anteriores, y varios
   recrean vistas que el siguiente vuelve a extender. Si tu cambio toca una
   vista, **copiá la definición del script más reciente que la crea** y extendela
   ahí; no partas del script 01.
3. **Todos idempotentes.** Reejecutarlo no debe fallar ni duplicar:
   `IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE ...)` para columnas e índices,
   `CREATE OR ALTER VIEW` para vistas, `IF OBJECT_ID(...) IS NULL` para tablas.
4. Encabezado con `USE TSD; GO`, comentario de bloque arriba explicando **qué
   problema resuelve** y qué asume, en español. Mirá `09` y `10` como modelo:
   documentan el error que corrigen, no sólo el DDL.

## Reglas de contenido

- **Columnas nuevas de dinero o fechas de MercadoPago nacen `NULL`**, nunca con
  `DEFAULT 0`. `NULL` significa "MP no lo informó todavía"; cero es un dato falso.
- Si el dato hay que recalcularlo en filas viejas, **anulá la columna a
  propósito** para que el repaso las vuelva a tomar — es lo que hace el script 09
  con `Comision`, y por eso el filtro del repaso incluye `Comision IS NULL`.
- **Las lecturas van por vista.** Si agregás una columna que TSD o EmpleadoWeb
  van a consumir, exponela en la vista correspondiente en el mismo script.
- **No cambies los literales de `EstadoLiberacion`.** El módulo de TSD compara
  contra los cuatro valores del script 08 con texto. Precisión nueva → columna
  nueva (como `LiberacionConfirmada`).
- En los `CASE` de liberación, **lo que informa MercadoPago va antes que la
  comparación de fechas**.
- Índices únicos sobre estados vivos van **filtrados** (`WHERE Estado IN (...)`),
  como `UX_SuscripcionCotizacion_Viva`.

## Después de escribir el script

- Decí explícitamente **si ya se ejecutó o no** en TSD. Es la pregunta que
  siempre vuelve.
- Si toca una vista que consume TSD Desktop, verificá contra
  `../TSD/MODULO_COBRANZAS_MERCADOPAGO.md` y las consultas textuales de
  `ServicioCobranzasMercadoPago.cs` que sigan resolviendo. Cuando el cambio vive
  en la vista, **TSD lo muestra sin recompilar**; cuando toca los literales, no.
- Si hay que ajustar el código de MPAPI, los repositorios usan ADO.NET directo
  con parámetros tipados (`cmd.Parameters.Add(..., SqlDbType...)`) y la conexión
  dentro de un `using`. Sin ORM.
- Ofrecé anotar el cambio en `ESTADO.md`.

## Lo que no se hace

- No ejecutes DDL contra la base sin que el usuario lo pida en ese pedido.
- No hagas `DROP` de columnas ni de tablas con datos productivos: proponelo y
  esperá confirmación.
- No renumeres ni edites scripts ya ejecutados. Un script ejecutado es historia;
  las correcciones van en uno nuevo.
