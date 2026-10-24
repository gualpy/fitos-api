-- El panel de plataforma muestra si el dueño de cada gimnasio ya creó su contraseña.
--   pending = tiene cuenta y todavía no creó la contraseña (se le puede reenviar la bienvenida)
--   active  = ya la creó
--   none    = el gimnasio no tiene dueño
-- Cambia la forma de lo que devuelve la función, por eso se recrea.
DROP FUNCTION platform_list_tenants();

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
  customers            bigint,
  owner_state          text
)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = public, pg_temp AS $$
  SELECT t.id, t.name, t.slug::text, t.status,
         tenant_effective_status(t.status, t.subscription_ends_on, ts.timezone),
         t.subscription_ends_on,
         (t.subscription_ends_on - (now() AT TIME ZONE COALESCE(ts.timezone, 'UTC'))::date)::integer,
         t.created_at,
         (SELECT count(*) FROM customers c WHERE c.tenant_id = t.id),
         COALESCE(
           (SELECT CASE WHEN o.must_change_password THEN 'pending' ELSE 'active' END
              FROM users o WHERE o.tenant_id = t.id AND o.role = 'owner'
              ORDER BY o.created_at LIMIT 1),
           'none')
  FROM tenants t
  LEFT JOIN tenant_settings ts ON ts.tenant_id = t.id
  ORDER BY t.created_at DESC;
$$;

ALTER FUNCTION platform_list_tenants() OWNER TO fitos_platform;
REVOKE ALL ON FUNCTION platform_list_tenants() FROM PUBLIC;
GRANT EXECUTE ON FUNCTION platform_list_tenants() TO fitos_app;
