using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StackExchange.Redis;

namespace TechStore360.Core.Caching
{
    public interface IRedisCacheService
    {
        Task<T?> GetAsync<T>(string key, CancellationToken ct = default);
        Task SetAsync<T>(string key, T value, TimeSpan? expiry = null, CancellationToken ct = default);
        Task RemoveAsync(string key, CancellationToken ct = default);
    }

    public class RedisCacheService : IRedisCacheService
    {
        private readonly IConnectionMultiplexer _redis;
        private readonly IDatabase _database;

        public RedisCacheService(IConnectionMultiplexer redis)
        {
            _redis = redis;
            _database = redis.GetDatabase();
        }

        public async Task<T?> GetAsync<T>(string key, CancellationToken ct = default)
        {
            try
            {
                var value = await _database.StringGetAsync(key);
                if (value.IsNullOrEmpty)
                {
                    return default;
                }

                return JsonSerializer.Deserialize<T>(value!);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Redis Error] Error al obtener la clave '{key}': {ex.Message}");
                return default;
            }
        }

        public async Task SetAsync<T>(string key, T value, TimeSpan? expiry = null, CancellationToken ct = default)
        {
            try
            {
                var serialized = JsonSerializer.Serialize(value);
                await _database.StringSetAsync(key, serialized, expiry);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Redis Error] Error al guardar la clave '{key}': {ex.Message}");
            }
        }

        public async Task RemoveAsync(string key, CancellationToken ct = default)
        {
            try
            {
                await _database.KeyDeleteAsync(key);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Redis Error] Error al eliminar la clave '{key}': {ex.Message}");
            }
        }
    }
}
