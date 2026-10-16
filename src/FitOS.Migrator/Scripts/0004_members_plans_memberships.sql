-- Socios, planes y membresías: reglas que la base garantiza aunque la aplicación falle.

-- btree_gist permite una restricción de exclusión con igualdad (tenant, socio) + solapamiento de fechas.
-- Es una extensión "trusted" en PostgreSQL 13+: la puede crear el dueño de la base sin ser superusuario.
CREATE EXTENSION IF NOT EXISTS btree_gist;

-- Un socio no puede tener dos membresías vigentes que se solapen en el tiempo.
-- Las canceladas y vencidas no cuentan; las futuras (programadas) sí, para que no se pisen.
ALTER TABLE memberships
  ADD CONSTRAINT memberships_no_overlap EXCLUDE USING gist (
    tenant_id   WITH =,
    customer_id WITH =,
    daterange(starts_on, ends_on, '[]') WITH &&
  ) WHERE (status = 'active');

-- Dos planes del mismo gimnasio no pueden llamarse igual (sin distinguir mayúsculas).
CREATE UNIQUE INDEX plans_name_uq ON plans (tenant_id, lower(name));

-- Tarifas de IVA que SICOFAC sabe facturar (15 %, 5 %, 0 %).
ALTER TABLE plans   ADD CONSTRAINT plans_iva_rate_valid   CHECK (iva_rate IN (0, 5, 15));
ALTER TABLE plans   ADD CONSTRAINT plans_duration_valid   CHECK (duration_months BETWEEN 1 AND 60);
ALTER TABLE plans   ADD CONSTRAINT plans_price_valid      CHECK (price <= 100000);

-- Búsqueda del listado de socios.
CREATE INDEX customers_name_idx ON customers (tenant_id, lower(name));
