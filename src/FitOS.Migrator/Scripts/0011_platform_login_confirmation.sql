-- Segundo paso del login de los administradores de plataforma: un enlace de un solo uso enviado a su correo.
--
-- El enlace (vive solo en el correo) NO basta: el navegador que hizo el login recibe además un secreto propio que no sale de
-- ese navegador. Hacen falta los dos, así que abrir el correo en otro dispositivo, o que alguien lea el correo, no inicia sesión.
-- Solo se guardan los hashes de ambos.

CREATE TABLE login_challenges (
  id          uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  user_id     uuid NOT NULL REFERENCES users ON DELETE CASCADE,
  link_hash   bytea NOT NULL UNIQUE,
  secret_hash bytea NOT NULL,
  expires_at  timestamptz NOT NULL,
  used_at     timestamptz,
  created_at  timestamptz NOT NULL DEFAULT now()
);
ALTER TABLE login_challenges ENABLE ROW LEVEL SECURITY;
ALTER TABLE login_challenges FORCE  ROW LEVEL SECURITY;
REVOKE ALL ON login_challenges FROM PUBLIC, fitos_app;

-- Un usuario por id, con la misma forma que auth_find_user (el estado del gimnasio incluido).
CREATE FUNCTION auth_find_user_by_id(p_user uuid)
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
  WHERE u.id = p_user;
$$;

-- Abre un desafío para un administrador de plataforma y devuelve su correo; sin correo guardado no crea nada (devuelve NULL).
CREATE FUNCTION auth_login_challenge_create(p_user uuid, p_link_hash bytea, p_secret_hash bytea, p_expires_at timestamptz)
RETURNS text
LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp AS $$
DECLARE
  v_email text;
BEGIN
  SELECT email::text INTO v_email FROM users WHERE id = p_user AND role = 'platform_admin' AND is_active;
  IF v_email IS NULL THEN
    RETURN NULL;
  END IF;

  DELETE FROM login_challenges WHERE user_id = p_user AND used_at IS NULL;
  INSERT INTO login_challenges (user_id, link_hash, secret_hash, expires_at) VALUES (p_user, p_link_hash, p_secret_hash, p_expires_at);
  RETURN v_email;
END $$;

-- Canjea el desafío: ok | invalid | expired. Un enlace con el secreto equivocado se quema (un solo intento).
CREATE FUNCTION auth_login_challenge_redeem(p_link_hash bytea, p_secret_hash bytea)
RETURNS TABLE (outcome text, user_id uuid)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp AS $$
DECLARE
  c login_challenges%ROWTYPE;
BEGIN
  SELECT * INTO c FROM login_challenges WHERE link_hash = p_link_hash FOR UPDATE;
  IF NOT FOUND OR c.used_at IS NOT NULL THEN
    RETURN QUERY SELECT 'invalid'::text, NULL::uuid;
    RETURN;
  END IF;

  UPDATE login_challenges SET used_at = now() WHERE id = c.id;
  IF c.secret_hash <> p_secret_hash THEN
    RETURN QUERY SELECT 'invalid'::text, NULL::uuid;
    RETURN;
  END IF;
  IF c.expires_at <= now() THEN
    RETURN QUERY SELECT 'expired'::text, NULL::uuid;
    RETURN;
  END IF;

  RETURN QUERY SELECT 'ok'::text, c.user_id;
END $$;

ALTER FUNCTION auth_find_user_by_id(uuid)                               OWNER TO fitos_platform;
ALTER FUNCTION auth_login_challenge_create(uuid, bytea, bytea, timestamptz) OWNER TO fitos_platform;
ALTER FUNCTION auth_login_challenge_redeem(bytea, bytea)                OWNER TO fitos_platform;

REVOKE ALL ON FUNCTION auth_find_user_by_id(uuid)                               FROM PUBLIC;
REVOKE ALL ON FUNCTION auth_login_challenge_create(uuid, bytea, bytea, timestamptz) FROM PUBLIC;
REVOKE ALL ON FUNCTION auth_login_challenge_redeem(bytea, bytea)                FROM PUBLIC;
GRANT EXECUTE ON FUNCTION auth_find_user_by_id(uuid)                               TO fitos_app;
GRANT EXECUTE ON FUNCTION auth_login_challenge_create(uuid, bytea, bytea, timestamptz) TO fitos_app;
GRANT EXECUTE ON FUNCTION auth_login_challenge_redeem(bytea, bytea)                TO fitos_app;
