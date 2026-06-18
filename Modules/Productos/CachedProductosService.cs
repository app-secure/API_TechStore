using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TechStore360.Core.Caching;

namespace TechStore360.Modules.Productos
{
    public class CachedProductosService : IProductosService
    {
        private readonly IProductosService _inner;
        private readonly IRedisCacheService _cache;

        private const string CatalogKey = "productos:catalogo";
        private const string InactiveKey = "productos:inactivos";
        private static string GetProductKey(int id) => $"productos:id:{id}";

        public CachedProductosService(IProductosService inner, IRedisCacheService cache)
        {
            _inner = inner;
            _cache = cache;
        }

        public async Task<IEnumerable<ProductoDto>> ObtenerCatalogoAsync(CancellationToken ct)
        {
            var cached = await _cache.GetAsync<List<ProductoDto>>(CatalogKey, ct);
            if (cached != null)
            {
                return cached;
            }

            var products = (await _inner.ObtenerCatalogoAsync(ct)).ToList();
            await _cache.SetAsync(CatalogKey, products, TimeSpan.FromMinutes(10), ct);
            return products;
        }

        public async Task<IEnumerable<ProductoDto>> ObtenerInactivosAsync(CancellationToken ct)
        {
            var cached = await _cache.GetAsync<List<ProductoDto>>(InactiveKey, ct);
            if (cached != null)
            {
                return cached;
            }

            var products = (await _inner.ObtenerInactivosAsync(ct)).ToList();
            await _cache.SetAsync(InactiveKey, products, TimeSpan.FromMinutes(10), ct);
            return products;
        }

        public async Task<ProductoDto?> ObtenerPorIdAsync(int id, CancellationToken ct)
        {
            string key = GetProductKey(id);
            var cached = await _cache.GetAsync<ProductoDto>(key, ct);
            if (cached != null)
            {
                return cached;
            }

            var product = await _inner.ObtenerPorIdAsync(id, ct);
            if (product != null)
            {
                await _cache.SetAsync(key, product, TimeSpan.FromMinutes(20), ct);
            }
            return product;
        }

        public async Task<IReadOnlyList<ProductoDto>> GetByIdsAsync(IEnumerable<int> ids, CancellationToken ct)
        {
            var idList = ids.ToList();
            if (!idList.Any()) return new List<ProductoDto>();

            var result = new List<ProductoDto>();
            var misses = new List<int>();

            foreach (var id in idList)
            {
                var cached = await _cache.GetAsync<ProductoDto>(GetProductKey(id), ct);
                if (cached != null)
                {
                    result.Add(cached);
                }
                else
                {
                    misses.Add(id);
                }
            }

            if (misses.Any())
            {
                var fetched = await _inner.GetByIdsAsync(misses, ct);
                foreach (var product in fetched)
                {
                    result.Add(product);
                    await _cache.SetAsync(GetProductKey(product.IdProducto), product, TimeSpan.FromMinutes(20), ct);
                }
            }

            return result;
        }

        public async Task<ProductoDto> CrearProductoAsync(CrearProductoRequest request, CancellationToken ct)
        {
            var product = await _inner.CrearProductoAsync(request, ct);
            await InvalidateCacheAsync(product.IdProducto, ct);
            return product;
        }

        public async Task<ProductoDto?> ActualizarProductoAsync(int id, ActualizarProductoRequest request, CancellationToken ct)
        {
            var product = await _inner.ActualizarProductoAsync(id, request, ct);
            if (product != null)
            {
                await InvalidateCacheAsync(id, ct);
            }
            return product;
        }

        public async Task<bool> EliminarProductoAsync(int id, CancellationToken ct)
        {
            var deleted = await _inner.EliminarProductoAsync(id, ct);
            if (deleted)
            {
                await InvalidateCacheAsync(id, ct);
            }
            return deleted;
        }

        public async Task<bool> ReactivarProductoAsync(int id, CancellationToken ct)
        {
            var reactivated = await _inner.ReactivarProductoAsync(id, ct);
            if (reactivated)
            {
                await InvalidateCacheAsync(id, ct);
            }
            return reactivated;
        }

        public async Task<bool> UpdateStockAsync(int id, int cantidadDelta, CancellationToken ct)
        {
            var updated = await _inner.UpdateStockAsync(id, cantidadDelta, ct);
            if (updated)
            {
                await InvalidateCacheAsync(id, ct);
            }
            return updated;
        }

        private async Task InvalidateCacheAsync(int id, CancellationToken ct)
        {
            await _cache.RemoveAsync(CatalogKey, ct);
            await _cache.RemoveAsync(InactiveKey, ct);
            await _cache.RemoveAsync(GetProductKey(id), ct);
        }
    }
}
