-- Un administrador de plataforma también puede iniciar sesión con su correo y restablecer su contraseña por correo
-- (igual que las cuentas de un gimnasio). Solo cambian las ramas de plataforma (sin slug); el correo es único entre
-- administradores de plataforma por el índice parcial de abajo.
CREATE UNIQUE INDEX IF NOT EXISTS users_platform_email_uq ON users (email) WHERE tenant_id IS NULL AND email IS NOT NULL;

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
          WHEN p_tenant_slug IS NULL AND position('@' in p_username) > 0 THEN u.tenant_id IS NULL AND u.email = p_username::citext
          WHEN p_tenant_slug IS NULL THEN u.tenant_id IS NULL AND u.username = p_username::citext
          WHEN position('@' in p_username) > 0 THEN t.slug = p_tenant_slug::citext AND u.email = p_username::citext
          ELSE t.slug = p_tenant_slug::citext AND u.username = p_username::citext
        END;
$$;

-- Quién pidió restablecer: una cuenta de un gimnasio (por su slug) o, sin slug, un administrador de plataforma.
CREATE OR REPLACE FUNCTION auth_find_user_for_reset(p_tenant_slug text, p_identifier text)
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
         CASE WHEN u.tenant_id IS NULL THEN 'active' ELSE tenant_effective_status(t.status, t.subscription_ends_on, ts.timezone) END,
         COALESCE(t.name, 'FitOS'), COALESCE(t.slug::text, 'platform')
  FROM users u
  LEFT JOIN tenants t ON t.id = u.tenant_id
  LEFT JOIN tenant_settings ts ON ts.tenant_id = t.id
  WHERE CASE
          WHEN p_tenant_slug IS NULL THEN u.tenant_id IS NULL AND u.role = 'platform_admin'
               AND CASE WHEN position('@' in p_identifier) > 0 THEN u.email = p_identifier::citext ELSE u.username = p_identifier::citext END
          ELSE t.slug = p_tenant_slug::citext
               AND CASE WHEN position('@' in p_identifier) > 0 THEN u.email = p_identifier::citext ELSE u.username = p_identifier::citext END
        END;
$$;
