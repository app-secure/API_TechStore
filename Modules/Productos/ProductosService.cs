namespace TechStore360.Modules.Productos;

public interface IProductosService
{
    Task<IEnumerable<ProductoDto>> ObtenerCatalogoAsync(CancellationToken ct);
    Task<IEnumerable<ProductoDto>> ObtenerInactivosAsync(CancellationToken ct);
    Task<ProductoDto?> ObtenerPorIdAsync(int id, CancellationToken ct);
    Task<ProductoDto> CrearProductoAsync(CrearProductoRequest request, CancellationToken ct);
    Task<ProductoDto?> ActualizarProductoAsync(int id, ActualizarProductoRequest request, CancellationToken ct);
    Task<bool> EliminarProductoAsync(int id, CancellationToken ct);
    Task<IReadOnlyList<ProductoDto>> GetByIdsAsync(IEnumerable<int> ids, CancellationToken ct);
    Task<bool> UpdateStockAsync(int id, int cantidadDelta, CancellationToken ct);
    Task<bool> ReactivarProductoAsync(int id, CancellationToken ct);
}

public class ProductosService : IProductosService
{
    private readonly IProductosRepository _repository;

    public ProductosService(IProductosRepository repository)
    {
        _repository = repository;
    }

    public async Task<IEnumerable<ProductoDto>> ObtenerCatalogoAsync(CancellationToken ct)
    {
        return await _repository.GetAllAsync(ct);
    }

    public async Task<ProductoDto?> ObtenerPorIdAsync(int id, CancellationToken ct)
    {
        return await _repository.GetByIdAsync(id, ct);
    }

    public async Task<ProductoDto> CrearProductoAsync(CrearProductoRequest request, CancellationToken ct)
    {
        return await _repository.AddAsync(request, ct);
    }

    public async Task<ProductoDto?> ActualizarProductoAsync(int id, ActualizarProductoRequest request, CancellationToken ct)
    {
        return await _repository.UpdateAsync(id, request, ct);
    }

    public async Task<bool> EliminarProductoAsync(int id, CancellationToken ct)
    {
        return await _repository.DeleteAsync(id, ct);
    }

    public async Task<IReadOnlyList<ProductoDto>> GetByIdsAsync(IEnumerable<int> ids, CancellationToken ct)
    {
        return await _repository.GetByIdsAsync(ids, ct);
    }

    public async Task<bool> UpdateStockAsync(int id, int cantidadDelta, CancellationToken ct)
    {
        return await _repository.UpdateStockAsync(id, cantidadDelta, ct);
    }

    public async Task<bool> ReactivarProductoAsync(int id, CancellationToken ct)
    {
        return await _repository.ReactivarAsync(id, ct);
    }

    public async Task<IEnumerable<ProductoDto>> ObtenerInactivosAsync(CancellationToken ct)
    {
        return await _repository.GetInactivosAsync(ct);
    }
}
