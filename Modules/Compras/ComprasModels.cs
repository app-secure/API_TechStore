using System;
using System.Collections.Generic;
using System.Linq;

namespace TechStore360.Modules.Compras
{
    public class MaestroCompraModel
    {
        public int NumeroFactura { get; set; }
        public string IdUsuario { get; set; } = "";
        public decimal TotalCompra { get; set; }
        public string Estado { get; set; } = "ABIERTA";
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public string MetodoPago { get; set; } = "Efectivo";
        public string LugarEntrega { get; set; } = "Retiro en Tienda";
        public bool RequiereFactura { get; set; } = true;
        public string? CedulaFactura { get; set; }
        public string? NombreFactura { get; set; }
    }

    public class DetalleCompraModel
    {
        public int NumeroDetalle { get; set; }
        public int NumeroFactura { get; set; }
        public int IdProducto { get; set; }
        public int Cantidad { get; set; }
        public decimal PrecioUnitario { get; set; }
    }

    public record CrearCompraRequest(
        string IdUsuario,
        List<CrearDetalleCompraRequest> Detalles,
        string MetodoPago,
        string LugarEntrega,
        bool RequiereFactura,
        string? CedulaFactura = null,
        string? NombreFactura = null
    )
    {
        public void Validar()
        {
            if (string.IsNullOrWhiteSpace(IdUsuario))
                throw new ArgumentException("IdUsuario es obligatorio.");

            if (Detalles is null || Detalles.Count == 0)
                throw new ArgumentException("La compra debe tener al menos un detalle.");

            if (Detalles.Any(d => d.Cantidad <= 0))
                throw new ArgumentException("La cantidad debe ser mayor a 0 en todos los detalles.");

            var metodosValidos = new[] { "Efectivo", "Transferencia", "PayPhone", "TarjetaVirtual", "PayPal" };
            if (!metodosValidos.Contains(MetodoPago))
                throw new ArgumentException($"El método de pago '{MetodoPago}' no está soportado.");

            if (string.IsNullOrWhiteSpace(LugarEntrega))
                throw new ArgumentException("Debe especificar un lugar de entrega o indicar 'Retiro en Tienda'.");

            if (RequiereFactura)
            {
                if (string.IsNullOrWhiteSpace(CedulaFactura))
                    throw new ArgumentException("La cédula o RUC es obligatoria si requiere factura legal.");

                if (string.IsNullOrWhiteSpace(NombreFactura))
                    throw new ArgumentException("La razón social/nombre es obligatorio si requiere factura legal.");

                string documentoLimpio = CedulaFactura.Replace("-", "").Trim();
                if (documentoLimpio.Length != 10 && documentoLimpio.Length != 13)
                    throw new ArgumentException("El documento de identidad ecuatoriano debe tener exactamente 10 dígitos o 13 dígitos.");
            }
        }
    }

    public record CrearDetalleCompraRequest(
        int IdProducto,
        int Cantidad
    );

    public record CompraCreada(
        int NumeroFactura,
        decimal TotalCompra
    );

    public record CrearCompraResponse(
        int NumeroFactura,
        decimal TotalCompra,
        string Estado,
        string Mensaje,
        bool FacturaEmitida
    );

    public record CompraCompletaDto(
        int NumeroFactura,
        string IdUsuario,
        decimal TotalCompra,
        decimal Subtotal,
        decimal Iva,
        string Estado,
        string MetodoPago,
        string LugarEntrega,
        bool RequiereFactura,
        string NombreFactura,
        string CedulaFactura,
        DateTime CreatedAt,
        List<DetalleCompraDto> Detalles
    );

    public record DetalleCompraDto(
        int IdProducto,
        string NombreProducto,
        string? UrlImagen,
        int Cantidad,
        decimal PrecioUnitario,
        decimal Subtotal
    );

    public record CompraResumenDto(
        int NumeroFactura,
        string IdUsuario,
        decimal TotalCompra,
        string Estado,
        string MetodoPago,
        DateTime CreatedAt
    );
}
