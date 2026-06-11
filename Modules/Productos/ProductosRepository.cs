using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
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

        public async Task<IReadOnlyList<ProductoDto>> GetAllAsync(CancellationToken cancellationToken = default)
        {
            var source = await _dbExecutor.GetActiveDatabaseAsync();
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

        private async Task<IReadOnlyList<ProductoDto>> GetAllFromFallbackAsync(CancellationToken cancellationToken)
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

        public async Task<ProductoDto?> GetByIdAsync(int idProducto, CancellationToken cancellationToken = default)
        {
            var source = await _dbExecutor.GetActiveDatabaseAsync();
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

        private async Task<ProductoDto?> GetByIdFromFallbackAsync(int idProducto, CancellationToken cancellationToken)
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
                    return MapReaderToDto(reader);
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
                    return MapReaderToDto(reader);
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
                return rowsAffected > 0;
            }
            return false;
        }

        public async Task<IReadOnlyList<ProductoDto>> GetByIdsAsync(IEnumerable<int> ids, CancellationToken cancellationToken = default)
        {
            var idsList = ids.ToList();
            if (!idsList.Any()) return new List<ProductoDto>();

            var source = await _dbExecutor.GetActiveDatabaseAsync();
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

        private async Task<IReadOnlyList<ProductoDto>> GetByIdsFromFallbackAsync(List<int> idsList, CancellationToken cancellationToken)
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
                return rowsAffected > 0;
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
                return rowsAffected > 0;
            }
            return false;
        }

        public async Task<IReadOnlyList<ProductoDto>> GetInactivosAsync(CancellationToken cancellationToken = default)
        {
            var source = await _dbExecutor.GetActiveDatabaseAsync();
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

        private async Task<IReadOnlyList<ProductoDto>> GetInactivosFromFallbackAsync(CancellationToken cancellationToken)
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
