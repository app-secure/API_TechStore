using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using MongoDB.Driver;
using MongoDB.Bson;
using TechStore360.Data;

namespace TechStore360.Modules.Compras
{
    public interface IComprasRepository
    {
        Task<CompraCreada> AddAsync(MaestroCompraModel maestro, List<DetalleCompraModel> detalles, CancellationToken cancellationToken = default);
        Task<bool> UpdateStatusAsync(int numeroFactura, string nuevoEstado, CancellationToken cancellationToken = default);
        Task<CompraCompletaDto?> GetCompraCompletaAsync(int idCompra, CancellationToken cancellationToken = default);
        Task<List<CompraResumenDto>> GetAllAsync(CancellationToken cancellationToken = default);
        Task<List<CompraResumenDto>> GetByUsuarioAsync(string idUsuario, CancellationToken cancellationToken = default);
    }

    public sealed class ComprasRepository : IComprasRepository
    {
        private readonly ResilientDbExecutor _dbExecutor;

        public ComprasRepository(ResilientDbExecutor dbExecutor)
        {
            _dbExecutor = dbExecutor;
        }

        private static BsonDocument CompraToBson(int numeroFactura, MaestroCompraModel maestro, List<DetalleCompraModel> detalles)
        {
            var detailsArray = new BsonArray();
            foreach (var d in detalles)
            {
                detailsArray.Add(new BsonDocument
                {
                    { "id_producto", d.IdProducto },
                    { "cantidad", d.Cantidad },
                    { "precio_unitario", (double)d.PrecioUnitario }
                });
            }

            return new BsonDocument
            {
                { "_id", numeroFactura },
                { "id_usuario", maestro.IdUsuario },
                { "total_compra", (double)maestro.TotalCompra },
                { "estado", maestro.Estado },
                { "created_at", maestro.CreatedAt },
                { "metodo_pago", maestro.MetodoPago },
                { "lugar_entrega", maestro.LugarEntrega },
                { "requiere_factura", maestro.RequiereFactura },
                { "cedula_factura", maestro.CedulaFactura ?? "" },
                { "nombre_factura", maestro.NombreFactura ?? "" },
                { "detalles", detailsArray }
            };
        }

        private static CompraCompletaDto CompraFromBson(BsonDocument doc)
        {
            var detalles = new List<DetalleCompraDto>();
            if (doc.Contains("detalles") && doc["detalles"].IsBsonArray)
            {
                foreach (var val in doc["detalles"].AsBsonArray)
                {
                    var d = val.AsBsonDocument;
                    var qty = Convert.ToInt32(BsonTypeMapper.MapToDotNetValue(d["cantidad"]));
                    var price = Convert.ToDecimal(BsonTypeMapper.MapToDotNetValue(d["precio_unitario"]));
                    detalles.Add(new DetalleCompraDto(
                        IdProducto: Convert.ToInt32(BsonTypeMapper.MapToDotNetValue(d["id_producto"])),
                        NombreProducto: "Componente Tecnológico",
                        UrlImagen: null,
                        Cantidad: qty,
                        PrecioUnitario: price,
                        Subtotal: qty * price
                    ));
                }
            }

            var total = Convert.ToDecimal(BsonTypeMapper.MapToDotNetValue(doc["total_compra"]));
            decimal tasaIva = 0.15m;
            decimal subtotalCalculado = total / (1 + tasaIva);
            decimal ivaCalculado = total - subtotalCalculado;

            return new CompraCompletaDto(
                NumeroFactura: Convert.ToInt32(BsonTypeMapper.MapToDotNetValue(doc["_id"])),
                IdUsuario: doc["id_usuario"].AsString,
                TotalCompra: total,
                Subtotal: subtotalCalculado,
                Iva: ivaCalculado,
                Estado: doc["estado"].AsString,
                MetodoPago: doc["metodo_pago"].AsString,
                LugarEntrega: doc["lugar_entrega"].AsString,
                RequiereFactura: doc["requiere_factura"].AsBoolean,
                NombreFactura: doc.Contains("nombre_factura") ? doc["nombre_factura"].AsString : "",
                CedulaFactura: doc.Contains("cedula_factura") ? doc["cedula_factura"].AsString : "",
                CreatedAt: doc["created_at"].ToUniversalTime(),
                Detalles: detalles
            );
        }

