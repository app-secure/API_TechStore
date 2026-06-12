using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TechStore360.Modules.Productos;

namespace TechStore360.Modules.Compras
{
    public interface IComprasService
    {
        Task<CompraCreada> RegistrarCompraAsync(CrearCompraRequest request, CancellationToken ct);
        Task<bool> AnularCompraAsync(int numeroFactura, CancellationToken ct);
        Task<CompraCompletaDto?> ObtenerDetalleCompraAsync(int idCompra, CancellationToken ct);
        Task<List<CompraResumenDto>> ListarComprasAsync(CancellationToken ct);
        Task<List<CompraResumenDto>> ListarComprasPorUsuarioAsync(string idUsuario, CancellationToken ct);
        Task<bool> UpdateStatusAsync(int numeroFactura, string nuevoEstado, CancellationToken cancellationToken = default);
    }

    public class ComprasService : IComprasService
    {
        private readonly IComprasRepository _repository;
        private readonly IProductosService _productosService;

        public ComprasService(IComprasRepository repository, IProductosService productosService)
        {
            _repository = repository;
            _productosService = productosService;
        }

        public async Task<CompraCreada> RegistrarCompraAsync(CrearCompraRequest request, CancellationToken ct)
        {
            request.Validar();
            var detallesAgrupados = request.Detalles
                .GroupBy(d => d.IdProducto)
                .Select(g => new { IdProducto = g.Key, Cantidad = g.Sum(x => x.Cantidad) });

            var ids = detallesAgrupados.Select(d => d.IdProducto).ToList();
            var productos = await _productosService.GetByIdsAsync(ids, ct);

            if (productos.Count != ids.Count)
            {
                var existentes = productos.Select(p => p.IdProducto).ToList();
                var faltantes = ids.Except(existentes).ToArray();
                throw new ArgumentException($"Productos no existentes: {string.Join(", ", faltantes)}");
            }

            decimal totalCompra = 0m;
            var detallesModel = new List<DetalleCompraModel>();
            foreach (var detalle in detallesAgrupados)
            {
                var prod = productos.First(p => p.IdProducto == detalle.IdProducto);
                if (prod.Stock < detalle.Cantidad)
                    throw new InvalidOperationException($"Stock insuficiente para el producto {detalle.IdProducto}.");

                totalCompra += prod.Precio * detalle.Cantidad;
                detallesModel.Add(new DetalleCompraModel
                {
                    IdProducto = detalle.IdProducto,
                    Cantidad = detalle.Cantidad,
                    PrecioUnitario = prod.Precio
                });
            }

            var maestro = new MaestroCompraModel
            {
                IdUsuario = request.IdUsuario,
                CreatedAt = DateTime.UtcNow,
                Estado = (request.MetodoPago == "PayPhone" || request.MetodoPago == "PayPal") ? "PENDIENTE_PAGO" : "ABIERTA",
                MetodoPago = request.MetodoPago,
                LugarEntrega = request.LugarEntrega,
                RequiereFactura = true,
                CedulaFactura = request.CedulaFactura ?? "",
                NombreFactura = request.NombreFactura ?? "",
                TotalCompra = totalCompra * 1.15m
            };

            var resultado = await _repository.AddAsync(maestro, detallesModel, ct);

            return resultado;
        }

        public async Task<bool> AnularCompraAsync(int numeroFactura, CancellationToken ct)
        {
            var compra = await _repository.GetCompraCompletaAsync(numeroFactura, ct);
            if (compra == null || compra.Estado == "ANULADA") return false;

            var exitoso = await _repository.UpdateStatusAsync(numeroFactura, "ANULADA", ct);
            if (exitoso)
            {
                foreach (var item in compra.Detalles)
                {
                    await _productosService.UpdateStockAsync(item.IdProducto, item.Cantidad, ct);
                }
                return true;
            }
            return false;
        }

        public async Task<CompraCompletaDto?> ObtenerDetalleCompraAsync(int idCompra, CancellationToken ct)
        {
            return await _repository.GetCompraCompletaAsync(idCompra, ct);
        }

        public async Task<List<CompraResumenDto>> ListarComprasAsync(CancellationToken ct)
        {
            return await _repository.GetAllAsync(ct);
        }

        public async Task<List<CompraResumenDto>> ListarComprasPorUsuarioAsync(string idUsuario, CancellationToken ct)
        {
            return await _repository.GetByUsuarioAsync(idUsuario, ct);
        }

        public async Task<bool> UpdateStatusAsync(int numeroFactura, string nuevoEstado, CancellationToken cancellationToken = default)
        {
            return await _repository.UpdateStatusAsync(numeroFactura, nuevoEstado, cancellationToken);
        }
    }
}
