-- Sesiones revocables: token de acceso corto + token de renovación que rota en cada uso.
--
-- Las tablas solo las toca la aplicación a través de funciones SECURITY DEFINER (como el login): el rol de la API
-- no puede leerlas ni escribirlas directamente, así que un fallo de inyección SQL en la API no expone los tokens.
-- El token en claro NUNCA se guarda: solo su SHA-256 (el token es aleatorio de 256 bits, no hace falta sal).

CREATE TABLE auth_sessions (
  id             uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  user_id        uuid NOT NULL REFERENCES users ON DELETE CASCADE,
  created_at     timestamptz NOT NULL DEFAULT now(),
  expires_at     timestamptz NOT NULL,            -- vida máxima absoluta de la sesión
  revoked_at     timestamptz,
  revoked_reason text,
  user_agent     text
);
CREATE INDEX auth_sessions_user_idx    ON auth_sessions (user_id) WHERE revoked_at IS NULL;
CREATE INDEX auth_sessions_expires_idx ON auth_sessions (expires_at);

CREATE TABLE refresh_tokens (
  id          uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  session_id  uuid NOT NULL REFERENCES auth_sessions ON DELETE CASCADE,
  token_hash  bytea NOT NULL UNIQUE CHECK (octet_length(token_hash) = 32),
  created_at  timestamptz NOT NULL DEFAULT now(),
  expires_at  timestamptz NOT NULL,               -- inactividad máxima (se renueva al rotar)
  used_at     timestamptz                         -- ya canjeado: presentarlo otra vez es señal de robo
);
CREATE INDEX refresh_tokens_session_idx ON refresh_tokens (session_id);

-- Nada de acceso directo para la API: RLS sin políticas + sin permisos.
ALTER TABLE auth_sessions  ENABLE ROW LEVEL SECURITY;
ALTER TABLE auth_sessions  FORCE  ROW LEVEL SECURITY;
ALTER TABLE refresh_tokens ENABLE ROW LEVEL SECURITY;
ALTER TABLE refresh_tokens FORCE  ROW LEVEL SECURITY;
REVOKE ALL ON auth_sessions, refresh_tokens FROM PUBLIC, fitos_app;

CREATE FUNCTION auth_session_create(p_user uuid, p_token_hash bytea, p_idle interval, p_max interval, p_user_agent text)
RETURNS TABLE (session_id uuid, refresh_expires_at timestamptz)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp AS $$
DECLARE
  v_session uuid;
  v_expires timestamptz;
  v_refresh timestamptz;
BEGIN
  -- Limpieza oportunista de sesiones muertas hace más de 30 días (arrastra sus tokens).
  DELETE FROM auth_sessions WHERE expires_at < now() - interval '30 days';

  INSERT INTO auth_sessions (user_id, expires_at, user_agent)
  VALUES (p_user, now() + p_max, left(p_user_agent, 300))
  RETURNING id, expires_at INTO v_session, v_expires;

  v_refresh := least(now() + p_idle, v_expires);
  INSERT INTO refresh_tokens (session_id, token_hash, expires_at) VALUES (v_session, p_token_hash, v_refresh);

  RETURN QUERY SELECT v_session, v_refresh;
END $$;

-- Canjea un token de renovación por otro nuevo. Resultados: ok | invalid | expired | reuse | concurrent | inactive.
--   reuse      = el token ya se había canjeado hace más de 10 s: alguien lo copió -> se revoca TODA la sesión.
--   concurrent = canjeado hace menos de 10 s (dos peticiones a la vez del mismo cliente): se rechaza sin revocar.
CREATE FUNCTION auth_session_refresh(p_old_hash bytea, p_new_hash bytea, p_idle interval)
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
  SELECT tn.status INTO v_tenant_status FROM tenants tn WHERE tn.id = u.tenant_id;
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

CREATE FUNCTION auth_session_revoke(p_session uuid, p_reason text)
RETURNS void
LANGUAGE sql SECURITY DEFINER SET search_path = public, pg_temp AS $$
  UPDATE auth_sessions SET revoked_at = now(), revoked_reason = p_reason WHERE id = p_session AND revoked_at IS NULL;
$$;

-- Cambiar la contraseña cierra TODAS las sesiones del usuario (quien la robó deja de entrar).
CREATE OR REPLACE FUNCTION auth_set_password(p_user uuid, p_hash text)
RETURNS void
LANGUAGE sql SECURITY DEFINER SET search_path = public, pg_temp AS $$
  UPDATE auth_sessions SET revoked_at = now(), revoked_reason = 'password_changed' WHERE user_id = p_user AND revoked_at IS NULL;
  UPDATE users
     SET password_hash = p_hash, must_change_password = false, failed_login_count = 0, locked_until = NULL
   WHERE id = p_user;
$$;

ALTER FUNCTION auth_session_create(uuid, bytea, interval, interval, text) OWNER TO fitos_platform;
ALTER FUNCTION auth_session_refresh(bytea, bytea, interval)               OWNER TO fitos_platform;
ALTER FUNCTION auth_session_revoke(uuid, text)                            OWNER TO fitos_platform;

REVOKE ALL ON FUNCTION auth_session_create(uuid, bytea, interval, interval, text) FROM PUBLIC;
REVOKE ALL ON FUNCTION auth_session_refresh(bytea, bytea, interval)               FROM PUBLIC;
REVOKE ALL ON FUNCTION auth_session_revoke(uuid, text)                            FROM PUBLIC;
GRANT EXECUTE ON FUNCTION auth_session_create(uuid, bytea, interval, interval, text) TO fitos_app;
GRANT EXECUTE ON FUNCTION auth_session_refresh(bytea, bytea, interval)               TO fitos_app;
GRANT EXECUTE ON FUNCTION auth_session_revoke(uuid, text)                            TO fitos_app;
