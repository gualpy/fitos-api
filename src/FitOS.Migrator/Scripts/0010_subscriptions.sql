-- Suscripciones de los gimnasios, correo de los usuarios y enlaces de un solo uso para crear la contraseña.
--
-- La plataforma opera sobre TODOS los gimnasios, y la API no salta RLS: por eso todo lo de plataforma
-- (listar, extender, suspender, ingresos) vive en funciones SECURITY DEFINER del rol fitos_platform, igual que el login.

-- Último día con servicio (inclusive). El valor por defecto es el período de prueba; el alta lo fija desde la configuración.
ALTER TABLE tenants ADD COLUMN subscription_ends_on date NOT NULL DEFAULT (current_date + 30);

ALTER TABLE users ADD COLUMN email citext;
ALTER TABLE users ADD CONSTRAINT users_email_format CHECK (email IS NULL OR (length(email) <= 254 AND email ~ '^[^@\s]+@[^@\s]+\.[^@\s]+$'));

-- Lo que la plataforma cobra por alquilar el sistema. Solo se lee y escribe mediante funciones.
CREATE TABLE subscription_payments (
  id           uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id    uuid NOT NULL REFERENCES tenants ON DELETE CASCADE,
  amount       numeric(10,2) NOT NULL CHECK (amount >= 0 AND amount <= 100000),
  months       integer NOT NULL CHECK (months BETWEEN 1 AND 24),
  period_start date NOT NULL,
  period_end   date NOT NULL,
  paid_on      date NOT NULL DEFAULT current_date,
  recorded_by  uuid REFERENCES users ON DELETE SET NULL,
  created_at   timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX subscription_payments_paid_idx ON subscription_payments (paid_on DESC);
ALTER TABLE subscription_payments ENABLE ROW LEVEL SECURITY;
ALTER TABLE subscription_payments FORCE  ROW LEVEL SECURITY;
REVOKE ALL ON subscription_payments FROM PUBLIC, fitos_app;

-- Enlaces para crear la contraseña (bienvenida). Se guarda solo el hash del token, nunca el token.
CREATE TABLE password_setup_tokens (
  id         uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  user_id    uuid NOT NULL REFERENCES users ON DELETE CASCADE,
  token_hash bytea NOT NULL UNIQUE,
  expires_at timestamptz NOT NULL,
  used_at    timestamptz,
  created_at timestamptz NOT NULL DEFAULT now()
);
ALTER TABLE password_setup_tokens ENABLE ROW LEVEL SECURITY;
ALTER TABLE password_setup_tokens FORCE  ROW LEVEL SECURITY;
REVOKE ALL ON password_setup_tokens FROM PUBLIC, fitos_app;

-- Estado real de un gimnasio: vencido el último día de servicio, deja de ser 'active'/'trial' sin necesidad de ningún proceso.
CREATE FUNCTION tenant_effective_status(p_status text, p_ends_on date, p_timezone text)
RETURNS text
LANGUAGE sql STABLE AS $$
  SELECT CASE WHEN p_status IN ('trial', 'active') AND p_ends_on < (now() AT TIME ZONE COALESCE(p_timezone, 'UTC'))::date
              THEN 'expired' ELSE p_status END;
$$;

CREATE OR REPLACE FUNCTION auth_find_user(p_tenant_slug text, p_username text)
RETURNS TABLE (
  user_id              uuid,
  tenant_id            uuid,
  password_hash        text,
  role                 text,
  display_name         text,
  customer_id          uuid,
  is_active            boolean,
  is_locked            boolean,
  tenant_status        text,
  must_change_password boolean
)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = public, pg_temp AS $$
  SELECT u.id, u.tenant_id, u.password_hash, u.role, u.display_name, u.customer_id,
         u.is_active,
         COALESCE(u.locked_until > now(), false),
         tenant_effective_status(t.status, t.subscription_ends_on, ts.timezone),
         u.must_change_password
  FROM users u
  LEFT JOIN tenants t ON t.id = u.tenant_id
  LEFT JOIN tenant_settings ts ON ts.tenant_id = t.id
  WHERE u.username = p_username::citext
    AND (   (p_tenant_slug IS NULL AND u.tenant_id IS NULL)
         OR (p_tenant_slug IS NOT NULL AND t.slug = p_tenant_slug::citext));
$$;

CREATE OR REPLACE FUNCTION auth_session_refresh(p_old_hash bytea, p_new_hash bytea, p_idle interval)
RETURNS TABLE (
  outcome              text,
  session_id           uuid,
  refresh_expires_at   timestamptz,
  user_id              uuid,
  tenant_id            uuid,
  role                 text,
  display_name         text,
  customer_id          uuid,
  must_change_password boolean,
  tenant_status        text
)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp AS $$
DECLARE
  t refresh_tokens%ROWTYPE;
  s auth_sessions%ROWTYPE;
  u users%ROWTYPE;
  v_tenant_status text;
  v_refresh timestamptz;
BEGIN
  SELECT * INTO t FROM refresh_tokens WHERE token_hash = p_old_hash FOR UPDATE;
  IF NOT FOUND THEN
    RETURN QUERY SELECT 'invalid'::text, NULL::uuid, NULL::timestamptz, NULL::uuid, NULL::uuid, NULL::text, NULL::text, NULL::uuid, NULL::boolean, NULL::text;
    RETURN;
  END IF;

  SELECT * INTO s FROM auth_sessions WHERE id = t.session_id FOR UPDATE;
  IF s.revoked_at IS NOT NULL OR s.expires_at <= now() THEN
    RETURN QUERY SELECT 'expired'::text, NULL::uuid, NULL::timestamptz, NULL::uuid, NULL::uuid, NULL::text, NULL::text, NULL::uuid, NULL::boolean, NULL::text;
    RETURN;
  END IF;

  IF t.used_at IS NOT NULL THEN
    IF t.used_at > now() - interval '10 seconds' THEN
      RETURN QUERY SELECT 'concurrent'::text, NULL::uuid, NULL::timestamptz, NULL::uuid, NULL::uuid, NULL::text, NULL::text, NULL::uuid, NULL::boolean, NULL::text;
    ELSE
      UPDATE auth_sessions SET revoked_at = now(), revoked_reason = 'refresh_reuse' WHERE id = s.id;
      RETURN QUERY SELECT 'reuse'::text, s.id, NULL::timestamptz, s.user_id, NULL::uuid, NULL::text, NULL::text, NULL::uuid, NULL::boolean, NULL::text;
    END IF;
    RETURN;
  END IF;

  IF t.expires_at <= now() THEN
    RETURN QUERY SELECT 'expired'::text, NULL::uuid, NULL::timestamptz, NULL::uuid, NULL::uuid, NULL::text, NULL::text, NULL::uuid, NULL::boolean, NULL::text;
    RETURN;
  END IF;

  SELECT * INTO u FROM users WHERE id = s.user_id;
  SELECT tenant_effective_status(tn.status, tn.subscription_ends_on, ts.timezone) INTO v_tenant_status
    FROM tenants tn LEFT JOIN tenant_settings ts ON ts.tenant_id = tn.id WHERE tn.id = u.tenant_id;
  IF NOT u.is_active OR (u.tenant_id IS NOT NULL AND v_tenant_status NOT IN ('active', 'trial')) THEN
    UPDATE auth_sessions SET revoked_at = now(), revoked_reason = 'inactive' WHERE id = s.id;
    RETURN QUERY SELECT 'inactive'::text, NULL::uuid, NULL::timestamptz, NULL::uuid, NULL::uuid, NULL::text, NULL::text, NULL::uuid, NULL::boolean, NULL::text;
    RETURN;
  END IF;

  UPDATE refresh_tokens SET used_at = now() WHERE id = t.id;
  v_refresh := least(now() + p_idle, s.expires_at);
  INSERT INTO refresh_tokens (session_id, token_hash, expires_at) VALUES (s.id, p_new_hash, v_refresh);

  RETURN QUERY SELECT 'ok'::text, s.id, v_refresh, u.id, u.tenant_id, u.role, u.display_name, u.customer_id, u.must_change_password, v_tenant_status;
END $$;


-- Canjea un enlace de creación de contraseña. Resultados: ok | invalid | expired. Un enlace sirve una sola vez.
CREATE FUNCTION auth_setup_token_redeem(p_hash bytea, p_password_hash text)
RETURNS TABLE (outcome text, user_id uuid, tenant_id uuid)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp AS $$
DECLARE
  t password_setup_tokens%ROWTYPE;
  u users%ROWTYPE;
BEGIN
  SELECT * INTO t FROM password_setup_tokens WHERE token_hash = p_hash FOR UPDATE;
  IF NOT FOUND OR t.used_at IS NOT NULL THEN
    RETURN QUERY SELECT 'invalid'::text, NULL::uuid, NULL::uuid;
    RETURN;
  END IF;
  IF t.expires_at <= now() THEN
    RETURN QUERY SELECT 'expired'::text, NULL::uuid, NULL::uuid;
    RETURN;
  END IF;

  UPDATE password_setup_tokens SET used_at = now() WHERE id = t.id;
  UPDATE users
     SET password_hash = p_password_hash, must_change_password = false, failed_login_count = 0, locked_until = NULL
   WHERE id = t.user_id
  RETURNING * INTO u;
  -- Una contraseña nueva cierra las sesiones que hubiera.
  UPDATE auth_sessions SET revoked_at = now(), revoked_reason = 'password_changed' WHERE auth_sessions.user_id = u.id AND revoked_at IS NULL;

  RETURN QUERY SELECT 'ok'::text, u.id, u.tenant_id;
END $$;

-- Emite un enlace nuevo y anula los anteriores sin usar del mismo usuario.
CREATE FUNCTION auth_setup_token_issue(p_user uuid, p_hash bytea, p_expires_at timestamptz)
RETURNS void
LANGUAGE sql SECURITY DEFINER SET search_path = public, pg_temp AS $$
  WITH cleared AS (DELETE FROM password_setup_tokens WHERE user_id = p_user AND used_at IS NULL)
  INSERT INTO password_setup_tokens (user_id, token_hash, expires_at) VALUES (p_user, p_hash, p_expires_at);
$$;

-- ---------------------------------------------------------------------------
-- Operaciones de plataforma
-- ---------------------------------------------------------------------------
CREATE FUNCTION platform_list_tenants()
RETURNS TABLE (
  id                   uuid,
  name                 text,
  slug                 text,
  status               text,
  effective_status     text,
  subscription_ends_on date,
  days_left            integer,
  created_at           timestamptz,
  customers            bigint
)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = public, pg_temp AS $$
  SELECT t.id, t.name, t.slug::text, t.status,
         tenant_effective_status(t.status, t.subscription_ends_on, ts.timezone),
         t.subscription_ends_on,
         (t.subscription_ends_on - (now() AT TIME ZONE COALESCE(ts.timezone, 'UTC'))::date)::integer,
         t.created_at,
         (SELECT count(*) FROM customers c WHERE c.tenant_id = t.id)
  FROM tenants t
  LEFT JOIN tenant_settings ts ON ts.tenant_id = t.id
  ORDER BY t.created_at DESC;
$$;

-- Suma meses al servicio: desde el día de vencimiento si aún está vigente, o desde hoy si ya venció. Devuelve el nuevo último día.
CREATE FUNCTION platform_extend_subscription(p_tenant uuid, p_months integer, p_amount numeric, p_actor uuid)
RETURNS date
LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp AS $$
DECLARE
  t tenants%ROWTYPE;
  v_today date;
  v_base date;
  v_end date;
BEGIN
  SELECT * INTO t FROM tenants WHERE id = p_tenant FOR UPDATE;
  IF NOT FOUND THEN
    RETURN NULL;
  END IF;

  SELECT (now() AT TIME ZONE COALESCE((SELECT timezone FROM tenant_settings WHERE tenant_id = p_tenant), 'UTC'))::date INTO v_today;
  v_base := greatest(t.subscription_ends_on, v_today);
  v_end := (v_base + make_interval(months => p_months))::date;

  UPDATE tenants
     SET subscription_ends_on = v_end,
         status = CASE WHEN status = 'trial' THEN 'active' ELSE status END
   WHERE id = p_tenant;

  INSERT INTO subscription_payments (tenant_id, amount, months, period_start, period_end, paid_on, recorded_by)
  VALUES (p_tenant, p_amount, p_months, v_base, v_end, v_today, p_actor);

  INSERT INTO audit_log (tenant_id, user_id, action, entity, entity_id, before, after)
  VALUES (p_tenant, p_actor, 'subscription.extended', 'tenant', p_tenant::text,
          jsonb_build_object('endsOn', t.subscription_ends_on),
          jsonb_build_object('endsOn', v_end, 'months', p_months, 'amount', p_amount));
  RETURN v_end;
END $$;

-- Suspende o reactiva a mano (p_status: 'active' | 'suspended'). Devuelve false si el gimnasio no existe.
CREATE FUNCTION platform_set_tenant_status(p_tenant uuid, p_status text, p_actor uuid)
RETURNS boolean
LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp AS $$
DECLARE
  v_before text;
BEGIN
  IF p_status NOT IN ('active', 'suspended') THEN
    RAISE EXCEPTION 'invalid status %', p_status;
  END IF;

  SELECT status INTO v_before FROM tenants WHERE id = p_tenant FOR UPDATE;
  IF NOT FOUND THEN
    RETURN false;
  END IF;

  UPDATE tenants SET status = p_status WHERE id = p_tenant;
  UPDATE auth_sessions SET revoked_at = now(), revoked_reason = 'tenant_suspended'
   WHERE p_status = 'suspended' AND revoked_at IS NULL AND user_id IN (SELECT id FROM users WHERE tenant_id = p_tenant);

  INSERT INTO audit_log (tenant_id, user_id, action, entity, entity_id, before, after)
  VALUES (p_tenant, p_actor, 'tenant.status_changed', 'tenant', p_tenant::text,
          jsonb_build_object('status', v_before), jsonb_build_object('status', p_status));
  RETURN true;
END $$;

-- Ingresos de la plataforma por mes (los últimos p_months, el mes en curso incluido).
CREATE FUNCTION platform_revenue_by_month(p_months integer)
RETURNS TABLE (month date, total numeric, payments bigint)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = public, pg_temp AS $$
  SELECT date_trunc('month', paid_on)::date, sum(amount), count(*)
  FROM subscription_payments
  WHERE paid_on >= (date_trunc('month', current_date) - make_interval(months => p_months - 1))::date
  GROUP BY 1
  ORDER BY 1 DESC;
$$;

-- El dueño de un gimnasio (el primero que se creó), para reenviarle el enlace de bienvenida.
CREATE FUNCTION platform_find_owner(p_tenant uuid)
RETURNS TABLE (user_id uuid, username text, display_name text, email text, must_change_password boolean, gym_name text, gym_slug text)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = public, pg_temp AS $$
  SELECT u.id, u.username::text, u.display_name, u.email::text, u.must_change_password, t.name, t.slug::text
  FROM users u JOIN tenants t ON t.id = u.tenant_id
  WHERE u.tenant_id = p_tenant AND u.role = 'owner'
  ORDER BY u.created_at
  LIMIT 1;
$$;

ALTER FUNCTION auth_setup_token_redeem(bytea, text)                          OWNER TO fitos_platform;
ALTER FUNCTION auth_setup_token_issue(uuid, bytea, timestamptz)              OWNER TO fitos_platform;
ALTER FUNCTION platform_list_tenants()                                       OWNER TO fitos_platform;
ALTER FUNCTION platform_extend_subscription(uuid, integer, numeric, uuid)    OWNER TO fitos_platform;
ALTER FUNCTION platform_set_tenant_status(uuid, text, uuid)                  OWNER TO fitos_platform;
ALTER FUNCTION platform_revenue_by_month(integer)                            OWNER TO fitos_platform;
ALTER FUNCTION platform_find_owner(uuid)                                     OWNER TO fitos_platform;

REVOKE ALL ON FUNCTION auth_setup_token_redeem(bytea, text)                       FROM PUBLIC;
REVOKE ALL ON FUNCTION auth_setup_token_issue(uuid, bytea, timestamptz)           FROM PUBLIC;
REVOKE ALL ON FUNCTION platform_list_tenants()                                    FROM PUBLIC;
REVOKE ALL ON FUNCTION platform_extend_subscription(uuid, integer, numeric, uuid) FROM PUBLIC;
REVOKE ALL ON FUNCTION platform_set_tenant_status(uuid, text, uuid)               FROM PUBLIC;
REVOKE ALL ON FUNCTION platform_revenue_by_month(integer)                         FROM PUBLIC;
REVOKE ALL ON FUNCTION platform_find_owner(uuid)                                  FROM PUBLIC;
GRANT EXECUTE ON FUNCTION tenant_effective_status(text, date, text)                    TO fitos_app, fitos_platform;
GRANT EXECUTE ON FUNCTION auth_setup_token_redeem(bytea, text)                         TO fitos_app;
GRANT EXECUTE ON FUNCTION auth_setup_token_issue(uuid, bytea, timestamptz)             TO fitos_app;
GRANT EXECUTE ON FUNCTION platform_list_tenants()                                      TO fitos_app;
GRANT EXECUTE ON FUNCTION platform_extend_subscription(uuid, integer, numeric, uuid)   TO fitos_app;
GRANT EXECUTE ON FUNCTION platform_set_tenant_status(uuid, text, uuid)                 TO fitos_app;
GRANT EXECUTE ON FUNCTION platform_revenue_by_month(integer)                           TO fitos_app;
GRANT EXECUTE ON FUNCTION platform_find_owner(uuid)                                    TO fitos_app;