        private static CompraResumenDto ResumenFromBson(BsonDocument doc)
        {
            return new CompraResumenDto(
                NumeroFactura: Convert.ToInt32(BsonTypeMapper.MapToDotNetValue(doc["_id"])),
                IdUsuario: doc["id_usuario"].AsString,
                TotalCompra: Convert.ToDecimal(BsonTypeMapper.MapToDotNetValue(doc["total_compra"])),
                Estado: doc["estado"].AsString,
                MetodoPago: doc["metodo_pago"].AsString,
                CreatedAt: doc["created_at"].ToUniversalTime()
            );
        }

        private async Task SyncToMongoBackgroundAsync(int numeroFactura, MaestroCompraModel maestro, List<DetalleCompraModel> detalles)
        {
            try
            {
                var collection = _dbExecutor.GetMongoDatabase().GetCollection<BsonDocument>("compras");
                var doc = CompraToBson(numeroFactura, maestro, detalles);
                await collection.ReplaceOneAsync(
                    Builders<BsonDocument>.Filter.Eq("_id", numeroFactura),
                    doc,
                    new ReplaceOptions { IsUpsert = true }
                );
            }
            catch
            {
            }
        }

        public async Task<CompraCreada> AddAsync(MaestroCompraModel maestro, List<DetalleCompraModel> detalles, CancellationToken cancellationToken = default)
        {
            var source = await _dbExecutor.GetActiveDatabaseAsync();
            _dbExecutor.CheckWriteAccess(source);

            if (source == ActiveDbSource.Supabase)
            {
                using var connection = await _dbExecutor.GetPostgresConnectionAsync();
                using var transaction = await connection.BeginTransactionAsync(cancellationToken);

                try
                {
                    const string insertMaestroSql = @"
                        INSERT INTO public.maestro_compras (
                            id_usuario, total_compra, estado, created_at, 
                            metodo_pago, lugar_entrega, requiere_factura, cedula_factura, nombre_factura
                        )
                        VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9)
                        RETURNING numero_factura;";

                    int numeroFactura;
                    using (var cmd = new NpgsqlCommand(insertMaestroSql, connection, transaction))
                    {
                        cmd.Parameters.AddWithValue(maestro.IdUsuario);
                        cmd.Parameters.AddWithValue(maestro.TotalCompra);
                        cmd.Parameters.AddWithValue(maestro.Estado);
                        cmd.Parameters.AddWithValue(maestro.CreatedAt);
                        cmd.Parameters.AddWithValue(maestro.MetodoPago);
                        cmd.Parameters.AddWithValue(maestro.LugarEntrega);
                        cmd.Parameters.AddWithValue(maestro.RequiereFactura);
                        cmd.Parameters.AddWithValue((object?)maestro.CedulaFactura ?? DBNull.Value);
                        cmd.Parameters.AddWithValue((object?)maestro.NombreFactura ?? DBNull.Value);

                        var scalarResult = await cmd.ExecuteScalarAsync(cancellationToken);
                        numeroFactura = scalarResult != null ? Convert.ToInt32(scalarResult) : throw new InvalidOperationException("No se pudo obtener el número de factura.");
                    }

                    const string insertDetalleSql = @"
                        INSERT INTO public.detalle_compras (numero_factura, id_producto, cantidad, precio_unitario)
                        VALUES ($1, $2, $3, $4);";

                    const string updateStockSql = @"
                        UPDATE public.productos 
                        SET stock = stock - $1 
                        WHERE id_producto = $2 AND estado = true AND stock >= $1;";

                    foreach (var detalle in detalles)
                    {
                        // 1. Insertar detalle de compra
                        using (var cmd = new NpgsqlCommand(insertDetalleSql, connection, transaction))
                        {
                            cmd.Parameters.AddWithValue(numeroFactura);
                            cmd.Parameters.AddWithValue(detalle.IdProducto);
                            cmd.Parameters.AddWithValue(detalle.Cantidad);
                            cmd.Parameters.AddWithValue(detalle.PrecioUnitario);
                            await cmd.ExecuteNonQueryAsync(cancellationToken);
                        }

                        // 2. Descontar stock atómicamente con control de concurrencia y verificar filas afectadas
                        using (var cmdStock = new NpgsqlCommand(updateStockSql, connection, transaction))
                        {
                            cmdStock.Parameters.AddWithValue(detalle.Cantidad);
                            cmdStock.Parameters.AddWithValue(detalle.IdProducto);
                            var rowsAffected = await cmdStock.ExecuteNonQueryAsync(cancellationToken);
                            if (rowsAffected == 0)
                            {
                                throw new InvalidOperationException($"Stock insuficiente o producto inactivo para el Producto ID: {detalle.IdProducto}.");
                            }
                        }
                    }

                    await transaction.CommitAsync(cancellationToken);
                    _ = Task.Run(() => SyncToMongoBackgroundAsync(numeroFactura, maestro, detalles));
                    return new CompraCreada(numeroFactura, maestro.TotalCompra);
                }
                catch
                {
                    await transaction.RollbackAsync(cancellationToken);
                    throw;
                }
            }
            else if (source == ActiveDbSource.MongoDB)
            {
                var numFactura = Random.Shared.Next(100000, 999999);
                var collection = _dbExecutor.GetMongoDatabase().GetCollection<BsonDocument>("compras");
                var doc = CompraToBson(numFactura, maestro, detalles);
                await collection.InsertOneAsync(doc, null, cancellationToken);
                return new CompraCreada(numFactura, maestro.TotalCompra);
            }
            throw new InvalidOperationException("Escritura no disponible.");
        }

