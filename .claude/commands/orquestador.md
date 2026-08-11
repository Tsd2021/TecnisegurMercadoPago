---
description: Arranca el orquestador de la integración MercadoPago (MPAPI + EmpleadoWeb + TSD)
argument-hint: [pedido opcional — si va vacío, muestra el tablero y pregunta]
---

Tomá el rol definido en `.claude/agents/orquestador-mercadopago.md`: leelo
completo y adoptá sus reglas, su criterio y su idioma para el resto de la sesión.
No lo resumas para el usuario; aplicalo.

Después, ponete al día con el estado real del proyecto:

1. `git log --oneline -12` — qué se movió último.
2. `git status --short` — si hay trabajo sin commitear, mencionalo.
3. Leé `PENDIENTES.md` completo y el encabezado de `ESTADO.md` (hasta la primera
   sección de novedades). Si hay un `ANALISIS-*.md` con fecha más reciente que
   `PENDIENTES.md`, leelo también: manda el más nuevo.

Ojo con las fechas: estos documentos se contradicen entre sí a propósito y
`ESTADO.md` marca pasajes como desactualizados. Ante conflicto gana el código y
el commit más reciente.

Con eso, presentá un **tablero corto** —no más de quince líneas— con:

- **Dónde está el proyecto hoy**, en una o dos frases.
- **Lo que bloquea el uso real**, con lo que ya se descartó de cada tema. Un
  candidato descartado con evidencia vale tanto como una causa encontrada.
- **Riesgos vivos**: si el servidor está operando con credenciales de producción,
  decilo — cualquier link que se genere cobra dinero real.

No repitas lo que el usuario ya sabe ni expliques el dominio. Datos y estado.

---

**Si `$ARGUMENTS` trae algo**, tratalo como el pedido de trabajo: mostrá el
tablero en tres o cuatro líneas y andá derecho a eso.

**Si viene vacío**, cerrá con las dos o tres cosas que tendría sentido atacar
ahora, ordenadas por lo que destraba más, y preguntá por cuál seguimos.

Las skills del proyecto (`diagnostico-cobro`, `conciliacion-liberaciones`,
`forense-webhook`, `migracion-sql`, `cambio-punta-a-punta`) se activan solas
cuando el pedido encaja. Cuando el trabajo sea de búsqueda amplia o convenga
aislarlo, delegá en el subagente `orquestador-mercadopago`.
