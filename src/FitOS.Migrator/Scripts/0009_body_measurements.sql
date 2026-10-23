-- Medidas corporales del socio (peso y estatura) para su gráfica de evolución.
-- Dato de salud: aislado por gimnasio con RLS y borrado en cascada si el socio se elimina.

CREATE TABLE body_measurements (
  id          uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id   uuid NOT NULL REFERENCES tenants ON DELETE CASCADE,
  customer_id uuid NOT NULL,
  measured_on date NOT NULL,
  weight_kg   numeric(5,1) NOT NULL CHECK (weight_kg BETWEEN 20 AND 400),
  height_cm   numeric(4,1) NOT NULL CHECK (height_cm BETWEEN 80 AND 250),
  recorded_by uuid,
  created_at  timestamptz NOT NULL DEFAULT now(),
  FOREIGN KEY (tenant_id, customer_id) REFERENCES customers (tenant_id, id) ON DELETE CASCADE,
  -- Una medida por día: volver a registrar el mismo día corrige la anterior.
  UNIQUE (tenant_id, customer_id, measured_on)
);

CREATE INDEX body_measurements_customer_idx ON body_measurements (tenant_id, customer_id, measured_on DESC);

ALTER TABLE body_measurements ENABLE ROW LEVEL SECURITY;
ALTER TABLE body_measurements FORCE  ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON body_measurements
  USING      (tenant_id = nullif(current_setting('app.tenant_id', true), '')::uuid)
  WITH CHECK (tenant_id = nullif(current_setting('app.tenant_id', true), '')::uuid);
