-- Registro de eventos de seguridad y actualización transparente del hash de contraseña.
--
-- security_events se escribe y se lee solo mediante funciones SECURITY DEFINER: el rol de la API puede AÑADIR eventos
-- pero no leerlos ni borrarlos, así que quien comprometa la API no puede ocultar sus huellas.
-- Nunca se guardan contraseñas ni nombres de usuario tecleados (la gente a veces escribe la clave en el campo usuario).

CREATE TABLE security_events (
  id         bigserial PRIMARY KEY,
  at         timestamptz NOT NULL DEFAULT now(),
  tenant_id  uuid REFERENCES tenants ON DELETE SET NULL,
  user_id    uuid REFERENCES users   ON DELETE SET NULL,
  event      text NOT NULL CHECK (event ~ '^[a-z_]{3,40}$'),
  ip         inet,
  user_agent text,
  detail     jsonb
);
CREATE INDEX security_events_at_idx     ON security_events (at DESC);
CREATE INDEX security_events_tenant_idx ON security_events (tenant_id, at DESC);
CREATE INDEX security_events_user_idx   ON security_events (user_id, at DESC);

ALTER TABLE security_events ENABLE ROW LEVEL SECURITY;
ALTER TABLE security_events FORCE  ROW LEVEL SECURITY;
REVOKE ALL ON security_events FROM PUBLIC, fitos_app;
REVOKE ALL ON SEQUENCE security_events_id_seq FROM PUBLIC, fitos_app;

CREATE FUNCTION security_event_log(p_event text, p_tenant uuid, p_user uuid, p_ip text, p_user_agent text, p_detail jsonb)
RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp AS $$
DECLARE
  v_tenant uuid := p_tenant;
  v_ip inet;
BEGIN
  -- Una IP mal formada nunca debe impedir registrar el evento.
  BEGIN
    v_ip := p_ip::inet;
  EXCEPTION WHEN OTHERS THEN
    v_ip := NULL;
  END;

  IF v_tenant IS NULL AND p_user IS NOT NULL THEN
    SELECT tenant_id INTO v_tenant FROM users WHERE id = p_user;
  END IF;

  INSERT INTO security_events (tenant_id, user_id, event, ip, user_agent, detail)
  VALUES (v_tenant, p_user, p_event, v_ip, left(p_user_agent, 300), p_detail);
END $$;

-- Ahora informa si ESTE fallo fue el que bloqueó la cuenta (para registrar "account_locked" una sola vez).
DROP FUNCTION auth_register_failure(uuid, integer, integer);
CREATE FUNCTION auth_register_failure(p_user uuid, p_max_failures integer, p_lock_minutes integer)
RETURNS boolean
LANGUAGE sql SECURITY DEFINER SET search_path = public, pg_temp AS $$
  WITH before AS (
    SELECT failed_login_count AS c, (locked_until IS NOT NULL AND locked_until > now()) AS was_locked FROM users WHERE id = p_user
  ), updated AS (
    UPDATE users
       SET failed_login_count = failed_login_count + 1,
           locked_until = CASE WHEN failed_login_count + 1 >= p_max_failures
                               THEN now() + make_interval(mins => p_lock_minutes)
                               ELSE locked_until END
     WHERE id = p_user
    RETURNING failed_login_count
  )
  SELECT COALESCE((SELECT updated.failed_login_count >= p_max_failures FROM updated) AND NOT (SELECT was_locked FROM before), false);
$$;

-- Sube el coste del hash cuando el usuario entra con uno antiguo. NO cierra sesiones ni toca otros campos.
CREATE FUNCTION auth_rehash_password(p_user uuid, p_hash text)
RETURNS void
LANGUAGE sql SECURITY DEFINER SET search_path = public, pg_temp AS $$
  UPDATE users SET password_hash = p_hash WHERE id = p_user;
$$;

ALTER FUNCTION security_event_log(text, uuid, uuid, text, text, jsonb)  OWNER TO fitos_platform;
ALTER FUNCTION auth_register_failure(uuid, integer, integer)             OWNER TO fitos_platform;
ALTER FUNCTION auth_rehash_password(uuid, text)                          OWNER TO fitos_platform;

REVOKE ALL ON FUNCTION security_event_log(text, uuid, uuid, text, text, jsonb) FROM PUBLIC;
REVOKE ALL ON FUNCTION auth_register_failure(uuid, integer, integer)            FROM PUBLIC;
REVOKE ALL ON FUNCTION auth_rehash_password(uuid, text)                         FROM PUBLIC;
GRANT EXECUTE ON FUNCTION security_event_log(text, uuid, uuid, text, text, jsonb) TO fitos_app;
GRANT EXECUTE ON FUNCTION auth_register_failure(uuid, integer, integer)            TO fitos_app;
GRANT EXECUTE ON FUNCTION auth_rehash_password(uuid, text)                         TO fitos_app;
