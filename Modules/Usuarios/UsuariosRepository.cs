using System;
using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using MongoDB.Driver;
using MongoDB.Bson;
using TechStore360.Data;

namespace TechStore360.Modules.Usuarios
{
    public interface IUsuariosRepository
    {
        Task<UsuarioDto?> GetByIdAsync(string idUsuario, CancellationToken ct = default);
        Task<UsuarioDto?> GetByCedulaAsync(string cedula, CancellationToken ct = default);
        Task<List<UsuarioDto>> GetAllAsync(CancellationToken ct = default);
        Task<List<UsuarioDto>> GetInactivosAsync(CancellationToken ct = default);
        Task<UsuarioDto> AddAsync(UsuarioDto usuario, CancellationToken ct = default);
        Task<UsuarioDto?> UpdateAsync(string idUsuario, ActualizarUsuarioRequest request, CancellationToken ct = default);
        Task<UsuarioDto?> UpdatePerfilAsync(string idUsuario, EditarPerfilRequest request, CancellationToken ct = default);
        Task<bool> DeleteAsync(string idUsuario, CancellationToken ct = default);
        Task<bool> ReactivarAsync(string idUsuario, CancellationToken ct = default);
    }

    public class UsuariosRepository : IUsuariosRepository
    {
        private readonly ResilientDbExecutor _dbExecutor;

        public UsuariosRepository(ResilientDbExecutor dbExecutor)
        {
            _dbExecutor = dbExecutor;
        }

        private static UsuarioDto MapReaderToDto(NpgsqlDataReader reader)
        {
            return new UsuarioDto(
                IdUsuario: reader.GetString(reader.GetOrdinal("id_usuario")),
                NombreCompleto: reader.GetString(reader.GetOrdinal("nombre_completo")),
                Email: reader.GetString(reader.GetOrdinal("email")),
                Cedula: reader.GetString(reader.GetOrdinal("cedula")),
                Telefono: reader.GetString(reader.GetOrdinal("telefono")),
                Direccion: reader.GetString(reader.GetOrdinal("direccion")),
                Rol: reader.GetString(reader.GetOrdinal("rol")),
                Estado: reader.GetBoolean(reader.GetOrdinal("estado"))
            );
        }

        private static BsonDocument ToBson(UsuarioDto u)
        {
            return new BsonDocument
            {
                { "_id", u.IdUsuario },
                { "nombre_completo", u.NombreCompleto },
                { "email", u.Email },
                { "cedula", u.Cedula },
                { "telefono", u.Telefono },
                { "direccion", u.Direccion },
                { "rol", u.Rol },
                { "estado", u.Estado }
            };
        }

        private static UsuarioDto FromBson(BsonDocument doc)
        {
            return new UsuarioDto(
                IdUsuario: doc["_id"].AsString,
                NombreCompleto: doc["nombre_completo"].AsString,
                Email: doc["email"].AsString,
                Cedula: doc["cedula"].AsString,
                Telefono: doc["telefono"].AsString,
                Direccion: doc["direccion"].AsString,
                Rol: doc["rol"].AsString,
                Estado: doc["estado"].AsBoolean
            );
        }

        private async Task SyncToMongoBackgroundAsync(UsuarioDto usuario)
        {
            try
            {
                var collection = _dbExecutor.GetMongoDatabase().GetCollection<BsonDocument>("usuarios");
                var doc = ToBson(usuario);
                await collection.ReplaceOneAsync(
                    Builders<BsonDocument>.Filter.Eq("_id", usuario.IdUsuario),
                    doc,
                    new ReplaceOptions { IsUpsert = true }
                );
            }
            catch
            {
            }
        }

        private async Task SyncDeleteToMongoBackgroundAsync(string idUsuario)
        {
            try
            {
                var collection = _dbExecutor.GetMongoDatabase().GetCollection<BsonDocument>("usuarios");
                await collection.DeleteOneAsync(Builders<BsonDocument>.Filter.Eq("_id", idUsuario));
            }
            catch
            {
            }
        }