        public async Task<bool> UpdateStatusAsync(int numeroFactura, string nuevoEstado, CancellationToken cancellationToken = default)
        {
            var source = await _dbExecutor.GetActiveDatabaseAsync();
            _dbExecutor.CheckWriteAccess(source);

            if (source == ActiveDbSource.Supabase)
            {
                using var conn = await _dbExecutor.GetPostgresConnectionAsync();
                const string sql = "UPDATE public.maestro_compras SET estado = $1 WHERE numero_factura = $2 AND estado <> $1;";
                using var cmd = new NpgsqlCommand(sql, conn);
                cmd.Parameters.AddWithValue(nuevoEstado);
                cmd.Parameters.AddWithValue(numeroFactura);
                var rowsAffected = await cmd.ExecuteNonQueryAsync(cancellationToken);
                if (rowsAffected > 0)
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var collection = _dbExecutor.GetMongoDatabase().GetCollection<BsonDocument>("compras");
                            await collection.UpdateOneAsync(
                                Builders<BsonDocument>.Filter.Eq("_id", numeroFactura),
                                Builders<BsonDocument>.Update.Set("estado", nuevoEstado)
                            );
                        }
                        catch {}
                    });
                    return true;
                }
            }
            else if (source == ActiveDbSource.MongoDB)
            {
                var collection = _dbExecutor.GetMongoDatabase().GetCollection<BsonDocument>("compras");
                var result = await collection.UpdateOneAsync(
                    Builders<BsonDocument>.Filter.Eq("_id", numeroFactura),
                    Builders<BsonDocument>.Update.Set("estado", nuevoEstado),
                    null,
                    cancellationToken
                );
                return result.ModifiedCount > 0;
            }
            return false;
        }

        public async Task<CompraCompletaDto?> GetCompraCompletaAsync(int numeroFactura, CancellationToken cancellationToken = default)
        {
            var source = await _dbExecutor.GetActiveDatabaseAsync();
            if (source == ActiveDbSource.Supabase || source == ActiveDbSource.Aiven)
            {
                try
                {
                    using var conn = await _dbExecutor.GetPostgresConnectionAsync();
                    const string maestroSql = @"
                        SELECT numero_factura, id_usuario, total_compra, estado, created_at,
                               metodo_pago, lugar_entrega, requiere_factura, cedula_factura, nombre_factura 
                        FROM public.maestro_compras 
                        WHERE numero_factura = $1;";

                    using var cmdMaestro = new NpgsqlCommand(maestroSql, conn);
                    cmdMaestro.Parameters.AddWithValue(numeroFactura);

                    int dbNumeroFactura = 0;
                    string dbIdUsuario = "";
                    decimal dbTotalCompra = 0;
                    string dbEstado = "";
                    DateTime dbCreatedAt = DateTime.MinValue;
                    string dbMetodoPago = "";
                    string dbLugarEntrega = "";
                    bool dbRequiereFactura = true;
                    string dbCedulaFactura = "";
                    string dbNombreFactura = "";

                    using (var reader = await cmdMaestro.ExecuteReaderAsync(cancellationToken))
                    {
                        if (await reader.ReadAsync(cancellationToken))
                        {
                            dbNumeroFactura = reader.GetInt32(reader.GetOrdinal("numero_factura"));
                            dbIdUsuario = reader.GetString(reader.GetOrdinal("id_usuario"));
                            dbTotalCompra = reader.GetDecimal(reader.GetOrdinal("total_compra"));
                            dbEstado = reader.GetString(reader.GetOrdinal("estado"));
                            dbCreatedAt = reader.GetDateTime(reader.GetOrdinal("created_at"));
                            dbMetodoPago = reader.GetString(reader.GetOrdinal("metodo_pago"));
                            dbLugarEntrega = reader.GetString(reader.GetOrdinal("lugar_entrega"));
                            dbRequiereFactura = reader.GetBoolean(reader.GetOrdinal("requiere_factura"));
                            dbCedulaFactura = reader.IsDBNull(reader.GetOrdinal("cedula_factura")) ? "" : reader.GetString(reader.GetOrdinal("cedula_factura"));
                            dbNombreFactura = reader.IsDBNull(reader.GetOrdinal("nombre_factura")) ? "" : reader.GetString(reader.GetOrdinal("nombre_factura"));
                        }
                        else
                        {
                            return null;
                        }
                    }

                    const string detallesSql = @"
                        SELECT d.id_producto, p.nombre, p.url_imagen, d.cantidad, d.precio_unitario 
                        FROM public.detalle_compras d
                        LEFT JOIN public.productos p ON d.id_producto = p.id_producto
                        WHERE d.numero_factura = $1;";

                    using var cmdDetalles = new NpgsqlCommand(detallesSql, conn);
                    cmdDetalles.Parameters.AddWithValue(numeroFactura);

                    var detallesDto = new List<DetalleCompraDto>();
                    using (var reader = await cmdDetalles.ExecuteReaderAsync(cancellationToken))
                    {
                        while (await reader.ReadAsync(cancellationToken))
                        {
                            var idProducto = reader.GetInt32(reader.GetOrdinal("id_producto"));
                            var nombre = reader.IsDBNull(reader.GetOrdinal("nombre")) ? "Producto no disponible" : reader.GetString(reader.GetOrdinal("nombre"));
                            var urlImagen = reader.IsDBNull(reader.GetOrdinal("url_imagen")) ? null : reader.GetString(reader.GetOrdinal("url_imagen"));
                            var cantidad = reader.GetInt32(reader.GetOrdinal("cantidad"));
                            var precioUnitario = reader.GetDecimal(reader.GetOrdinal("precio_unitario"));

                            detallesDto.Add(new DetalleCompraDto(
                                IdProducto: idProducto,
                                NombreProducto: nombre,
                                UrlImagen: urlImagen,
                                Cantidad: cantidad,
                                PrecioUnitario: precioUnitario,
                                Subtotal: precioUnitario * cantidad
                            ));
                        }
                    }

                    decimal tasaIva = 0.15m;
                    decimal subtotalCalculado = dbTotalCompra / (1 + tasaIva);
                    decimal ivaCalculado = dbTotalCompra - subtotalCalculado;

                    return new CompraCompletaDto(
                        NumeroFactura: dbNumeroFactura,
                        IdUsuario: dbIdUsuario,
                        TotalCompra: dbTotalCompra,
                        Subtotal: subtotalCalculado,
                        Iva: ivaCalculado,
                        Estado: dbEstado,
                        MetodoPago: dbMetodoPago,
                        LugarEntrega: dbLugarEntrega,
                        RequiereFactura: dbRequiereFactura,
                        NombreFactura: dbNombreFactura,
                        CedulaFactura: dbCedulaFactura,
                        CreatedAt: dbCreatedAt,
                        Detalles: detallesDto
                    );
                }
                catch when (source == ActiveDbSource.Supabase)
                {
                    return await GetCompraCompletaFromFallbackAsync(numeroFactura, cancellationToken);
                }
            }
            else
            {
                return await GetCompraCompletaFromMongoAsync(numeroFactura, cancellationToken);
            }
        }

        private async Task<CompraCompletaDto?> GetCompraCompletaFromFallbackAsync(int numeroFactura, CancellationToken cancellationToken)
        {
            try
            {
                using var conn = new NpgsqlConnection(ParsePostgresUrl(Environment.GetEnvironmentVariable("AIVEN_URL")));
                await conn.OpenAsync(cancellationToken);
                const string maestroSql = @"
                    SELECT numero_factura, id_usuario, total_compra, estado, created_at,
                           metodo_pago, lugar_entrega, requiere_factura, cedula_factura, nombre_factura 
                    FROM public.maestro_compras 
                    WHERE numero_factura = $1;";

                using var cmdMaestro = new NpgsqlCommand(maestroSql, conn);
                cmdMaestro.Parameters.AddWithValue(numeroFactura);

                int dbNumeroFactura = 0;
                string dbIdUsuario = "";
                decimal dbTotalCompra = 0;
                string dbEstado = "";
                DateTime dbCreatedAt = DateTime.MinValue;
                string dbMetodoPago = "";
                string dbLugarEntrega = "";
                bool dbRequiereFactura = true;
                string dbCedulaFactura = "";
                string dbNombreFactura = "";

                using (var reader = await cmdMaestro.ExecuteReaderAsync(cancellationToken))
                {
                    if (await reader.ReadAsync(cancellationToken))
                    {
                        dbNumeroFactura = reader.GetInt32(reader.GetOrdinal("numero_factura"));
                        dbIdUsuario = reader.GetString(reader.GetOrdinal("id_usuario"));
                        dbTotalCompra = reader.GetDecimal(reader.GetOrdinal("total_compra"));
                        dbEstado = reader.GetString(reader.GetOrdinal("estado"));
                        dbCreatedAt = reader.GetDateTime(reader.GetOrdinal("created_at"));
                        dbMetodoPago = reader.GetString(reader.GetOrdinal("metodo_pago"));
                        dbLugarEntrega = reader.GetString(reader.GetOrdinal("lugar_entrega"));
                        dbRequiereFactura = reader.GetBoolean(reader.GetOrdinal("requiere_factura"));
                        dbCedulaFactura = reader.IsDBNull(reader.GetOrdinal("cedula_factura")) ? "" : reader.GetString(reader.GetOrdinal("cedula_factura"));
                        dbNombreFactura = reader.IsDBNull(reader.GetOrdinal("nombre_factura")) ? "" : reader.GetString(reader.GetOrdinal("nombre_factura"));
                    }
                    else
                    {
                        return null;
                    }
                }

                const string detallesSql = @"
                    SELECT d.id_producto, p.nombre, p.url_imagen, d.cantidad, d.precio_unitario 
                    FROM public.detalle_compras d
                    LEFT JOIN public.productos p ON d.id_producto = p.id_producto
                    WHERE d.numero_factura = $1;";

                using var cmdDetalles = new NpgsqlCommand(detallesSql, conn);
                cmdDetalles.Parameters.AddWithValue(numeroFactura);

                var detallesDto = new List<DetalleCompraDto>();
                using (var reader = await cmdDetalles.ExecuteReaderAsync(cancellationToken))
                {
                    while (await reader.ReadAsync(cancellationToken))
                    {
                        var idProducto = reader.GetInt32(reader.GetOrdinal("id_producto"));
                        var nombre = reader.IsDBNull(reader.GetOrdinal("nombre")) ? "Producto no disponible" : reader.GetString(reader.GetOrdinal("nombre"));
                        var urlImagen = reader.IsDBNull(reader.GetOrdinal("url_imagen")) ? null : reader.GetString(reader.GetOrdinal("url_imagen"));
                        var cantidad = reader.GetInt32(reader.GetOrdinal("cantidad"));
                        var precioUnitario = reader.GetDecimal(reader.GetOrdinal("precio_unitario"));

                        detallesDto.Add(new DetalleCompraDto(
                            IdProducto: idProducto,
                            NombreProducto: nombre,
                            UrlImagen: urlImagen,
                            Cantidad: cantidad,
                            PrecioUnitario: precioUnitario,
                            Subtotal: precioUnitario * cantidad
                        ));
                    }
                }

                decimal tasaIva = 0.15m;
                decimal subtotalCalculado = dbTotalCompra / (1 + tasaIva);
                decimal ivaCalculado = dbTotalCompra - subtotalCalculado;

                return new CompraCompletaDto(
                    NumeroFactura: dbNumeroFactura,
                    IdUsuario: dbIdUsuario,
                    TotalCompra: dbTotalCompra,
                    Subtotal: subtotalCalculado,
                    Iva: ivaCalculado,
                    Estado: dbEstado,
                    MetodoPago: dbMetodoPago,
                    LugarEntrega: dbLugarEntrega,
                    RequiereFactura: dbRequiereFactura,
                    NombreFactura: dbNombreFactura,
                    CedulaFactura: dbCedulaFactura,
                    CreatedAt: dbCreatedAt,
                    Detalles: detallesDto
                );
            }
            catch
            {
                return await GetCompraCompletaFromMongoAsync(numeroFactura, cancellationToken);
            }
        }

        private async Task<CompraCompletaDto?> GetCompraCompletaFromMongoAsync(int numeroFactura, CancellationToken cancellationToken)
        {
            try
            {
                var collection = _dbExecutor.GetMongoDatabase().GetCollection<BsonDocument>("compras");
                var doc = await collection.Find(Builders<BsonDocument>.Filter.Eq("_id", numeroFactura)).FirstOrDefaultAsync(cancellationToken);
                if (doc != null)
                {
                    return CompraFromBson(doc);
                }
            }
            catch
            {
            }
            return null;
        }

        public async Task<List<CompraResumenDto>> GetAllAsync(CancellationToken cancellationToken = default)
        {
            var source = await _dbExecutor.GetActiveDatabaseAsync();
            if (source == ActiveDbSource.Supabase || source == ActiveDbSource.Aiven)
            {
                try
                {
                    using var conn = await _dbExecutor.GetPostgresConnectionAsync();
                    const string sql = "SELECT numero_factura, id_usuario, total_compra, estado, metodo_pago, created_at FROM public.maestro_compras ORDER BY created_at DESC;";
                    using var cmd = new NpgsqlCommand(sql, conn);
                    var list = new List<CompraResumenDto>();
                    using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
                    while (await reader.ReadAsync(cancellationToken))
                    {
                        list.Add(new CompraResumenDto(
                            reader.GetInt32(reader.GetOrdinal("numero_factura")),
                            reader.GetString(reader.GetOrdinal("id_usuario")),
                            reader.GetDecimal(reader.GetOrdinal("total_compra")),
                            reader.GetString(reader.GetOrdinal("estado")),
                            reader.GetString(reader.GetOrdinal("metodo_pago")),
                            reader.GetDateTime(reader.GetOrdinal("created_at"))
                        ));
                    }
                    return list;
                }
                catch when (source == ActiveDbSource.Supabase)
                {
                    return await GetAllFromFallbackAsync(cancellationToken);
                }
            }
            else
            {
                return await GetAllFromMongoAsync(cancellationToken);
            }
        }

        private async Task<List<CompraResumenDto>> GetAllFromFallbackAsync(CancellationToken cancellationToken)
        {
            try
            {
                using var conn = new NpgsqlConnection(ParsePostgresUrl(Environment.GetEnvironmentVariable("AIVEN_URL")));
                await conn.OpenAsync(cancellationToken);
                const string sql = "SELECT numero_factura, id_usuario, total_compra, estado, metodo_pago, created_at FROM public.maestro_compras ORDER BY created_at DESC;";
                using var cmd = new NpgsqlCommand(sql, conn);
                var list = new List<CompraResumenDto>();
                using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    list.Add(new CompraResumenDto(
                        reader.GetInt32(reader.GetOrdinal("numero_factura")),
                        reader.GetString(reader.GetOrdinal("id_usuario")),
                        reader.GetDecimal(reader.GetOrdinal("total_compra")),
                        reader.GetString(reader.GetOrdinal("estado")),
                        reader.GetString(reader.GetOrdinal("metodo_pago")),
                        reader.GetDateTime(reader.GetOrdinal("created_at"))
                    ));
                }
                return list;
            }
            catch
            {
                return await GetAllFromMongoAsync(cancellationToken);
            }
        }

        private async Task<List<CompraResumenDto>> GetAllFromMongoAsync(CancellationToken cancellationToken)
        {
            var list = new List<CompraResumenDto>();
            try
            {
                var collection = _dbExecutor.GetMongoDatabase().GetCollection<BsonDocument>("compras");
                var docs = await collection.Find(new BsonDocument()).Sort(Builders<BsonDocument>.Sort.Descending("created_at")).ToListAsync(cancellationToken);
                foreach (var doc in docs)
                {
                    list.Add(ResumenFromBson(doc));
                }
            }
            catch
            {
            }
            return list;
        }

        public async Task<List<CompraResumenDto>> GetByUsuarioAsync(string idUsuario, CancellationToken cancellationToken = default)
        {
            var source = await _dbExecutor.GetActiveDatabaseAsync();
            if (source == ActiveDbSource.Supabase || source == ActiveDbSource.Aiven)
            {
                try
                {
                    using var conn = await _dbExecutor.GetPostgresConnectionAsync();
                    const string sql = "SELECT numero_factura, id_usuario, total_compra, estado, metodo_pago, created_at FROM public.maestro_compras WHERE id_usuario = $1 ORDER BY created_at DESC;";
                    using var cmd = new NpgsqlCommand(sql, conn);
                    cmd.Parameters.AddWithValue(idUsuario);
                    var list = new List<CompraResumenDto>();
                    using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
                    while (await reader.ReadAsync(cancellationToken))
                    {
                        list.Add(new CompraResumenDto(
                            reader.GetInt32(reader.GetOrdinal("numero_factura")),
                            reader.GetString(reader.GetOrdinal("id_usuario")),
                            reader.GetDecimal(reader.GetOrdinal("total_compra")),
                            reader.GetString(reader.GetOrdinal("estado")),
                            reader.GetString(reader.GetOrdinal("metodo_pago")),
                            reader.GetDateTime(reader.GetOrdinal("created_at"))
                        ));
                    }
                    return list;
                }
                catch when (source == ActiveDbSource.Supabase)
                {
                    return await GetByUsuarioFromFallbackAsync(idUsuario, cancellationToken);
                }
            }
            else
            {
                return await GetByUsuarioFromMongoAsync(idUsuario, cancellationToken);
            }
        }

        private async Task<List<CompraResumenDto>> GetByUsuarioFromFallbackAsync(string idUsuario, CancellationToken cancellationToken)
        {
            try
            {
                using var conn = new NpgsqlConnection(ParsePostgresUrl(Environment.GetEnvironmentVariable("AIVEN_URL")));
                await conn.OpenAsync(cancellationToken);
                const string sql = "SELECT numero_factura, id_usuario, total_compra, estado, metodo_pago, created_at FROM public.maestro_compras WHERE id_usuario = $1 ORDER BY created_at DESC;";
                using var cmd = new NpgsqlCommand(sql, conn);
                cmd.Parameters.AddWithValue(idUsuario);
                var list = new List<CompraResumenDto>();
                using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    list.Add(new CompraResumenDto(
                        reader.GetInt32(reader.GetOrdinal("numero_factura")),
                        reader.GetString(reader.GetOrdinal("id_usuario")),
                        reader.GetDecimal(reader.GetOrdinal("total_compra")),
                        reader.GetString(reader.GetOrdinal("estado")),
                        reader.GetString(reader.GetOrdinal("metodo_pago")),
                        reader.GetDateTime(reader.GetOrdinal("created_at"))
                    ));
                }
                return list;
            }
            catch
            {
                return await GetByUsuarioFromMongoAsync(idUsuario, cancellationToken);
            }
        }

        private async Task<List<CompraResumenDto>> GetByUsuarioFromMongoAsync(string idUsuario, CancellationToken cancellationToken)
        {
            var list = new List<CompraResumenDto>();
            try
            {
                var collection = _dbExecutor.GetMongoDatabase().GetCollection<BsonDocument>("compras");
                var docs = await collection.Find(Builders<BsonDocument>.Filter.Eq("id_usuario", idUsuario)).Sort(Builders<BsonDocument>.Sort.Descending("created_at")).ToListAsync(cancellationToken);
                foreach (var doc in docs)
                {
                    list.Add(ResumenFromBson(doc));
                }
            }
            catch
            {
            }
            return list;
        }

        private static string ParsePostgresUrl(string? url)
        {
            if (string.IsNullOrWhiteSpace(url)) return "";
            if (!url.StartsWith("postgres://") && !url.StartsWith("postgresql://")) return url;
            try
            {
                var uri = new Uri(url);
                var userInfo = uri.UserInfo.Split(':');
                var username = userInfo[0];
                var password = userInfo.Length > 1 ? userInfo[1] : "";
                var host = uri.Host;
                var port = uri.Port;
                var database = uri.AbsolutePath.TrimStart('/');
                return $"Host={host};Port={port};Database={database};Username={username};Password={password};SslMode=Require;TrustServerCertificate=true;";
            }
            catch
            {
                return url;
            }
        }
    }
}