-- Configuración del servidor de correo, editable desde el panel de plataforma (una sola fila para todo el sistema).
--
-- La contraseña va CIFRADA (AES-GCM, la misma clave de Secrets:EncryptionKey que protege las API keys de SICOFAC): un hash no
-- serviría porque el servidor SMTP necesita la contraseña real. El rol de la API no lee la tabla directamente.

CREATE TABLE email_settings (
  id            smallint PRIMARY KEY DEFAULT 1 CHECK (id = 1),
  host          text NOT NULL CHECK (length(host) BETWEEN 1 AND 255),
  port          integer NOT NULL CHECK (port BETWEEN 1 AND 65535),
  security      text NOT NULL CHECK (security IN ('ssl_on_connect', 'start_tls')),
  username      text NOT NULL DEFAULT '' CHECK (length(username) <= 254),
  from_address  citext NOT NULL CHECK (length(from_address) <= 254),
  password_enc  bytea,
  password_hint varchar(8),
  updated_at    timestamptz NOT NULL DEFAULT now(),
  updated_by    uuid REFERENCES users ON DELETE SET NULL
);
ALTER TABLE email_settings ENABLE ROW LEVEL SECURITY;
ALTER TABLE email_settings FORCE  ROW LEVEL SECURITY;
REVOKE ALL ON email_settings FROM PUBLIC, fitos_app;

-- Lo lee cualquier envío de correo (también el de "olvidé mi contraseña", sin sesión): devuelve la contraseña ya cifrada.
CREATE FUNCTION email_settings_get()
RETURNS TABLE (host text, port integer, security text, username text, from_address text, password_enc bytea, password_hint text, updated_at timestamptz)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = public, pg_temp AS $$
  SELECT host, port, security, username, from_address::text, password_enc, password_hint::text, updated_at FROM email_settings WHERE id = 1;
$$;

-- Guarda la configuración. p_password_enc NULL = conservar la contraseña actual (falla si todavía no hay ninguna fila).
CREATE FUNCTION email_settings_save(
  p_host text, p_port integer, p_security text, p_username text, p_from_address text,
  p_password_enc bytea, p_password_hint text, p_actor uuid)
RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp AS $$
BEGIN
  IF p_password_enc IS NULL AND NOT EXISTS (SELECT 1 FROM email_settings WHERE id = 1) THEN
    RAISE EXCEPTION 'password required for the first save';
  END IF;

  INSERT INTO email_settings (id, host, port, security, username, from_address, password_enc, password_hint, updated_at, updated_by)
  VALUES (1, p_host, p_port, p_security, p_username, p_from_address, p_password_enc, p_password_hint, now(), p_actor)
  ON CONFLICT (id) DO UPDATE
     SET host = EXCLUDED.host, port = EXCLUDED.port, security = EXCLUDED.security, username = EXCLUDED.username,
         from_address = EXCLUDED.from_address,
         password_enc = COALESCE(EXCLUDED.password_enc, email_settings.password_enc),
         password_hint = CASE WHEN EXCLUDED.password_enc IS NULL THEN email_settings.password_hint ELSE EXCLUDED.password_hint END,
         updated_at = now(), updated_by = EXCLUDED.updated_by;
END $$;

-- Para mostrar el nombre del gimnasio en la página del enlace, sin gastarlo. Sin fila = enlace inválido, usado o vencido.
CREATE FUNCTION auth_setup_token_info(p_hash bytea)
RETURNS TABLE (display_name text, gym_name text, gym_slug text)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = public, pg_temp AS $$
  SELECT u.display_name, t.name, t.slug::text
  FROM password_setup_tokens k
  JOIN users u ON u.id = k.user_id
  LEFT JOIN tenants t ON t.id = u.tenant_id
  WHERE k.token_hash = p_hash AND k.used_at IS NULL AND k.expires_at > now();
$$;

ALTER FUNCTION email_settings_get()                                                   OWNER TO fitos_platform;
ALTER FUNCTION email_settings_save(text, integer, text, text, text, bytea, text, uuid) OWNER TO fitos_platform;
ALTER FUNCTION auth_setup_token_info(bytea)                                           OWNER TO fitos_platform;

REVOKE ALL ON FUNCTION email_settings_get()                                                   FROM PUBLIC;
REVOKE ALL ON FUNCTION email_settings_save(text, integer, text, text, text, bytea, text, uuid) FROM PUBLIC;
REVOKE ALL ON FUNCTION auth_setup_token_info(bytea)                                           FROM PUBLIC;
GRANT EXECUTE ON FUNCTION email_settings_get()                                                   TO fitos_app;
GRANT EXECUTE ON FUNCTION email_settings_save(text, integer, text, text, text, bytea, text, uuid) TO fitos_app;
GRANT EXECUTE ON FUNCTION auth_setup_token_info(bytea)                                           TO fitos_app;
