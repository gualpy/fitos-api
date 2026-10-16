-- Autenticación: bloqueo por intentos fallidos y funciones de login.
--
-- El login ocurre ANTES de conocer al usuario/tenant, y RLS (FORCE) ocultaría `users` y `tenants`.
-- En vez de dar a la app una conexión con BYPASSRLS, el login pasa por estas funciones
-- SECURITY DEFINER, cuyo dueño es `fitos_platform` (BYPASSRLS). La app solo puede ejecutarlas:
-- devuelven una fila de UN usuario concreto, nunca un listado.
--
-- Requisito de despliegue: el rol que ejecuta las migraciones debe ser superusuario o miembro de
-- fitos_platform (GRANT fitos_platform TO <rol_migraciones>) para poder asignar el dueño.

ALTER TABLE users
  ADD COLUMN failed_login_count integer     NOT NULL DEFAULT 0,
  ADD COLUMN locked_until       timestamptz;

CREATE FUNCTION auth_find_user(p_tenant_slug text, p_username text)
RETURNS TABLE (
  user_id       uuid,
  tenant_id     uuid,
  password_hash text,
  role          text,
  display_name  text,
  customer_id   uuid,
  is_active     boolean,
  is_locked     boolean,
  tenant_status text
)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = public, pg_temp AS $$
  SELECT u.id, u.tenant_id, u.password_hash, u.role, u.display_name, u.customer_id,
         u.is_active,
         COALESCE(u.locked_until > now(), false),
         t.status
  FROM users u
  LEFT JOIN tenants t ON t.id = u.tenant_id
  WHERE u.username = p_username::citext
    AND (   (p_tenant_slug IS NULL AND u.tenant_id IS NULL)   -- login de plataforma
         OR (p_tenant_slug IS NOT NULL AND t.slug = p_tenant_slug::citext));
$$;

CREATE FUNCTION auth_register_failure(p_user uuid, p_max_failures integer, p_lock_minutes integer)
RETURNS void
LANGUAGE sql SECURITY DEFINER SET search_path = public, pg_temp AS $$
  UPDATE users
     SET failed_login_count = failed_login_count + 1,
         locked_until = CASE WHEN failed_login_count + 1 >= p_max_failures
                             THEN now() + make_interval(mins => p_lock_minutes)
                             ELSE locked_until END
   WHERE id = p_user;
$$;

CREATE FUNCTION auth_register_success(p_user uuid)
RETURNS void
LANGUAGE sql SECURITY DEFINER SET search_path = public, pg_temp AS $$
  UPDATE users
     SET failed_login_count = 0, locked_until = NULL, last_login_at = now()
   WHERE id = p_user;
$$;

ALTER FUNCTION auth_find_user(text, text)                   OWNER TO fitos_platform;
ALTER FUNCTION auth_register_failure(uuid, integer, integer) OWNER TO fitos_platform;
ALTER FUNCTION auth_register_success(uuid)                  OWNER TO fitos_platform;

REVOKE ALL ON FUNCTION auth_find_user(text, text)                    FROM PUBLIC;
REVOKE ALL ON FUNCTION auth_register_failure(uuid, integer, integer) FROM PUBLIC;
REVOKE ALL ON FUNCTION auth_register_success(uuid)                   FROM PUBLIC;
GRANT EXECUTE ON FUNCTION auth_find_user(text, text)                    TO fitos_app;
GRANT EXECUTE ON FUNCTION auth_register_failure(uuid, integer, integer) TO fitos_app;
GRANT EXECUTE ON FUNCTION auth_register_success(uuid)                   TO fitos_app;

-- Tablas futuras heredan los permisos de las actuales (0001 solo cubre las existentes).
ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES    TO fitos_app, fitos_platform;
ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT USAGE, SELECT                  ON SEQUENCES TO fitos_app, fitos_platform;
