-- Alta de gimnasios: API key cifrada, cambio obligatorio de contraseña, slugs válidos.

-- Un slug es un subdominio: formato fijo y sin nombres reservados (ver TenantSlug en el dominio).
ALTER TABLE tenants
  ADD CONSTRAINT tenants_slug_valid CHECK (
    slug::text ~ '^[a-z0-9][a-z0-9-]{0,62}$'
    AND slug::text NOT IN ('www', 'admin', 'api', 'platform'));

-- La API key de SICOFAC va cifrada (AES-GCM, ligada al tenant); aquí solo hay texto cifrado.
ALTER TABLE tenant_fiscal_config
  ADD COLUMN provider_api_key_enc  bytea,
  ADD COLUMN provider_api_key_hint varchar(8),   -- últimos caracteres, solo para mostrar "••••abcd"
  ADD CONSTRAINT tenant_fiscal_ruc_format CHECK (country <> 'EC' OR ruc ~ '^[0-9]{13}$');

ALTER TABLE users ADD COLUMN must_change_password boolean NOT NULL DEFAULT false;

-- Cambia el tipo de retorno (añade must_change_password): hay que recrearla.
DROP FUNCTION auth_find_user(text, text);

CREATE FUNCTION auth_find_user(p_tenant_slug text, p_username text)
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
         t.status,
         u.must_change_password
  FROM users u
  LEFT JOIN tenants t ON t.id = u.tenant_id
  WHERE u.username = p_username::citext
    AND (   (p_tenant_slug IS NULL AND u.tenant_id IS NULL)
         OR (p_tenant_slug IS NOT NULL AND t.slug = p_tenant_slug::citext));
$$;

CREATE FUNCTION auth_get_password_hash(p_user uuid)
RETURNS text
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = public, pg_temp AS $$
  SELECT password_hash FROM users WHERE id = p_user;
$$;

CREATE FUNCTION auth_set_password(p_user uuid, p_hash text)
RETURNS void
LANGUAGE sql SECURITY DEFINER SET search_path = public, pg_temp AS $$
  UPDATE users
     SET password_hash = p_hash, must_change_password = false, failed_login_count = 0, locked_until = NULL
   WHERE id = p_user;
$$;

ALTER FUNCTION auth_find_user(text, text)      OWNER TO fitos_platform;
ALTER FUNCTION auth_get_password_hash(uuid)    OWNER TO fitos_platform;
ALTER FUNCTION auth_set_password(uuid, text)   OWNER TO fitos_platform;

REVOKE ALL ON FUNCTION auth_find_user(text, text)    FROM PUBLIC;
REVOKE ALL ON FUNCTION auth_get_password_hash(uuid)  FROM PUBLIC;
REVOKE ALL ON FUNCTION auth_set_password(uuid, text) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION auth_find_user(text, text)    TO fitos_app;
GRANT EXECUTE ON FUNCTION auth_get_password_hash(uuid)  TO fitos_app;
GRANT EXECUTE ON FUNCTION auth_set_password(uuid, text) TO fitos_app;
