/* ============================================================================
   99 - Datos de prueba para el módulo de cobranzas de TSD Desktop

   ⚠️  ESCRIBE EN LA BASE DE PRODUCCIÓN (TSD, 172.16.10.22).
   ⚠️  NO toca MercadoPago. No crea suscripciones reales, no genera links, no
       mueve un peso. Son filas locales inventadas, nada más.

   POR QUÉ EXISTE
   --------------
   Al 31/07/2026 la base no tiene ninguna suscripción ni ningún pago: los datos
   de prueba se limpiaron el 29/07 y todavía no hubo cobros reales. Con la base
   vacía, el módulo de TSD compila y las vistas devuelven cero filas — o sea que
   el código de mapeo (LeerCobranza, LeerCuota, LeerCliente) nunca leyó una fila
   y está sin ejercitar.

   Este script arma un escenario chico pero completo para recorrer las pestañas:

     - 1 suscripción activa
     - 1 cuota cobrada y YA LIBERADA        (Disponible = 1)
     - 1 cuota cobrada y PENDIENTE de liberar (Disponible = 0)
     - 1 cuota cobrada SIN datos de liberación (Disponible = NULL → "A liberar")
     - 1 cuota RECHAZADA                     (pestaña Pagos pendientes)
     - 1 cuota PROGRAMADA a futuro
     - 1 pago único cobrado y liberado

   Los importes usan la comisión real de la cuenta: 4,99% + IVA = 6,09%.

   CÓMO LIMPIAR
   ------------
   Todo lo que inserta queda marcado con UsuarioCreacion = 'PRUEBA-TSD'.
   Al final del archivo está el bloque de limpieza. Ejecutarlo apenas termine
   la verificación: son datos falsos en una base de producción y no deben
   quedar ahí.
   ============================================================================ */

USE TSD;
GO

SET NOCOUNT ON;
GO

/* ----------------------------------------------------------------------------
   1) Elegir una cotización sobre la cual colgar la prueba.

      Tiene que ser una que NO tenga ya una suscripción viva: el índice único
      filtrado UX_SuscripcionCotizacion_Viva permite una sola por cotización,
      y es la defensa contra el doble cobro. Si no hay ninguna disponible, el
      script se detiene en vez de romper el índice.
   ---------------------------------------------------------------------------- */
DECLARE @IdCotizacion INT;

SELECT TOP 1 @IdCotizacion = c.Id
FROM dbo.CotizacionesComericales AS c
WHERE NOT EXISTS
(
    SELECT 1
    FROM dbo.SuscripcionCotizacion AS s
    WHERE s.IdCotizacion = c.Id
      AND s.Estado IN ('pending', 'authorized', 'paused')
)
ORDER BY c.Id DESC;

IF @IdCotizacion IS NULL
BEGIN
    RAISERROR(
        'No hay ninguna cotizacion libre en CotizacionesComericales. Cree una desde EmpleadoWeb antes de correr esta prueba.',
        16, 1);
    RETURN;
END

PRINT 'Usando la cotización ' + CAST(@IdCotizacion AS VARCHAR(10)) + '.';


/* ----------------------------------------------------------------------------
   2) La suscripción
   ---------------------------------------------------------------------------- */
DECLARE @IdSuscripcion INT;

INSERT INTO dbo.SuscripcionCotizacion
(
    IdCotizacion, ExternalReference, PreapprovalId, InitPoint,
    NombreCliente, PayerEmail, MontoMensual, Moneda,
    Estado, FechaCreacion, FechaAutorizacion, FechaProximoPago, FechaUltimoPago,
    UsuarioCreacion, Origen
)
VALUES
(
    @IdCotizacion,
    'COT-' + CAST(@IdCotizacion AS VARCHAR(10)),
    'PRUEBA-TSD-' + CAST(@IdCotizacion AS VARCHAR(10)),   -- no existe en MercadoPago
    'https://www.mercadopago.com.uy/subscriptions/checkout?preapproval_id=PRUEBA',
    'Cliente de Prueba TSD',
    'prueba@tecnisegur.com.uy',
    7770.00,
    'UYU',
    'authorized',
    DATEADD(month, -3, GETDATE()),
    DATEADD(month, -3, GETDATE()),
    DATEADD(day, 7, GETDATE()),
    DATEADD(day, -5, GETDATE()),
    'PRUEBA-TSD',
    'TSD'
);

