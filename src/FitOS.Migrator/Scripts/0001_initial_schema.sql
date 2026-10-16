-- FitOS · esquema PostgreSQL multi-tenant (una base, tenant_id + Row-Level Security)
-- Requiere PostgreSQL 14+. Aplicar con un rol propietario (migraciones); la API
-- debe conectarse con `fitos_app` (sin BYPASSRLS) y fijar el tenant por transacción:
--     SET LOCAL app.tenant_id = '<uuid>';
-- Facturación: FitOS NO firma ni habla con el SRI. Delega en SICOFAC (API REST);
-- aquí solo se guarda el vínculo, un snapshot del comprobante y su estado.

CREATE EXTENSION IF NOT EXISTS citext;

-- ---------------------------------------------------------------------------
-- Roles de la aplicación
-- ---------------------------------------------------------------------------
DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'fitos_app') THEN
    CREATE ROLE fitos_app NOLOGIN NOBYPASSRLS;   -- dar LOGIN + password al desplegar
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'fitos_platform') THEN
    CREATE ROLE fitos_platform NOLOGIN BYPASSRLS; -- panel super-admin / jobs globales
  END IF;
END $$;

-- ---------------------------------------------------------------------------
-- Tenants (gimnasios) y configuración fiscal
-- ---------------------------------------------------------------------------
CREATE TABLE tenants (
  id          uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  name        text NOT NULL,
  slug        citext NOT NULL UNIQUE,             -- subdominio: <slug>.fitos.com
  status      text NOT NULL DEFAULT 'active' CHECK (status IN ('trial','active','suspended','cancelled')),
  created_at  timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE tenant_settings (
  tenant_id          uuid PRIMARY KEY REFERENCES tenants ON DELETE CASCADE,
  allow_without_plan boolean NOT NULL DEFAULT false,   -- permitir ingreso sin plan vigente
  timezone           text NOT NULL DEFAULT 'America/Guayaquil',
  currency           char(3) NOT NULL DEFAULT 'USD'
);

-- Datos del emisor (Ecuador/SRI). El certificado .p12 vive en SICOFAC, no aquí.
CREATE TABLE tenant_fiscal_config (
  tenant_id             uuid PRIMARY KEY REFERENCES tenants ON DELETE CASCADE,
  country               char(2) NOT NULL DEFAULT 'EC',
  ruc                   varchar(13) NOT NULL,
  legal_name            text NOT NULL,
  trade_name            text,
  head_office_address   text NOT NULL,
  regime                text NOT NULL DEFAULT 'general' CHECK (regime IN ('general','rimpe_emprendedor','rimpe_negocio_popular')),
  accounting_required   boolean NOT NULL DEFAULT false,
  environment           text NOT NULL DEFAULT 'test' CHECK (environment IN ('test','production')),
  provider              text NOT NULL DEFAULT 'sicofac',
  provider_company_id   text,                          -- id de la Company en SICOFAC
  certificate_expires_at date,                         -- espejo informativo para avisos
  sri_certification_ok  boolean NOT NULL DEFAULT false, -- habilitado para producción
  UNIQUE (ruc)
);

-- Contadores por tenant (código de cliente, etc.) sin huecos ni condiciones de carrera
CREATE TABLE tenant_counters (
  tenant_id uuid NOT NULL REFERENCES tenants ON DELETE CASCADE,
  name      text NOT NULL,
  value     bigint NOT NULL DEFAULT 0,
  PRIMARY KEY (tenant_id, name)
);

CREATE FUNCTION next_counter(p_tenant uuid, p_name text) RETURNS bigint
LANGUAGE sql AS $$
  INSERT INTO tenant_counters (tenant_id, name, value)
  VALUES (p_tenant, p_name, 1)
  ON CONFLICT (tenant_id, name) DO UPDATE SET value = tenant_counters.value + 1
  RETURNING value;
$$;

-- ---------------------------------------------------------------------------
-- Sedes y puntos de emisión
-- ---------------------------------------------------------------------------
CREATE TABLE branches (
  id                uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id         uuid NOT NULL REFERENCES tenants ON DELETE CASCADE,
  name              text NOT NULL,
  address           text NOT NULL,
  sri_establishment char(3) NOT NULL CHECK (sri_establishment ~ '^[0-9]{3}$'),
  is_active         boolean NOT NULL DEFAULT true,
  UNIQUE (tenant_id, id),
  UNIQUE (tenant_id, sri_establishment)
);

CREATE TABLE emission_points (
  id                    uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id             uuid NOT NULL,
  branch_id             uuid NOT NULL,
  code                  char(3) NOT NULL CHECK (code ~ '^[0-9]{3}$'),
  provider_point_id     text,                          -- id del punto en SICOFAC
  is_active             boolean NOT NULL DEFAULT true,
  UNIQUE (tenant_id, id),
  UNIQUE (tenant_id, branch_id, code),
  FOREIGN KEY (tenant_id, branch_id) REFERENCES branches (tenant_id, id)
);

CREATE TABLE business_hours (
  tenant_id  uuid NOT NULL,
  branch_id  uuid NOT NULL,
  weekday    smallint NOT NULL CHECK (weekday BETWEEN 1 AND 7),  -- 1 = lunes (ISO)
  is_open    boolean NOT NULL DEFAULT true,
  open_time  time NOT NULL DEFAULT '06:00',
  close_time time NOT NULL DEFAULT '22:00',
  PRIMARY KEY (tenant_id, branch_id, weekday),
  FOREIGN KEY (tenant_id, branch_id) REFERENCES branches (tenant_id, id) ON DELETE CASCADE
);

-- ---------------------------------------------------------------------------
-- Socios y usuarios
-- ---------------------------------------------------------------------------
CREATE TABLE customers (
  id                  uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id           uuid NOT NULL REFERENCES tenants ON DELETE CASCADE,
  branch_id           uuid,
  client_code         bigint NOT NULL,                   -- visible, correlativo por tenant
  name                text NOT NULL,
  identification_type text CHECK (identification_type IN ('04','05','06','08')), -- RUC, cédula, pasaporte, exterior
  identification      text,
  email               citext,
  phone               text,
  gender              char(1) CHECK (gender IN ('M','F','O')),
  birth_date          date,
  rfid_card           text,
  status              text NOT NULL DEFAULT 'active' CHECK (status IN ('active','inactive')),
  joined_at           date NOT NULL DEFAULT current_date,
  created_at          timestamptz NOT NULL DEFAULT now(),
  UNIQUE (tenant_id, id),
  UNIQUE (tenant_id, client_code),
  CHECK ((identification IS NULL) = (identification_type IS NULL)),
  FOREIGN KEY (tenant_id, branch_id) REFERENCES branches (tenant_id, id)
);
CREATE UNIQUE INDEX customers_ident_uq ON customers (tenant_id, identification) WHERE identification IS NOT NULL;
CREATE UNIQUE INDEX customers_rfid_uq  ON customers (tenant_id, rfid_card)     WHERE rfid_card IS NOT NULL;

CREATE FUNCTION customers_set_code() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF NEW.client_code IS NULL THEN NEW.client_code := next_counter(NEW.tenant_id, 'client_code'); END IF;
  RETURN NEW;
END $$;

-- client_code se declara NOT NULL; el trigger BEFORE lo rellena antes del chequeo
CREATE TRIGGER customers_code BEFORE INSERT ON customers
  FOR EACH ROW EXECUTE FUNCTION customers_set_code();

-- Usuarios: tenant_id NULL = administrador de plataforma
CREATE TABLE users (
  id            uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id     uuid REFERENCES tenants ON DELETE CASCADE,
  username      citext NOT NULL,
  password_hash text NOT NULL,                           -- Argon2/bcrypt (Identity), nunca SHA simple
  display_name  text NOT NULL,
  role          text NOT NULL CHECK (role IN ('platform_admin','owner','admin','reception','customer')),
  customer_id   uuid,
  is_active     boolean NOT NULL DEFAULT true,
  last_login_at timestamptz,
  created_at    timestamptz NOT NULL DEFAULT now(),
  UNIQUE (tenant_id, id),
  FOREIGN KEY (tenant_id, customer_id) REFERENCES customers (tenant_id, id),
  CHECK ((role = 'platform_admin') = (tenant_id IS NULL)),
  CHECK (role <> 'customer' OR customer_id IS NOT NULL)
);
CREATE UNIQUE INDEX users_username_uq ON users (tenant_id, username) WHERE tenant_id IS NOT NULL;
CREATE UNIQUE INDEX users_platform_uq  ON users (username)            WHERE tenant_id IS NULL;

-- ---------------------------------------------------------------------------
-- Planes y membresías
-- ---------------------------------------------------------------------------
CREATE TABLE plans (
  id              uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id       uuid NOT NULL REFERENCES tenants ON DELETE CASCADE,
  name            text NOT NULL,
  description     text,
  price           numeric(12,2) NOT NULL CHECK (price >= 0),   -- sin IVA
  iva_rate        numeric(5,2)  NOT NULL DEFAULT 15.00 CHECK (iva_rate >= 0),
  duration_months smallint NOT NULL CHECK (duration_months > 0),
  is_promotion    boolean NOT NULL DEFAULT false,
  is_active       boolean NOT NULL DEFAULT true,
  UNIQUE (tenant_id, id)
);

CREATE TABLE memberships (
  id          uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id   uuid NOT NULL,
  customer_id uuid NOT NULL,
  plan_id     uuid NOT NULL,
  starts_on   date NOT NULL,
  ends_on     date NOT NULL,
  status      text NOT NULL DEFAULT 'active' CHECK (status IN ('active','expired','cancelled')),
  sale_id     uuid,                                       -- venta que la originó (FK abajo)
  created_at  timestamptz NOT NULL DEFAULT now(),
  UNIQUE (tenant_id, id),
  CHECK (ends_on >= starts_on),
  FOREIGN KEY (tenant_id, customer_id) REFERENCES customers (tenant_id, id),
  FOREIGN KEY (tenant_id, plan_id)     REFERENCES plans (tenant_id, id)
);
CREATE INDEX memberships_customer_idx ON memberships (tenant_id, customer_id, ends_on DESC);

-- ---------------------------------------------------------------------------
-- Control de acceso
-- ---------------------------------------------------------------------------
CREATE TABLE access_devices (
  id           uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id    uuid NOT NULL,
  branch_id    uuid NOT NULL,
  name         text NOT NULL,
  kind         text NOT NULL CHECK (kind IN ('turnstile','rfid_reader','fingerprint','face','simulator')),
  api_key_hash text NOT NULL,                             -- credencial propia del dispositivo/servicio local
  last_seen_at timestamptz,
  is_active    boolean NOT NULL DEFAULT true,
  UNIQUE (tenant_id, id),
  FOREIGN KEY (tenant_id, branch_id) REFERENCES branches (tenant_id, id)
);

CREATE TABLE attendance (
  id          uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id   uuid NOT NULL,
  branch_id   uuid NOT NULL,
  customer_id uuid NOT NULL,
  device_id   uuid,
  check_in    timestamptz NOT NULL DEFAULT now(),
  check_out   timestamptz,
  result      text NOT NULL DEFAULT 'granted' CHECK (result IN ('granted','denied')),
  deny_reason text,
  CHECK (check_out IS NULL OR check_out >= check_in),
  FOREIGN KEY (tenant_id, branch_id)   REFERENCES branches (tenant_id, id),
  FOREIGN KEY (tenant_id, customer_id) REFERENCES customers (tenant_id, id),
  FOREIGN KEY (tenant_id, device_id)   REFERENCES access_devices (tenant_id, id)
);
CREATE INDEX attendance_customer_idx ON attendance (tenant_id, customer_id, check_in DESC);
CREATE INDEX attendance_day_idx      ON attendance (tenant_id, branch_id, check_in);

-- ---------------------------------------------------------------------------
-- Inventario, ventas (POS)
-- ---------------------------------------------------------------------------
CREATE TABLE products (
  id         uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id  uuid NOT NULL REFERENCES tenants ON DELETE CASCADE,
  sku        text,
  barcode    text,
  name       text NOT NULL,
  category   text,
  price      numeric(12,2) NOT NULL CHECK (price >= 0),    -- sin IVA
  iva_rate   numeric(5,2)  NOT NULL DEFAULT 15.00 CHECK (iva_rate >= 0),
  stock      integer NOT NULL DEFAULT 0 CHECK (stock >= 0), -- nunca negativo
  is_active  boolean NOT NULL DEFAULT true,
  UNIQUE (tenant_id, id)
);
CREATE UNIQUE INDEX products_sku_uq ON products (tenant_id, sku) WHERE sku IS NOT NULL;

CREATE TABLE stock_movements (
  id         uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id  uuid NOT NULL,
  product_id uuid NOT NULL,
  delta      integer NOT NULL CHECK (delta <> 0),
  reason     text NOT NULL CHECK (reason IN ('sale','purchase','adjustment','return')),
  sale_id    uuid,
  user_id    uuid,
  created_at timestamptz NOT NULL DEFAULT now(),
  FOREIGN KEY (tenant_id, product_id) REFERENCES products (tenant_id, id)
);

CREATE TABLE sales (
  id           uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id    uuid NOT NULL,
  branch_id    uuid NOT NULL,
  customer_id  uuid,                                      -- NULL = consumidor final sin socio
  cashier_id   uuid,
  buyer_kind   text NOT NULL DEFAULT 'final_consumer' CHECK (buyer_kind IN ('final_consumer','identified')),
  subtotal     numeric(12,2) NOT NULL CHECK (subtotal >= 0),
  tax_total    numeric(12,2) NOT NULL CHECK (tax_total >= 0),
  total        numeric(12,2) NOT NULL CHECK (total >= 0),
  status       text NOT NULL DEFAULT 'paid' CHECK (status IN ('paid','void')),
  created_at   timestamptz NOT NULL DEFAULT now(),
  UNIQUE (tenant_id, id),
  CHECK (total = subtotal + tax_total),
  FOREIGN KEY (tenant_id, branch_id)   REFERENCES branches (tenant_id, id),
  FOREIGN KEY (tenant_id, customer_id) REFERENCES customers (tenant_id, id),
  FOREIGN KEY (tenant_id, cashier_id)  REFERENCES users (tenant_id, id)
);
CREATE INDEX sales_date_idx ON sales (tenant_id, branch_id, created_at DESC);

ALTER TABLE memberships    ADD FOREIGN KEY (tenant_id, sale_id) REFERENCES sales (tenant_id, id);
ALTER TABLE stock_movements ADD FOREIGN KEY (tenant_id, sale_id) REFERENCES sales (tenant_id, id);

CREATE TABLE sale_lines (
  id          uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id   uuid NOT NULL,
  sale_id     uuid NOT NULL,
  product_id  uuid,
  plan_id     uuid,
  description text NOT NULL,                              -- snapshot
  quantity    numeric(12,3) NOT NULL CHECK (quantity > 0),
  unit_price  numeric(12,4) NOT NULL CHECK (unit_price >= 0),
  discount    numeric(12,2) NOT NULL DEFAULT 0 CHECK (discount >= 0),
  iva_rate    numeric(5,2)  NOT NULL,                     -- snapshot de la tarifa
  line_total  numeric(12,2) NOT NULL CHECK (line_total >= 0), -- sin IVA
  CHECK ((product_id IS NOT NULL) <> (plan_id IS NOT NULL)),
  FOREIGN KEY (tenant_id, sale_id)    REFERENCES sales (tenant_id, id) ON DELETE CASCADE,
  FOREIGN KEY (tenant_id, product_id) REFERENCES products (tenant_id, id),
  FOREIGN KEY (tenant_id, plan_id)    REFERENCES plans (tenant_id, id)
);

CREATE TABLE sale_payments (
  id        uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id uuid NOT NULL,
  sale_id   uuid NOT NULL,
  method    text NOT NULL CHECK (method IN ('cash','credit_card','debit_card','transfer','other')),
  sri_code  char(2) NOT NULL,                             -- código de forma de pago del SRI
  amount    numeric(12,2) NOT NULL CHECK (amount > 0),
  FOREIGN KEY (tenant_id, sale_id) REFERENCES sales (tenant_id, id) ON DELETE CASCADE
);

-- ---------------------------------------------------------------------------
-- Facturación (cliente de SICOFAC)
-- ---------------------------------------------------------------------------
CREATE TABLE fiscal_documents (
  id                 uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id          uuid NOT NULL,
  sale_id            uuid NOT NULL,
  doc_type           char(2) NOT NULL DEFAULT '01' CHECK (doc_type IN ('01','04','05')), -- factura, NC, ND
  related_document_id uuid,                                -- NC/ND → factura original
  status             text NOT NULL DEFAULT 'pending'
                       CHECK (status IN ('pending','submitted','authorized','rejected','failed','voided')),
  idempotency_key    uuid NOT NULL DEFAULT gen_random_uuid() UNIQUE, -- se envía a SICOFAC
  provider_invoice_id text,                                -- id en SICOFAC
  emission_point_id  uuid,
  establishment      char(3),
  emission_point     char(3),
  sequential         char(9),
  access_key         char(49) UNIQUE,
  authorization_no   text,
  authorized_at      timestamptz,
  buyer_id_type      char(2) NOT NULL CHECK (buyer_id_type IN ('04','05','06','07','08')),
  buyer_id           text NOT NULL,
  buyer_name         text NOT NULL,
  buyer_email        citext,
  buyer_address      text,
  subtotal           numeric(12,2) NOT NULL,
  tax_total          numeric(12,2) NOT NULL,
  total              numeric(12,2) NOT NULL,
  attempts           integer NOT NULL DEFAULT 0,
  next_retry_at      timestamptz,
  last_error         text,
  created_at         timestamptz NOT NULL DEFAULT now(),
  updated_at         timestamptz NOT NULL DEFAULT now(),
  UNIQUE (tenant_id, id),
  CHECK (buyer_id_type <> '07' OR buyer_id = '9999999999999'),
  CHECK (doc_type = '01' OR related_document_id IS NOT NULL),
  FOREIGN KEY (tenant_id, sale_id)            REFERENCES sales (tenant_id, id),
  FOREIGN KEY (tenant_id, related_document_id) REFERENCES fiscal_documents (tenant_id, id),
  FOREIGN KEY (tenant_id, emission_point_id)  REFERENCES emission_points (tenant_id, id)
);
CREATE INDEX fiscal_pending_idx ON fiscal_documents (next_retry_at) WHERE status IN ('pending','submitted','failed');
CREATE UNIQUE INDEX fiscal_one_invoice_per_sale ON fiscal_documents (tenant_id, sale_id) WHERE doc_type = '01' AND status <> 'voided';
CREATE UNIQUE INDEX fiscal_seq_uq ON fiscal_documents (tenant_id, doc_type, establishment, emission_point, sequential) WHERE sequential IS NOT NULL;

CREATE TABLE fiscal_document_events (
  id          bigserial PRIMARY KEY,
  tenant_id   uuid NOT NULL,
  document_id uuid NOT NULL,
  event       text NOT NULL,
  detail      jsonb,
  created_at  timestamptz NOT NULL DEFAULT now(),
  FOREIGN KEY (tenant_id, document_id) REFERENCES fiscal_documents (tenant_id, id)
);

-- Comprobante autorizado = inmutable: no se borra ni se cambia lo fiscal; se corrige con NC.
CREATE FUNCTION fiscal_documents_guard() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP = 'DELETE' THEN
    RAISE EXCEPTION 'fiscal_documents no se puede borrar (usar nota de crédito)';
  END IF;
  NEW.updated_at := now();
  IF OLD.status = 'authorized' AND (
       NEW.access_key IS DISTINCT FROM OLD.access_key OR NEW.sequential IS DISTINCT FROM OLD.sequential
    OR NEW.total IS DISTINCT FROM OLD.total OR NEW.subtotal IS DISTINCT FROM OLD.subtotal
    OR NEW.tax_total IS DISTINCT FROM OLD.tax_total OR NEW.buyer_id IS DISTINCT FROM OLD.buyer_id
    OR NEW.authorization_no IS DISTINCT FROM OLD.authorization_no OR NEW.sale_id IS DISTINCT FROM OLD.sale_id) THEN
    RAISE EXCEPTION 'comprobante autorizado es inmutable';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER fiscal_documents_guard BEFORE UPDATE OR DELETE ON fiscal_documents
  FOR EACH ROW EXECUTE FUNCTION fiscal_documents_guard();

-- ---------------------------------------------------------------------------
-- Auditoría
-- ---------------------------------------------------------------------------
CREATE TABLE audit_log (
  id         bigserial PRIMARY KEY,
  tenant_id  uuid NOT NULL,
  user_id    uuid,
  action     text NOT NULL,
  entity     text NOT NULL,
  entity_id  text,
  before     jsonb,
  after      jsonb,
  created_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX audit_entity_idx ON audit_log (tenant_id, entity, entity_id);

-- ---------------------------------------------------------------------------
-- Row-Level Security: toda tabla con tenant_id se aísla por app.tenant_id
-- ---------------------------------------------------------------------------
DO $$
DECLARE t text;
BEGIN
  FOR t IN
    SELECT c.table_name FROM information_schema.columns c
    JOIN information_schema.tables tb USING (table_schema, table_name)
    WHERE c.table_schema = 'public' AND c.column_name = 'tenant_id' AND tb.table_type = 'BASE TABLE'
  LOOP
    EXECUTE format('ALTER TABLE %I ENABLE ROW LEVEL SECURITY', t);
    EXECUTE format('ALTER TABLE %I FORCE ROW LEVEL SECURITY', t);
    EXECUTE format($p$CREATE POLICY tenant_isolation ON %I
        USING      (tenant_id = nullif(current_setting('app.tenant_id', true), '')::uuid)
        WITH CHECK (tenant_id = nullif(current_setting('app.tenant_id', true), '')::uuid)$p$, t);
  END LOOP;
END $$;

-- tenants: cada tenant solo ve su fila
ALTER TABLE tenants ENABLE ROW LEVEL SECURITY;
ALTER TABLE tenants FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_self ON tenants
  USING (id = nullif(current_setting('app.tenant_id', true), '')::uuid);

-- users: tenant_id NULL (platform_admin) solo visible para fitos_platform (BYPASSRLS); ya cubierto
-- por la política genérica, que no devuelve filas con tenant_id NULL.

GRANT USAGE ON SCHEMA public TO fitos_app, fitos_platform;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO fitos_app, fitos_platform;
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO fitos_app, fitos_platform;
-- Registros que la app no debe borrar:
REVOKE DELETE ON audit_log, fiscal_documents, fiscal_document_events, stock_movements FROM fitos_app;