        public async Task<UsuarioDto?> GetByIdAsync(string idUsuario, CancellationToken ct = default)
        {
            var source = await _dbExecutor.GetActiveDatabaseAsync();
            if (source == ActiveDbSource.Supabase || source == ActiveDbSource.Aiven)
            {
                try
                {
                    using var conn = await _dbExecutor.GetPostgresConnectionAsync();
                    const string sql = "SELECT id_usuario, nombre_completo, email, cedula, telefono, direccion, rol, estado FROM public.usuarios WHERE id_usuario = $1;";
                    using var cmd = new NpgsqlCommand(sql, conn);
                    cmd.Parameters.AddWithValue(idUsuario);
                    using var reader = await cmd.ExecuteReaderAsync(ct);
                    if (await reader.ReadAsync(ct))
                    {
                        return MapReaderToDto(reader);
                    }
                }
                catch when (source == ActiveDbSource.Supabase)
                {
                    return await GetByIdFromFallbackAsync(idUsuario, ct);
                }
            }
            else
            {
                return await GetByIdFromMongoAsync(idUsuario, ct);
            }
            return null;
        }

        private async Task<UsuarioDto?> GetByIdFromFallbackAsync(string idUsuario, CancellationToken ct)
        {
            try
            {
                using var conn = new NpgsqlConnection(ParsePostgresUrl(Environment.GetEnvironmentVariable("AIVEN_URL")));
                await conn.OpenAsync(ct);
                const string sql = "SELECT id_usuario, nombre_completo, email, cedula, telefono, direccion, rol, estado FROM public.usuarios WHERE id_usuario = $1;";
                using var cmd = new NpgsqlCommand(sql, conn);
                cmd.Parameters.AddWithValue(idUsuario);
                using var reader = await cmd.ExecuteReaderAsync(ct);
                if (await reader.ReadAsync(ct))
                {
                    return MapReaderToDto(reader);
                }
            }
            catch
            {
                return await GetByIdFromMongoAsync(idUsuario, ct);
            }
            return null;
        }

        private async Task<UsuarioDto?> GetByIdFromMongoAsync(string idUsuario, CancellationToken ct)
        {
            try
            {
                var collection = _dbExecutor.GetMongoDatabase().GetCollection<BsonDocument>("usuarios");
                var doc = await collection.Find(Builders<BsonDocument>.Filter.Eq("_id", idUsuario)).FirstOrDefaultAsync(ct);
                if (doc != null)
                {
                    return FromBson(doc);
                }
            }
            catch
            {
            }
            return null;
        }

        public async Task<UsuarioDto?> GetByCedulaAsync(string cedula, CancellationToken ct = default)
        {
            var source = await _dbExecutor.GetActiveDatabaseAsync();
            if (source == ActiveDbSource.Supabase || source == ActiveDbSource.Aiven)
            {
                try
                {
                    using var conn = await _dbExecutor.GetPostgresConnectionAsync();
                    const string sql = "SELECT id_usuario, nombre_completo, email, cedula, telefono, direccion, rol, estado FROM public.usuarios WHERE cedula = $1 AND estado = true;";
                    using var cmd = new NpgsqlCommand(sql, conn);
                    cmd.Parameters.AddWithValue(cedula);
                    using var reader = await cmd.ExecuteReaderAsync(ct);
                    if (await reader.ReadAsync(ct))
                    {
                        return MapReaderToDto(reader);
                    }
                }
                catch when (source == ActiveDbSource.Supabase)
                {
                    return await GetByCedulaFromFallbackAsync(cedula, ct);
                }
            }
            else
            {
                return await GetByCedulaFromMongoAsync(cedula, ct);
            }
            return null;
        }

        private async Task<UsuarioDto?> GetByCedulaFromFallbackAsync(string cedula, CancellationToken ct)
        {
            try
            {
                using var conn = new NpgsqlConnection(ParsePostgresUrl(Environment.GetEnvironmentVariable("AIVEN_URL")));
                await conn.OpenAsync(ct);
                const string sql = "SELECT id_usuario, nombre_completo, email, cedula, telefono, direccion, rol, estado FROM public.usuarios WHERE cedula = $1 AND estado = true;";
                using var cmd = new NpgsqlCommand(sql, conn);
                cmd.Parameters.AddWithValue(cedula);
                using var reader = await cmd.ExecuteReaderAsync(ct);
                if (await reader.ReadAsync(ct))
                {
                    return MapReaderToDto(reader);
                }
            }
            catch
            {
                return await GetByCedulaFromMongoAsync(cedula, ct);
            }
            return null;
        }