SET @IdSuscripcion = SCOPE_IDENTITY();

PRINT 'Suscripción de prueba creada con Id ' + CAST(@IdSuscripcion AS VARCHAR(10)) + '.';


/* ----------------------------------------------------------------------------
   3) Las cuotas

      Neto y comisión calculados con la tarifa real:
          comisión = bruto * 0,0499 * 1,22   (4,99% + IVA)
          neto     = bruto - comisión
      Sobre $7.770: comisión $473,02 · neto $7.296,98
   ---------------------------------------------------------------------------- */

/* 3.1 Cobrada hace 60 días — ya liberada. Debe salir como "Disponible". */
INSERT INTO dbo.SuscripcionPago
(IdSuscripcion, MpAuthorizedPaymentId, MpPaymentId, Monto, MontoNeto, Comision,
 Moneda, Estado, EstadoPago, DetalleEstado,
 FechaProgramada, FechaPago, FechaLiberacion, FechaRegistro)
VALUES
(@IdSuscripcion, 'PRUEBA-AP-1', 'PRUEBA-PAY-1', 7770.00, 7296.98, 473.02,
 'UYU', 'processed', 'approved', 'accredited',
 DATEADD(day, -60, GETDATE()), DATEADD(day, -60, GETDATE()),
 DATEADD(day, -39, GETDATE()), GETDATE());

/* 3.2 Cobrada hace 5 días — se libera en 16. Debe salir como "A liberar". */
INSERT INTO dbo.SuscripcionPago
(IdSuscripcion, MpAuthorizedPaymentId, MpPaymentId, Monto, MontoNeto, Comision,
 Moneda, Estado, EstadoPago, DetalleEstado,
 FechaProgramada, FechaPago, FechaLiberacion, FechaRegistro)
VALUES
(@IdSuscripcion, 'PRUEBA-AP-2', 'PRUEBA-PAY-2', 7770.00, 7296.98, 473.02,
 'UYU', 'processed', 'approved', 'accredited',
 DATEADD(day, -5, GETDATE()), DATEADD(day, -5, GETDATE()),
 DATEADD(day, 16, GETDATE()), GETDATE());

/* 3.3 Cobrada hace 30 días, SIN datos de liberación.
       Es el caso de las cuotas viejas registradas antes de que la API capture
       los campos (§9.1): neto, comisión y liberación en NULL.
       La interfaz debe mostrar "—" en las tres, tratarla como "A liberar", y
       el total de neto debe sumar las demás declarando que ésta falta. */
INSERT INTO dbo.SuscripcionPago
(IdSuscripcion, MpAuthorizedPaymentId, MpPaymentId, Monto,
 Moneda, Estado, EstadoPago, DetalleEstado,
 FechaProgramada, FechaPago, FechaRegistro)
VALUES
(@IdSuscripcion, 'PRUEBA-AP-3', 'PRUEBA-PAY-3', 7770.00,
 'UYU', 'processed', 'approved', 'accredited',
 DATEADD(day, -30, GETDATE()), DATEADD(day, -30, GETDATE()), GETDATE());

/* 3.4 Rechazada por fondos insuficientes. Pestaña "Pagos pendientes". */
INSERT INTO dbo.SuscripcionPago
(IdSuscripcion, MpAuthorizedPaymentId, MpPaymentId, Monto,
 Moneda, Estado, EstadoPago, DetalleEstado,
 FechaProgramada, FechaRegistro)
VALUES
(@IdSuscripcion, 'PRUEBA-AP-4', 'PRUEBA-PAY-4', 7770.00,
 'UYU', 'recycling', 'rejected', 'cc_rejected_insufficient_amount',
 DATEADD(day, -12, GETDATE()), GETDATE());

/* 3.5 Programada a futuro. */
INSERT INTO dbo.SuscripcionPago
(IdSuscripcion, MpAuthorizedPaymentId, Monto,
 Moneda, Estado, FechaProgramada, FechaRegistro)
