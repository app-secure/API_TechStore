using System;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Npgsql;
using MongoDB.Driver;
using MongoDB.Bson;

namespace TechStore360.Data
{
    public enum ActiveDbSource
    {
        Supabase,
        Aiven,
        MongoDB
    }

    public class ResilientDbExecutor
    {
        private readonly string _supabaseConnStr;
        private readonly string _aivenConnStr;
        private readonly string _mongoUri;
        private readonly MongoClient _mongoClient;
        
        private ActiveDbSource _cachedSource = ActiveDbSource.Supabase;
        private DateTime _lastCheck = DateTime.MinValue;
        private readonly SemaphoreSlim _semaphore = new SemaphoreSlim(1, 1);

        public ResilientDbExecutor(IConfiguration configuration)
        {
            var supabaseEnv = Environment.GetEnvironmentVariable("SUPABASE_URL") ?? configuration["ConnectionStrings:Supabase"];
            var aivenEnv = Environment.GetEnvironmentVariable("AIVEN_URL") ?? configuration["ConnectionStrings:Aiven"];
            var mongoEnv = Environment.GetEnvironmentVariable("MONGODB_URI") ?? configuration["ConnectionStrings:MongoDB"];

            _supabaseConnStr = ParsePostgresUrl(supabaseEnv);
            _aivenConnStr = ParsePostgresUrl(aivenEnv);
            _mongoUri = mongoEnv ?? "";
            
            if (!string.IsNullOrWhiteSpace(_mongoUri))
            {
                _mongoClient = new MongoClient(_mongoUri);
            }
            else
            {
                _mongoClient = null!;
            }
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

        public async Task<ActiveDbSource> GetActiveDatabaseAsync()
        {
            if (DateTime.UtcNow - _lastCheck < TimeSpan.FromSeconds(5))
            {
                return _cachedSource;
            }

            await _semaphore.WaitAsync();
            try
            {
                if (DateTime.UtcNow - _lastCheck < TimeSpan.FromSeconds(5))
                {
                    return _cachedSource;
                }

                if (await TestPostgresAsync(_supabaseConnStr))
                {
                    _cachedSource = ActiveDbSource.Supabase;
                }
                else if (await TestPostgresAsync(_aivenConnStr))
                {
                    _cachedSource = ActiveDbSource.Aiven;
                }
                else if (await TestMongoAsync())
                {
                    _cachedSource = ActiveDbSource.MongoDB;
                }
                else
                {
                    _cachedSource = ActiveDbSource.Supabase;
                }

                _lastCheck = DateTime.UtcNow;
                return _cachedSource;
            }
            finally
            {
                _semaphore.Release();
            }
        }

        private async Task<bool> TestPostgresAsync(string connStr)
        {
            if (string.IsNullOrWhiteSpace(connStr)) return false;
            try
            {
                using var conn = new NpgsqlConnection(connStr);
                await conn.OpenAsync();
                using var cmd = new NpgsqlCommand("SELECT 1;", conn);
                await cmd.ExecuteScalarAsync();
                return true;
            }
            catch
            {
                return false;
            }
        }

        private async Task<bool> TestMongoAsync()
        {
            if (_mongoClient == null) return false;
            try
            {
                var db = _mongoClient.GetDatabase("admin");
                await db.RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1));
                return true;
            }
            catch
            {
                return false;
            }
        }

        public async Task<NpgsqlConnection> GetPostgresConnectionAsync()
        {
            var source = await GetActiveDatabaseAsync();
            if (source == ActiveDbSource.Supabase)
            {
                var conn = new NpgsqlConnection(_supabaseConnStr);
                await conn.OpenAsync();
                return conn;
            }
            if (source == ActiveDbSource.Aiven)
            {
                var conn = new NpgsqlConnection(_aivenConnStr);
                await conn.OpenAsync();
                return conn;
            }
            throw new InvalidOperationException("PostgreSQL no disponible en la base de datos activa actual.");
        }

        public IMongoDatabase GetMongoDatabase()
        {
            if (_mongoClient == null) throw new InvalidOperationException("MongoDB no configurado.");
            return _mongoClient.GetDatabase("techstore360");
        }

        public void CheckWriteAccess(ActiveDbSource source)
        {
            if (source == ActiveDbSource.Aiven)
            {
                throw new InvalidOperationException("La base de datos se encuentra en modo de réplica de lectura (Aiven). Operación de escritura no permitida.");
            }
        }
    }
}