        private async Task<UsuarioDto?> GetByCedulaFromMongoAsync(string cedula, CancellationToken ct)
        {
            try
            {
                var collection = _dbExecutor.GetMongoDatabase().GetCollection<BsonDocument>("usuarios");
                var doc = await collection.Find(Builders<BsonDocument>.Filter.Eq("cedula", cedula)).FirstOrDefaultAsync(ct);
                if (doc != null && doc["estado"].AsBoolean)
                {
                    return FromBson(doc);
                }
            }
            catch
            {
            }
            return null;
        }

        public async Task<List<UsuarioDto>> GetAllAsync(CancellationToken ct = default)
        {
            var list = new List<UsuarioDto>();
            var source = await _dbExecutor.GetActiveDatabaseAsync();
            if (source == ActiveDbSource.Supabase || source == ActiveDbSource.Aiven)
            {
                try
                {
                    using var conn = await _dbExecutor.GetPostgresConnectionAsync();
                    const string sql = "SELECT id_usuario, nombre_completo, email, cedula, telefono, direccion, rol, estado FROM public.usuarios WHERE estado = true ORDER BY nombre_completo ASC;";
                    using var cmd = new NpgsqlCommand(sql, conn);
                    using var reader = await cmd.ExecuteReaderAsync(ct);
                    while (await reader.ReadAsync(ct))
                    {
                        list.Add(MapReaderToDto(reader));
                    }
                    return list;
                }
                catch when (source == ActiveDbSource.Supabase)
                {
                    return await GetAllFromFallbackAsync(ct);
                }
            }
            else
            {
                return await GetAllFromMongoAsync(ct);
            }
        }

        private async Task<List<UsuarioDto>> GetAllFromFallbackAsync(CancellationToken ct)
        {
            var list = new List<UsuarioDto>();
            try
            {
                using var conn = new NpgsqlConnection(ParsePostgresUrl(Environment.GetEnvironmentVariable("AIVEN_URL")));
                await conn.OpenAsync(ct);
                const string sql = "SELECT id_usuario, nombre_completo, email, cedula, telefono, direccion, rol, estado FROM public.usuarios WHERE estado = true ORDER BY nombre_completo ASC;";
                using var cmd = new NpgsqlCommand(sql, conn);
                using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    list.Add(MapReaderToDto(reader));
                }
                return list;
            }
            catch
            {
                return await GetAllFromMongoAsync(ct);
            }
        }

        private async Task<List<UsuarioDto>> GetAllFromMongoAsync(CancellationToken ct)
        {
            var list = new List<UsuarioDto>();
            try
            {
                var collection = _dbExecutor.GetMongoDatabase().GetCollection<BsonDocument>("usuarios");
                var docs = await collection.Find(Builders<BsonDocument>.Filter.Eq("estado", true)).ToListAsync(ct);
                foreach (var doc in docs)
                {
                    list.Add(FromBson(doc));
                }
            }
            catch
            {
            }
            return list;
        }

        public async Task<List<UsuarioDto>> GetInactivosAsync(CancellationToken ct = default)
        {
            var list = new List<UsuarioDto>();
            var source = await _dbExecutor.GetActiveDatabaseAsync();
            if (source == ActiveDbSource.Supabase || source == ActiveDbSource.Aiven)
            {
                try
                {
                    using var conn = await _dbExecutor.GetPostgresConnectionAsync();
                    const string sql = "SELECT id_usuario, nombre_completo, email, cedula, telefono, direccion, rol, estado FROM public.usuarios WHERE estado = false ORDER BY nombre_completo ASC;";
                    using var cmd = new NpgsqlCommand(sql, conn);
                    using var reader = await cmd.ExecuteReaderAsync(ct);
                    while (await reader.ReadAsync(ct))
                    {
                        list.Add(MapReaderToDto(reader));
                    }
                    return list;
                }
                catch when (source == ActiveDbSource.Supabase)
                {
                    return await GetInactivosFromFallbackAsync(ct);
                }
            }
            else
            {
                return await GetInactivosFromMongoAsync(ct);
            }
        }

