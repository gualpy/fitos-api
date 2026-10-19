-- Productos y ventas: reglas que la base garantiza aunque la aplicación falle.

-- Dos productos del mismo gimnasio no pueden llamarse igual (sin distinguir mayúsculas).
CREATE UNIQUE INDEX products_name_uq ON products (tenant_id, lower(name));

-- Un código de barras identifica a un solo producto del gimnasio.
CREATE UNIQUE INDEX products_barcode_uq ON products (tenant_id, barcode) WHERE barcode IS NOT NULL;

-- Tarifas de IVA que SICOFAC sabe facturar, y topes razonables.
ALTER TABLE products ADD CONSTRAINT products_iva_rate_valid CHECK (iva_rate IN (0, 5, 15));
ALTER TABLE products ADD CONSTRAINT products_price_valid    CHECK (price <= 100000);

-- Historial de un producto, del más reciente al más antiguo.
CREATE INDEX stock_movements_product_idx ON stock_movements (tenant_id, product_id, created_at DESC);

-- Detalle y pagos de una venta.
CREATE INDEX sale_lines_sale_idx    ON sale_lines    (tenant_id, sale_id);
CREATE INDEX sale_payments_sale_idx ON sale_payments (tenant_id, sale_id);
