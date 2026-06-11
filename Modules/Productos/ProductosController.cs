using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Threading;
using System.Threading.Tasks;

namespace TechStore360.Modules.Productos
{
    [ApiController]
    [Route("api/[controller]")]
    public class ProductosController : ControllerBase
    {
        private readonly IProductosService _service;

        public ProductosController(IProductosService service)
        {
            _service = service;
        }

        [HttpGet]
        public async Task<IActionResult> ObtenerCatalogo(CancellationToken ct)
        {
            var productos = await _service.ObtenerCatalogoAsync(ct);
            return Ok(productos);
        }

        [HttpGet("inactivos")]
        [Authorize(Policy = "Admin")]
        public async Task<IActionResult> ObtenerInactivos(CancellationToken ct)
        {
            var productos = await _service.ObtenerInactivosAsync(ct);
            return Ok(productos);
        }

        [HttpGet("{id:int}")]
        public async Task<IActionResult> ObtenerPorId(int id, CancellationToken ct)
        {
            var producto = await _service.ObtenerPorIdAsync(id, ct);
            if (producto is null)
            {
                return NotFound(new { mensaje = "Producto no encontrado." });
            }
            return Ok(producto);
        }

        [HttpPost]
        [Authorize(Policy = "Admin")]
        public async Task<IActionResult> Crear([FromBody] CrearProductoRequest request, CancellationToken ct)
        {
            var nuevoProducto = await _service.CrearProductoAsync(request, ct);
            return CreatedAtAction(nameof(ObtenerPorId), new { id = nuevoProducto.IdProducto }, nuevoProducto);
        }

        [HttpPut("{id:int}")]
        [Authorize(Policy = "Admin")]
        public async Task<IActionResult> Actualizar(int id, [FromBody] ActualizarProductoRequest request, CancellationToken ct)
        {
            var actualizado = await _service.ActualizarProductoAsync(id, request, ct);
            if (actualizado is null)
            {
                return NotFound(new { mensaje = "Producto no encontrado." });
            }
            return Ok(actualizado);
        }

        [HttpDelete("{id:int}")]
        [Authorize(Policy = "Admin")]
        public async Task<IActionResult> Eliminar(int id, CancellationToken ct)
        {
            var eliminado = await _service.EliminarProductoAsync(id, ct);
            if (!eliminado)
            {
                return NotFound(new { mensaje = $"Producto #{id} no encontrado o ya estaba desactivado." });
            }
            return Ok(new { mensaje = $"El producto #{id} ha sido desactivado correctamente." });
        }

        [HttpPost("{id:int}/activar")]
        [Authorize(Policy = "Admin")]
        public async Task<IActionResult> Activar(int id, CancellationToken ct)
        {
            var activado = await _service.ReactivarProductoAsync(id, ct);
            if (!activado)
            {
                return NotFound(new { mensaje = $"Producto #{id} no encontrado o ya estaba activo." });
            }
            return Ok(new { mensaje = $"El producto #{id} ha sido activado correctamente." });
        }
    }
}
