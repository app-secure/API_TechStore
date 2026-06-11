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

namespace TechStore360.Modules.Productos
{
    public interface IProductosRepository
    {
        Task<IReadOnlyList<ProductoDto>> GetAllAsync(CancellationToken cancellationToken = default);
        Task<IReadOnlyList<ProductoDto>> GetInactivosAsync(CancellationToken cancellationToken = default);
        Task<ProductoDto?> GetByIdAsync(int idProducto, CancellationToken cancellationToken = default);
        Task<ProductoDto> AddAsync(CrearProductoRequest request, CancellationToken cancellationToken = default);
        Task<ProductoDto?> UpdateAsync(int idProducto, ActualizarProductoRequest request, CancellationToken cancellationToken = default);
        Task<bool> DeleteAsync(int idProducto, CancellationToken cancellationToken = default);
        Task<IReadOnlyList<ProductoDto>> GetByIdsAsync(IEnumerable<int> ids, CancellationToken cancellationToken = default);
        Task<bool> UpdateStockAsync(int idProducto, int cantidadDelta, CancellationToken cancellationToken = default);
        Task<bool> ReactivarAsync(int idProducto, CancellationToken cancellationToken = default);
    }

    public sealed class ProductosRepository : IProductosRepository
    {
        private readonly ResilientDbExecutor _dbExecutor;

        public ProductosRepository(ResilientDbExecutor dbExecutor)
        {
            _dbExecutor = dbExecutor;
        }

        private static ProductoDto MapReaderToDto(NpgsqlDataReader reader)
        {
            return new ProductoDto(
                IdProducto: reader.GetInt32(reader.GetOrdinal("id_producto")),
                Nombre: reader.GetString(reader.GetOrdinal("nombre")),
                Precio: reader.GetDecimal(reader.GetOrdinal("precio")),
                Stock: reader.GetInt32(reader.GetOrdinal("stock")),
                UrlImagen: reader.IsDBNull(reader.GetOrdinal("url_imagen")) ? null : reader.GetString(reader.GetOrdinal("url_imagen")),
                Estado: reader.GetBoolean(reader.GetOrdinal("estado"))
            );
        }

        private static ProductoDto FromBson(BsonDocument doc)
        {
            return new ProductoDto(
                IdProducto: Convert.ToInt32(BsonTypeMapper.MapToDotNetValue(doc["_id"])),
                Nombre: doc["nombre"].AsString,
                Precio: Convert.ToDecimal(BsonTypeMapper.MapToDotNetValue(doc["precio"])),
                Stock: Convert.ToInt32(BsonTypeMapper.MapToDotNetValue(doc["stock"])),
                UrlImagen: doc.Contains("url_imagen") && !doc["url_imagen"].IsBsonNull ? doc["url_imagen"].AsString : null,
                Estado: doc["estado"].AsBoolean
            );
        }

        private static BsonDocument ToBson(ProductoDto p)
        {
            return new BsonDocument
            {
                { "_id", p.IdProducto },
                { "nombre", p.Nombre },
                { "precio", (double)p.Precio },
                { "stock", p.Stock },
                { "url_imagen", p.UrlImagen != null ? (BsonValue)p.UrlImagen : BsonNull.Value },
                { "estado", p.Estado }
            };
        }

        private async Task SyncToMongoBackgroundAsync(ProductoDto p)
        {
            try
            {
                var collection = _dbExecutor.GetMongoDatabase().GetCollection<BsonDocument>("productos");
                var doc = ToBson(p);
                await collection.ReplaceOneAsync(
                    Builders<BsonDocument>.Filter.Eq("_id", p.IdProducto),
                    doc,
                    new ReplaceOptions { IsUpsert = true }
                );
            }
            catch {}
        }

