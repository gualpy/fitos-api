-- Personal del gimnasio, acceso de socios, "olvidé mi contraseña" y login por correo o usuario.

-- Un correo identifica a una sola persona dentro de un gimnasio (es lo que se escribe para entrar).
CREATE UNIQUE INDEX users_email_uq ON users (tenant_id, email) WHERE tenant_id IS NOT NULL AND email IS NOT NULL;

-- Los enlaces de contraseña sirven para dos cosas: crear la primera (bienvenida) o restablecerla.
ALTER TABLE password_setup_tokens
  ADD COLUMN purpose text NOT NULL DEFAULT 'welcome' CHECK (purpose IN ('welcome', 'reset'));

-- Emite un enlace y anula los anteriores sin usar. Con p_min_gap_seconds > 0 no emite otro si ya se emitió uno del mismo
-- tipo hace menos de ese tiempo (freno contra quien pide restablecer una y otra vez). Devuelve si emitió.
DROP FUNCTION auth_setup_token_issue(uuid, bytea, timestamptz);
CREATE FUNCTION auth_setup_token_issue(
  p_user uuid, p_hash bytea, p_expires_at timestamptz, p_purpose text DEFAULT 'welcome', p_min_gap_seconds integer DEFAULT 0)
RETURNS boolean
LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp AS $$
BEGIN
  IF p_min_gap_seconds > 0 AND EXISTS (
       SELECT 1 FROM password_setup_tokens
       WHERE user_id = p_user AND purpose = p_purpose AND used_at IS NULL
         AND created_at > now() - make_interval(secs => p_min_gap_seconds)) THEN
    RETURN false;
  END IF;

  DELETE FROM password_setup_tokens WHERE user_id = p_user AND used_at IS NULL;
  INSERT INTO password_setup_tokens (user_id, token_hash, expires_at, purpose) VALUES (p_user, p_hash, p_expires_at, p_purpose);
  RETURN true;
END $$;

-- La página del enlace distingue crear de restablecer.
DROP FUNCTION auth_setup_token_info(bytea);
CREATE FUNCTION auth_setup_token_info(p_hash bytea)
RETURNS TABLE (display_name text, gym_name text, gym_slug text, purpose text)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = public, pg_temp AS $$
  SELECT u.display_name, t.name, t.slug::text, k.purpose
  FROM password_setup_tokens k
  JOIN users u ON u.id = k.user_id
  LEFT JOIN tenants t ON t.id = u.tenant_id
  WHERE k.token_hash = p_hash AND k.used_at IS NULL AND k.expires_at > now();
$$;

-- Login: lo escrito con '@' se busca por correo, lo demás por usuario (los usuarios no pueden llevar '@'). Los administradores
-- de plataforma (sin gimnasio) entran solo por usuario.
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
  WHERE CASE
          WHEN p_tenant_slug IS NULL THEN u.tenant_id IS NULL AND u.username = p_username::citext
          WHEN position('@' in p_username) > 0 THEN t.slug = p_tenant_slug::citext AND u.email = p_username::citext
          ELSE t.slug = p_tenant_slug::citext AND u.username = p_username::citext
        END;
$$;

-- Quién pidió restablecer (solo cuentas de un gimnasio, nunca de plataforma).
CREATE FUNCTION auth_find_user_for_reset(p_tenant_slug text, p_identifier text)
RETURNS TABLE (
  user_id        uuid,
  tenant_id      uuid,
  display_name   text,
  email          text,
  role           text,
  is_active      boolean,
  tenant_status  text,
  gym_name       text,
  gym_slug       text
)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = public, pg_temp AS $$
  SELECT u.id, u.tenant_id, u.display_name, u.email::text, u.role, u.is_active,
         tenant_effective_status(t.status, t.subscription_ends_on, ts.timezone), t.name, t.slug::text
  FROM users u
  JOIN tenants t ON t.id = u.tenant_id
  LEFT JOIN tenant_settings ts ON ts.tenant_id = t.id
  WHERE p_tenant_slug IS NOT NULL
    AND t.slug = p_tenant_slug::citext
    AND CASE WHEN position('@' in p_identifier) > 0 THEN u.email = p_identifier::citext ELSE u.username = p_identifier::citext END;
$$;

-- Cierra las sesiones de un usuario de ESE gimnasio (al desactivarlo).
CREATE FUNCTION auth_user_sessions_revoke(p_tenant uuid, p_user uuid, p_reason text)
RETURNS void
LANGUAGE sql SECURITY DEFINER SET search_path = public, pg_temp AS $$
  UPDATE auth_sessions SET revoked_at = now(), revoked_reason = p_reason
  WHERE user_id = p_user AND revoked_at IS NULL
    AND EXISTS (SELECT 1 FROM users WHERE id = p_user AND tenant_id = p_tenant);
$$;

ALTER FUNCTION auth_setup_token_issue(uuid, bytea, timestamptz, text, integer) OWNER TO fitos_platform;
ALTER FUNCTION auth_setup_token_info(bytea)                                     OWNER TO fitos_platform;
ALTER FUNCTION auth_find_user_for_reset(text, text)                             OWNER TO fitos_platform;
ALTER FUNCTION auth_user_sessions_revoke(uuid, uuid, text)                      OWNER TO fitos_platform;

REVOKE ALL ON FUNCTION auth_setup_token_issue(uuid, bytea, timestamptz, text, integer) FROM PUBLIC;
REVOKE ALL ON FUNCTION auth_setup_token_info(bytea)                                     FROM PUBLIC;
REVOKE ALL ON FUNCTION auth_find_user_for_reset(text, text)                             FROM PUBLIC;
REVOKE ALL ON FUNCTION auth_user_sessions_revoke(uuid, uuid, text)                      FROM PUBLIC;
GRANT EXECUTE ON FUNCTION auth_setup_token_issue(uuid, bytea, timestamptz, text, integer) TO fitos_app;
GRANT EXECUTE ON FUNCTION auth_setup_token_info(bytea)                                     TO fitos_app;
GRANT EXECUTE ON FUNCTION auth_find_user_for_reset(text, text)                             TO fitos_app;
GRANT EXECUTE ON FUNCTION auth_user_sessions_revoke(uuid, uuid, text)                      TO fitos_app;
