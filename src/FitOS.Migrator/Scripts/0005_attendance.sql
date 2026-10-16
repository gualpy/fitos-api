-- Asistencia: una sola entrada abierta por socio y cierre explícito de entradas olvidadas.

-- Marca de las entradas cerradas por el sistema (no por una salida real): no se inventan horas de salida.
ALTER TABLE attendance ADD COLUMN auto_closed_at timestamptz;

-- Un socio no puede estar "dentro" dos veces: protege contra doble lectura del lector o dos recepcionistas a la vez.
-- Las entradas denegadas no cuentan (nunca estuvo dentro).
CREATE UNIQUE INDEX attendance_one_open_uq ON attendance (tenant_id, customer_id)
  WHERE check_out IS NULL AND result = 'granted';

-- El motivo solo existe cuando se deniega.
ALTER TABLE attendance ADD CONSTRAINT attendance_deny_reason_valid
  CHECK (result = 'denied' OR deny_reason IS NULL);

-- Búsquedas por fecha dentro de un gimnasio.
CREATE INDEX attendance_tenant_time_idx ON attendance (tenant_id, check_in DESC);