        public async Task<IReadOnlyList<ProductoDto>> GetAllAsync(CancellationToken cancellationToken = default)
        {
            var source = await _dbExecutor.GetActiveDatabaseAsync();
            if (source == ActiveDbSource.Supabase || source == ActiveDbSource.Aiven)
            {
                try
                {
                    using var conn = await _dbExecutor.GetPostgresConnectionAsync();
                    const string sql = "SELECT id_producto, nombre, precio, stock, url_imagen, estado FROM public.productos WHERE estado = true ORDER BY nombre ASC;";
                    using var cmd = new NpgsqlCommand(sql, conn);
                    var list = new List<ProductoDto>();
                    using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
                    while (await reader.ReadAsync(cancellationToken))
                    {
                        list.Add(MapReaderToDto(reader));
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

        private async Task<IReadOnlyList<ProductoDto>> GetAllFromFallbackAsync(CancellationToken cancellationToken)
        {
            try
            {
                using var conn = new NpgsqlConnection(ParsePostgresUrl(Environment.GetEnvironmentVariable("AIVEN_URL")));
                await conn.OpenAsync(cancellationToken);
                const string sql = "SELECT id_producto, nombre, precio, stock, url_imagen, estado FROM public.productos WHERE estado = true ORDER BY nombre ASC;";
                using var cmd = new NpgsqlCommand(sql, conn);
                var list = new List<ProductoDto>();
                using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    list.Add(MapReaderToDto(reader));
                }
                return list;
            }
            catch
            {
                return await GetAllFromMongoAsync(cancellationToken);
            }
        }

        private async Task<IReadOnlyList<ProductoDto>> GetAllFromMongoAsync(CancellationToken cancellationToken)
        {
            var list = new List<ProductoDto>();
            try
            {
                var collection = _dbExecutor.GetMongoDatabase().GetCollection<BsonDocument>("productos");
                var docs = await collection.Find(Builders<BsonDocument>.Filter.Eq("estado", true)).Sort(Builders<BsonDocument>.Sort.Ascending("nombre")).ToListAsync(cancellationToken);
                foreach (var doc in docs)
                {
                    list.Add(FromBson(doc));
                }
            }
            catch {}
            return list;
        }

        public async Task<ProductoDto?> GetByIdAsync(int idProducto, CancellationToken cancellationToken = default)
        {
            var source = await _dbExecutor.GetActiveDatabaseAsync();
            if (source == ActiveDbSource.Supabase || source == ActiveDbSource.Aiven)
            {
                try
                {
                    using var conn = await _dbExecutor.GetPostgresConnectionAsync();
                    const string sql = "SELECT id_producto, nombre, precio, stock, url_imagen, estado FROM public.productos WHERE id_producto = $1;";
                    using var cmd = new NpgsqlCommand(sql, conn);
                    cmd.Parameters.AddWithValue(idProducto);
                    using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
                    if (await reader.ReadAsync(cancellationToken))
                    {
                        return MapReaderToDto(reader);
                    }
                    return null;
                }
                catch when (source == ActiveDbSource.Supabase)
                {
                    return await GetByIdFromFallbackAsync(idProducto, cancellationToken);
                }
            }
            else
            {
                return await GetByIdFromMongoAsync(idProducto, cancellationToken);
            }
        }

        private async Task<ProductoDto?> GetByIdFromFallbackAsync(int idProducto, CancellationToken cancellationToken)
        {
            try
            {
                using var conn = new NpgsqlConnection(ParsePostgresUrl(Environment.GetEnvironmentVariable("AIVEN_URL")));
                await conn.OpenAsync(cancellationToken);
                const string sql = "SELECT id_producto, nombre, precio, stock, url_imagen, estado FROM public.productos WHERE id_producto = $1;";
                using var cmd = new NpgsqlCommand(sql, conn);
                cmd.Parameters.AddWithValue(idProducto);
                using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
                if (await reader.ReadAsync(cancellationToken))
                {
                    return MapReaderToDto(reader);
                }
                return null;
            }
            catch
            {
                return await GetByIdFromMongoAsync(idProducto, cancellationToken);
            }
        }

        private async Task<ProductoDto?> GetByIdFromMongoAsync(int idProducto, CancellationToken cancellationToken)
        {
            try
            {
                var collection = _dbExecutor.GetMongoDatabase().GetCollection<BsonDocument>("productos");
                var doc = await collection.Find(Builders<BsonDocument>.Filter.Eq("_id", idProducto)).FirstOrDefaultAsync(cancellationToken);
                if (doc != null)
                {
                    return FromBson(doc);
                }
            }
            catch {}
            return null;
        }

        public async Task<ProductoDto> AddAsync(CrearProductoRequest request, CancellationToken cancellationToken = default)
        {
            var source = await _dbExecutor.GetActiveDatabaseAsync();
            _dbExecutor.CheckWriteAccess(source);

            if (source == ActiveDbSource.Supabase)
            {
                using var conn = await _dbExecutor.GetPostgresConnectionAsync();
                const string sql = @"
                    INSERT INTO public.productos (nombre, precio, stock, url_imagen, estado)
                    VALUES ($1, $2, $3, $4, true)
                    RETURNING id_producto, nombre, precio, stock, url_imagen, estado;";
                using var cmd = new NpgsqlCommand(sql, conn);
                cmd.Parameters.AddWithValue(request.Nombre);
                cmd.Parameters.AddWithValue(request.Precio);
                cmd.Parameters.AddWithValue(request.Stock);
                cmd.Parameters.AddWithValue((object?)request.UrlImagen ?? DBNull.Value);

                using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
                if (await reader.ReadAsync(cancellationToken))
                {
                    var inserted = MapReaderToDto(reader);
                    _ = Task.Run(() => SyncToMongoBackgroundAsync(inserted));
                    return inserted;
                }
                throw new InvalidOperationException("No se pudo insertar el producto en Supabase.");
            }
            throw new InvalidOperationException("Escritura no disponible.");
        }

        public async Task<ProductoDto?> UpdateAsync(int idProducto, ActualizarProductoRequest request, CancellationToken cancellationToken = default)
        {
            var source = await _dbExecutor.GetActiveDatabaseAsync();
            _dbExecutor.CheckWriteAccess(source);

            if (source == ActiveDbSource.Supabase)
            {
                var existing = await GetByIdAsync(idProducto, cancellationToken);
                if (existing == null) return null;

                var nombre = request.Nombre ?? existing.Nombre;
                var precio = request.Precio ?? existing.Precio;
                var stock = request.Stock ?? existing.Stock;
                var urlImagen = request.UrlImagen ?? existing.UrlImagen;

                using var conn = await _dbExecutor.GetPostgresConnectionAsync();
                const string sql = @"
                    UPDATE public.productos 
                    SET nombre = $1, precio = $2, stock = $3, url_imagen = $4
                    WHERE id_producto = $5
                    RETURNING id_producto, nombre, precio, stock, url_imagen, estado;";
                using var cmd = new NpgsqlCommand(sql, conn);
                cmd.Parameters.AddWithValue(nombre);
                cmd.Parameters.AddWithValue(precio);
                cmd.Parameters.AddWithValue(stock);
                cmd.Parameters.AddWithValue((object?)urlImagen ?? DBNull.Value);
                cmd.Parameters.AddWithValue(idProducto);

                using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
                if (await reader.ReadAsync(cancellationToken))
                {
                    var updated = MapReaderToDto(reader);
                    _ = Task.Run(() => SyncToMongoBackgroundAsync(updated));
                    return updated;
                }
            }
            return null;
        }

        public async Task<bool> DeleteAsync(int idProducto, CancellationToken cancellationToken = default)
        {
            var source = await _dbExecutor.GetActiveDatabaseAsync();
            _dbExecutor.CheckWriteAccess(source);

            if (source == ActiveDbSource.Supabase)
            {
                using var conn = await _dbExecutor.GetPostgresConnectionAsync();
                const string sql = "UPDATE public.productos SET estado = false WHERE id_producto = $1 AND estado = true;";
                using var cmd = new NpgsqlCommand(sql, conn);
                cmd.Parameters.AddWithValue(idProducto);
                var rowsAffected = await cmd.ExecuteNonQueryAsync(cancellationToken);
                if (rowsAffected > 0)
                {
                    _ = Task.Run(async () => {
                        try
                        {
                            var collection = _dbExecutor.GetMongoDatabase().GetCollection<BsonDocument>("productos");
                            await collection.UpdateOneAsync(
                                Builders<BsonDocument>.Filter.Eq("_id", idProducto),
                                Builders<BsonDocument>.Update.Set("estado", false)
                            );
                        }
                        catch {}
                    });
                    return true;
                }
            }
            return false;
        }

        public async Task<IReadOnlyList<ProductoDto>> GetByIdsAsync(IEnumerable<int> ids, CancellationToken cancellationToken = default)
        {
            var idsList = ids.ToList();
            if (!idsList.Any()) return new List<ProductoDto>();

            var source = await _dbExecutor.GetActiveDatabaseAsync();
            if (source == ActiveDbSource.Supabase || source == ActiveDbSource.Aiven)
            {
                try
                {
                    using var conn = await _dbExecutor.GetPostgresConnectionAsync();
                    var placeholders = string.Join(",", idsList.Select((_, index) => $"${index + 1}"));
                    var sql = $"SELECT id_producto, nombre, precio, stock, url_imagen, estado FROM public.productos WHERE id_producto IN ({placeholders});";
                    using var cmd = new NpgsqlCommand(sql, conn);
                    foreach (var id in idsList)
                    {
                        cmd.Parameters.AddWithValue(id);
                    }
                    var list = new List<ProductoDto>();
                    using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
                    while (await reader.ReadAsync(cancellationToken))
                    {
                        list.Add(MapReaderToDto(reader));
                    }
                    return list;
                }
                catch when (source == ActiveDbSource.Supabase)
                {
                    return await GetByIdsFromFallbackAsync(idsList, cancellationToken);
                }
            }
            else
            {
                return await GetByIdsFromMongoAsync(idsList, cancellationToken);
            }
        }

        private async Task<IReadOnlyList<ProductoDto>> GetByIdsFromFallbackAsync(List<int> idsList, CancellationToken cancellationToken)
        {
            try
            {
                using var conn = new NpgsqlConnection(ParsePostgresUrl(Environment.GetEnvironmentVariable("AIVEN_URL")));
                await conn.OpenAsync(cancellationToken);
                var placeholders = string.Join(",", idsList.Select((_, index) => $"${index + 1}"));
                var sql = $"SELECT id_producto, nombre, precio, stock, url_imagen, estado FROM public.productos WHERE id_producto IN ({placeholders});";
                using var cmd = new NpgsqlCommand(sql, conn);
                foreach (var id in idsList)
                {
                    cmd.Parameters.AddWithValue(id);
                }
                var list = new List<ProductoDto>();
                using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    list.Add(MapReaderToDto(reader));
                }
                return list;
            }
            catch
            {
                return await GetByIdsFromMongoAsync(idsList, cancellationToken);
            }
        }

        private async Task<IReadOnlyList<ProductoDto>> GetByIdsFromMongoAsync(List<int> idsList, CancellationToken cancellationToken)
        {
            var list = new List<ProductoDto>();
            try
            {
                var collection = _dbExecutor.GetMongoDatabase().GetCollection<BsonDocument>("productos");
                var docs = await collection.Find(Builders<BsonDocument>.Filter.In("_id", idsList)).ToListAsync(cancellationToken);
                foreach (var doc in docs)
                {
                    list.Add(FromBson(doc));
                }
            }
            catch {}
            return list;
        }

        public async Task<bool> UpdateStockAsync(int idProducto, int cantidadDelta, CancellationToken cancellationToken = default)
        {
            var source = await _dbExecutor.GetActiveDatabaseAsync();
            _dbExecutor.CheckWriteAccess(source);

            if (source == ActiveDbSource.Supabase)
            {
                using var conn = await _dbExecutor.GetPostgresConnectionAsync();
                const string sql = "UPDATE public.productos SET stock = stock + $1 WHERE id_producto = $2 AND estado = true;";
                using var cmd = new NpgsqlCommand(sql, conn);
                cmd.Parameters.AddWithValue(cantidadDelta);
                cmd.Parameters.AddWithValue(idProducto);
                var rowsAffected = await cmd.ExecuteNonQueryAsync(cancellationToken);
                if (rowsAffected > 0)
                {
                    _ = Task.Run(async () => {
                        try
                        {
                            var collection = _dbExecutor.GetMongoDatabase().GetCollection<BsonDocument>("productos");
                            await collection.UpdateOneAsync(
                                Builders<BsonDocument>.Filter.Eq("_id", idProducto),
                                Builders<BsonDocument>.Update.Inc("stock", cantidadDelta)
                            );
                        }
                        catch {}
                    });
                    return true;
                }
            }
            return false;
        }

        public async Task<bool> ReactivarAsync(int idProducto, CancellationToken cancellationToken = default)
        {
            var source = await _dbExecutor.GetActiveDatabaseAsync();
            _dbExecutor.CheckWriteAccess(source);

            if (source == ActiveDbSource.Supabase)
            {
                using var conn = await _dbExecutor.GetPostgresConnectionAsync();
                const string sql = "UPDATE public.productos SET estado = true WHERE id_producto = $1 AND estado = false;";
                using var cmd = new NpgsqlCommand(sql, conn);
                cmd.Parameters.AddWithValue(idProducto);
                var rowsAffected = await cmd.ExecuteNonQueryAsync(cancellationToken);
                if (rowsAffected > 0)
                {
                    _ = Task.Run(async () => {
                        try
                        {
                            var collection = _dbExecutor.GetMongoDatabase().GetCollection<BsonDocument>("productos");
                            await collection.UpdateOneAsync(
                                Builders<BsonDocument>.Filter.Eq("_id", idProducto),
                                Builders<BsonDocument>.Update.Set("estado", true)
                            );
                        }
                        catch {}
                    });
                    return true;
                }
            }
            return false;
        }

        public async Task<IReadOnlyList<ProductoDto>> GetInactivosAsync(CancellationToken cancellationToken = default)
        {
            var source = await _dbExecutor.GetActiveDatabaseAsync();
            if (source == ActiveDbSource.Supabase || source == ActiveDbSource.Aiven)
            {
                try
                {
                    using var conn = await _dbExecutor.GetPostgresConnectionAsync();
                    const string sql = "SELECT id_producto, nombre, precio, stock, url_imagen, estado FROM public.productos WHERE estado = false ORDER BY nombre ASC;";
                    using var cmd = new NpgsqlCommand(sql, conn);
                    var list = new List<ProductoDto>();
                    using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
                    while (await reader.ReadAsync(cancellationToken))
                    {
                        list.Add(MapReaderToDto(reader));
                    }
                    return list;
                }
                catch when (source == ActiveDbSource.Supabase)
                {
                    return await GetInactivosFromFallbackAsync(cancellationToken);
                }
            }
            else
            {
                return await GetInactivosFromMongoAsync(cancellationToken);
            }
        }

        private async Task<IReadOnlyList<ProductoDto>> GetInactivosFromFallbackAsync(CancellationToken cancellationToken)
        {
            try
            {
                using var conn = new NpgsqlConnection(ParsePostgresUrl(Environment.GetEnvironmentVariable("AIVEN_URL")));
                await conn.OpenAsync(cancellationToken);
                const string sql = "SELECT id_producto, nombre, precio, stock, url_imagen, estado FROM public.productos WHERE estado = false ORDER BY nombre ASC;";
                using var cmd = new NpgsqlCommand(sql, conn);
                var list = new List<ProductoDto>();
                using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    list.Add(MapReaderToDto(reader));
                }
                return list;
            }
            catch
            {
                return await GetInactivosFromMongoAsync(cancellationToken);
            }
        }

        private async Task<IReadOnlyList<ProductoDto>> GetInactivosFromMongoAsync(CancellationToken cancellationToken)
        {
            var list = new List<ProductoDto>();
            try
            {
                var collection = _dbExecutor.GetMongoDatabase().GetCollection<BsonDocument>("productos");
                var docs = await collection.Find(Builders<BsonDocument>.Filter.Eq("estado", false)).Sort(Builders<BsonDocument>.Sort.Ascending("nombre")).ToListAsync(cancellationToken);
                foreach (var doc in docs)
                {
                    list.Add(FromBson(doc));
                }
            }
            catch {}
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