        private async Task<List<UsuarioDto>> GetInactivosFromFallbackAsync(CancellationToken ct)
        {
            var list = new List<UsuarioDto>();
            try
            {
                using var conn = new NpgsqlConnection(ParsePostgresUrl(Environment.GetEnvironmentVariable("AIVEN_URL")));
                await conn.OpenAsync(ct);
                const string sql = "SELECT id_usuario, nombre_completo, email, cedula, telefono, direccion, rol, estado FROM public.usuarios WHERE estado = false ORDER BY nombre_completo ASC;";
                using var cmd = new NpgsqlCommand(sql, conn);
                using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    list.Add(MapReaderToDto(reader));
                }
                return list;
            }
            catch
            {
                return await GetInactivosFromMongoAsync(ct);
            }
        }

        private async Task<List<UsuarioDto>> GetInactivosFromMongoAsync(CancellationToken ct)
        {
            var list = new List<UsuarioDto>();
            try
            {
                var collection = _dbExecutor.GetMongoDatabase().GetCollection<BsonDocument>("usuarios");
                var docs = await collection.Find(Builders<BsonDocument>.Filter.Eq("estado", false)).ToListAsync(ct);
                foreach (var doc in docs)
                {
                    list.Add(FromBson(doc));
                }
            }
            catch
            {
            }
            return list;
        }

        public async Task<UsuarioDto> AddAsync(UsuarioDto usuario, CancellationToken ct = default)
        {
            var source = await _dbExecutor.GetActiveDatabaseAsync();
            _dbExecutor.CheckWriteAccess(source);

            if (source == ActiveDbSource.Supabase)
            {
                using var conn = await _dbExecutor.GetPostgresConnectionAsync();
                const string sql = @"
                    INSERT INTO public.usuarios (id_usuario, nombre_completo, email, cedula, telefono, direccion, rol, estado)
                    VALUES ($1, $2, $3, $4, $5, $6, $7, $8)
                    RETURNING id_usuario, nombre_completo, email, cedula, telefono, direccion, rol, estado;";
                using var cmd = new NpgsqlCommand(sql, conn);
                cmd.Parameters.AddWithValue(usuario.IdUsuario);
                cmd.Parameters.AddWithValue(usuario.NombreCompleto);
                cmd.Parameters.AddWithValue(usuario.Email);
                cmd.Parameters.AddWithValue(usuario.Cedula);
                cmd.Parameters.AddWithValue(usuario.Telefono);
                cmd.Parameters.AddWithValue(usuario.Direccion);
                cmd.Parameters.AddWithValue(usuario.Rol);
                cmd.Parameters.AddWithValue(usuario.Estado);

                using var reader = await cmd.ExecuteReaderAsync(ct);
                if (await reader.ReadAsync(ct))
                {
                    var inserted = MapReaderToDto(reader);
                    _ = Task.Run(() => SyncToMongoBackgroundAsync(inserted));
                    return inserted;
                }
                throw new InvalidOperationException("No se pudo insertar el usuario en Supabase.");
            }
            else if (source == ActiveDbSource.MongoDB)
            {
                var collection = _dbExecutor.GetMongoDatabase().GetCollection<BsonDocument>("usuarios");
                var doc = ToBson(usuario);
                await collection.InsertOneAsync(doc, null, ct);
                return usuario;
            }
            throw new InvalidOperationException("Escritura no disponible.");
        }

        public async Task<UsuarioDto?> UpdateAsync(string idUsuario, ActualizarUsuarioRequest request, CancellationToken ct = default)
        {
            var source = await _dbExecutor.GetActiveDatabaseAsync();
            _dbExecutor.CheckWriteAccess(source);

            if (source == ActiveDbSource.Supabase)
            {
                var existing = await GetByIdAsync(idUsuario, ct);
                if (existing == null) return null;

                var nombre = request.NombreCompleto ?? existing.NombreCompleto;
                var cedula = request.Cedula ?? existing.Cedula;
                var telefono = request.Telefono ?? existing.Telefono;
                var direccion = request.Direccion ?? existing.Direccion;

                using var conn = await _dbExecutor.GetPostgresConnectionAsync();
                const string sql = @"
                    UPDATE public.usuarios
                    SET nombre_completo = $1, cedula = $2, telefono = $3, direccion = $4
                    WHERE id_usuario = $5 AND estado = true
                    RETURNING id_usuario, nombre_completo, email, cedula, telefono, direccion, rol, estado;";
                using var cmd = new NpgsqlCommand(sql, conn);
                cmd.Parameters.AddWithValue(nombre);
                cmd.Parameters.AddWithValue(cedula);
                cmd.Parameters.AddWithValue(telefono);
                cmd.Parameters.AddWithValue(direccion);
                cmd.Parameters.AddWithValue(idUsuario);

                using var reader = await cmd.ExecuteReaderAsync(ct);
                if (await reader.ReadAsync(ct))
                {
                    var updated = MapReaderToDto(reader);
                    _ = Task.Run(() => SyncToMongoBackgroundAsync(updated));
                    return updated;
                }
            }
            else if (source == ActiveDbSource.MongoDB)
            {
                var collection = _dbExecutor.GetMongoDatabase().GetCollection<BsonDocument>("usuarios");
                var filter = Builders<BsonDocument>.Filter.And(
                    Builders<BsonDocument>.Filter.Eq("_id", idUsuario),
                    Builders<BsonDocument>.Filter.Eq("estado", true)
                );
                var doc = await collection.Find(filter).FirstOrDefaultAsync(ct);
                if (doc != null)
                {
                    var updatedDto = new UsuarioDto(
                        IdUsuario: idUsuario,
                        NombreCompleto: request.NombreCompleto ?? doc["nombre_completo"].AsString,
                        Email: doc["email"].AsString,
                        Cedula: request.Cedula ?? doc["cedula"].AsString,
                        Telefono: request.Telefono ?? doc["telefono"].AsString,
                        Direccion: request.Direccion ?? doc["direccion"].AsString,
                        Rol: doc["rol"].AsString,
                        Estado: true
                    );
                    await collection.ReplaceOneAsync(filter, ToBson(updatedDto), cancellationToken: ct);
                    return updatedDto;
                }
            }
            return null;
        }

        public async Task<UsuarioDto?> UpdatePerfilAsync(string idUsuario, EditarPerfilRequest request, CancellationToken ct = default)
        {
            var source = await _dbExecutor.GetActiveDatabaseAsync();
            _dbExecutor.CheckWriteAccess(source);

            if (source == ActiveDbSource.Supabase)
            {
                var existing = await GetByIdAsync(idUsuario, ct);
                if (existing == null) return null;

                var nombre = request.NombreCompleto ?? existing.NombreCompleto;
                var cedula = request.Cedula ?? existing.Cedula;
                var telefono = request.Telefono ?? existing.Telefono;
                var direccion = request.Direccion ?? existing.Direccion;

                using var conn = await _dbExecutor.GetPostgresConnectionAsync();
                const string sql = @"
                    UPDATE public.usuarios
                    SET nombre_completo = $1, cedula = $2, telefono = $3, direccion = $4
                    WHERE id_usuario = $5 AND estado = true
                    RETURNING id_usuario, nombre_completo, email, cedula, telefono, direccion, rol, estado;";
                using var cmd = new NpgsqlCommand(sql, conn);
                cmd.Parameters.AddWithValue(nombre);
                cmd.Parameters.AddWithValue(cedula);
                cmd.Parameters.AddWithValue(telefono);
                cmd.Parameters.AddWithValue(direccion);
                cmd.Parameters.AddWithValue(idUsuario);

                using var reader = await cmd.ExecuteReaderAsync(ct);
                if (await reader.ReadAsync(ct))
                {
                    var updated = MapReaderToDto(reader);
                    _ = Task.Run(() => SyncToMongoBackgroundAsync(updated));
                    return updated;
                }
            }
            else if (source == ActiveDbSource.MongoDB)
            {
                var collection = _dbExecutor.GetMongoDatabase().GetCollection<BsonDocument>("usuarios");
                var filter = Builders<BsonDocument>.Filter.And(
                    Builders<BsonDocument>.Filter.Eq("_id", idUsuario),
                    Builders<BsonDocument>.Filter.Eq("estado", true)
                );
                var doc = await collection.Find(filter).FirstOrDefaultAsync(ct);
                if (doc != null)
                {
                    var updatedDto = new UsuarioDto(
                        IdUsuario: idUsuario,
                        NombreCompleto: request.NombreCompleto ?? doc["nombre_completo"].AsString,
                        Email: doc["email"].AsString,
                        Cedula: request.Cedula ?? doc["cedula"].AsString,
                        Telefono: request.Telefono ?? doc["telefono"].AsString,
                        Direccion: request.Direccion ?? doc["direccion"].AsString,
                        Rol: doc["rol"].AsString,
                        Estado: true
                    );
                    await collection.ReplaceOneAsync(filter, ToBson(updatedDto), cancellationToken: ct);
                    return updatedDto;
                }
            }
            return null;
        }

        public async Task<bool> DeleteAsync(string idUsuario, CancellationToken ct = default)
        {
            var source = await _dbExecutor.GetActiveDatabaseAsync();
            _dbExecutor.CheckWriteAccess(source);

            if (source == ActiveDbSource.Supabase)
            {
                using var conn = await _dbExecutor.GetPostgresConnectionAsync();
                const string sql = "UPDATE public.usuarios SET estado = false WHERE id_usuario = $1 AND estado = true;";
                using var cmd = new NpgsqlCommand(sql, conn);
                cmd.Parameters.AddWithValue(idUsuario);
                var rows = await cmd.ExecuteNonQueryAsync(ct);
                if (rows > 0)
                {
                    _ = Task.Run(() => SyncDeleteToMongoBackgroundAsync(idUsuario));
                    return true;
                }
            }
            else if (source == ActiveDbSource.MongoDB)
            {
                var collection = _dbExecutor.GetMongoDatabase().GetCollection<BsonDocument>("usuarios");
                var filter = Builders<BsonDocument>.Filter.Eq("_id", idUsuario);
                var result = await collection.UpdateOneAsync(filter, Builders<BsonDocument>.Update.Set("estado", false), null, ct);
                return result.ModifiedCount > 0;
            }
            return false;
        }

        public async Task<bool> ReactivarAsync(string idUsuario, CancellationToken ct = default)
        {
            var source = await _dbExecutor.GetActiveDatabaseAsync();
            _dbExecutor.CheckWriteAccess(source);

            if (source == ActiveDbSource.Supabase)
            {
                using var conn = await _dbExecutor.GetPostgresConnectionAsync();
                const string sql = "UPDATE public.usuarios SET estado = true WHERE id_usuario = $1 AND estado = false;";
                using var cmd = new NpgsqlCommand(sql, conn);
                cmd.Parameters.AddWithValue(idUsuario);
                var rows = await cmd.ExecuteNonQueryAsync(ct);
                if (rows > 0)
                {
                    var updated = await GetByIdAsync(idUsuario, ct);
                    if (updated != null)
                    {
                        _ = Task.Run(() => SyncToMongoBackgroundAsync(updated));
                    }
                    return true;
                }
            }
            else if (source == ActiveDbSource.MongoDB)
            {
                var collection = _dbExecutor.GetMongoDatabase().GetCollection<BsonDocument>("usuarios");
                var filter = Builders<BsonDocument>.Filter.Eq("_id", idUsuario);
                var result = await collection.UpdateOneAsync(filter, Builders<BsonDocument>.Update.Set("estado", true), null, ct);
                return result.ModifiedCount > 0;
            }
            return false;
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