VALUES
(@IdSuscripcion, 'PRUEBA-AP-5', 7770.00,
 'UYU', 'scheduled', DATEADD(day, 7, GETDATE()), GETDATE());

PRINT '5 cuotas de prueba creadas.';


/* ----------------------------------------------------------------------------
   4) Un pago único de equipamiento, cobrado y liberado.
      Sobre $50.000: comisión $3.043,90 · neto $46.956,10

      ExternalReference NO se informa: es columna calculada PERSISTED.
   ---------------------------------------------------------------------------- */
INSERT INTO dbo.PagoUnico
(IdCotizacion, MpPreferenceId, MpPaymentId, InitPoint,
 Concepto, NombreCliente, PayerEmail, Monto, MontoNeto, Comision, Moneda,
 Estado, EstadoDetalle, FechaCreacion, FechaPago, FechaLiberacion,
 UsuarioCreacion, Origen)
VALUES
(@IdCotizacion, 'PRUEBA-PREF-1', 'PRUEBA-PAYU-1',
 'https://www.mercadopago.com.uy/checkout/v1/redirect?pref_id=PRUEBA',
 'TECNISEGUR ALARMAS - Equipamiento Cliente de Prueba',
 'Cliente de Prueba TSD', 'prueba@tecnisegur.com.uy',
 50000.00, 46956.10, 3043.90, 'UYU',
 'approved', 'accredited',
 DATEADD(day, -45, GETDATE()), DATEADD(day, -45, GETDATE()),
 DATEADD(day, -24, GETDATE()),
 'PRUEBA-TSD', 'TSD');

PRINT 'Pago único de prueba creado.';
GO


/* ----------------------------------------------------------------------------
   5) Qué tiene que verse en el módulo
   ---------------------------------------------------------------------------- */
SELECT Tipo, NombreCliente, Monto, MontoNeto, Comision,
       FechaPago, FechaLiberacion, Cobrado, Disponible
FROM dbo.vw_CobranzasMercadoPago
WHERE NombreCliente = 'Cliente de Prueba TSD'
ORDER BY Tipo, FechaPago;
GO

/* Esperado en TSD → IT → Alarmas → Cobranzas MercadoPago:

   Clientes           1 fila, "Cliente de Prueba TSD", estado Activa
   Suscripciones      1 fila activa, 2 cuotas cobradas... (la 3.3 también suma:
                      la vista cuenta EstadoPago='approved', son 3), 1 rechazada
   Pagos realizados   4 filas (3 cuotas + 1 pago único)
                        · 1 Disponible con fecha pasada
                        · 1 A liberar con fecha futura
                        · 1 con neto/comisión/liberación en "—" → A liberar
                        · el pago único, Disponible
                        · Bruto $73.310 · Neto suma sólo las 3 con dato,
                          declarando 1 sin informar
   Pagos pendientes   2 filas: la rechazada y la programada
   Resumen            Disponible ≈ $54.253 · A liberar ≈ $7.297 + la sin dato
*/


/* ============================================================================
   LIMPIEZA — ejecutar apenas termine la verificación.

   Descomentar y correr. El orden importa: primero las cuotas, después la
   suscripción (hay FK).
   ============================================================================

DELETE p
FROM dbo.SuscripcionPago AS p
INNER JOIN dbo.SuscripcionCotizacion AS s ON s.Id = p.IdSuscripcion
WHERE s.UsuarioCreacion = 'PRUEBA-TSD';

DELETE FROM dbo.SuscripcionCotizacion WHERE UsuarioCreacion = 'PRUEBA-TSD';

DELETE FROM dbo.PagoUnico WHERE UsuarioCreacion = 'PRUEBA-TSD';

-- Verificar que no quedó nada:
SELECT COUNT(*) AS SuscripcionesPrueba FROM dbo.SuscripcionCotizacion WHERE UsuarioCreacion = 'PRUEBA-TSD';
SELECT COUNT(*) AS PagosPrueba         FROM dbo.PagoUnico            WHERE UsuarioCreacion = 'PRUEBA-TSD';
-- Las dos tienen que dar 0.

============================================================================ */
