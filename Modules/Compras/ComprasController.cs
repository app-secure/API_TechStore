using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Text;
using TechStore360.ExternalServices;
using TechStore360.Modules.Usuarios;
using WkHtmlToPdfDotNet;
using WkHtmlToPdfDotNet.Contracts;
using QRCoder;

namespace TechStore360.Modules.Compras
{
    [ApiController]
    [Route("api/[controller]")]
    public class ComprasController : ControllerBase
    {
        private readonly IComprasService _service;
        private readonly IAuthorizationService _authorizationService;
        private readonly IUsuariosService _usuariosService;
        private readonly TechStore360.Modulos.Factura.IFacturacionService _facturacionService;

        public ComprasController(
                        IComprasService service,
                        IAuthorizationService authorizationService,
                        IUsuariosService usuariosService,
                        NotificationSmsService smsService, // mantenido en DI para compatibilidad, no se usa
                        TechStore360.Modulos.Factura.IFacturacionService facturacionService)
        {
            _service = service;
            _authorizationService = authorizationService;
            _usuariosService = usuariosService;
            _facturacionService = facturacionService;
        }

        [HttpPost]
        [Authorize]
        public async Task<IActionResult> Crear([FromBody] CrearCompraRequest request, CancellationToken ct)
        {
            try { request.Validar(); }
            catch (ArgumentException ex) { return BadRequest(new { mensaje = ex.Message }); }

            try
            {
                var compra = await _service.RegistrarCompraAsync(request, ct);

                // Si el método de pago es inmediato (no es PayPhone ni PayPal), enviar factura por email
                if (request.MetodoPago != "PayPhone" && request.MetodoPago != "PayPal")
                {
                    try
                    {
                        await _facturacionService.EnviarFacturaPorEmailAsync(compra.NumeroFactura, ct);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error al enviar factura por correo: {ex.Message}");
                    }
                }

                var response = new CrearCompraResponse(
                    NumeroFactura: compra.NumeroFactura,
                    TotalCompra: compra.TotalCompra,
                    Estado: (request.MetodoPago == "PayPhone" || request.MetodoPago == "PayPal") ? "PENDIENTE_PAGO" : "ABIERTA",
                    Mensaje: $"Compra procesada mediante {request.MetodoPago}. Despacho asignado a: {request.LugarEntrega}.",
                    FacturaEmitida: true
                );

                return Created($"/api/compras/{compra.NumeroFactura}", response);
            }
            catch (InvalidOperationException ex)
            {
                return Conflict(new { mensaje = ex.Message });
            }
        }

        [HttpGet]
        [Authorize(Policy = "Admin")]
        public async Task<IActionResult> ObtenerTodas(CancellationToken ct)
        {
            var compras = await _service.ListarComprasAsync(ct);
            return Ok(compras);
        }

        [HttpGet("usuario/{idUsuario}")]
        [Authorize]
        public async Task<IActionResult> ObtenerPorUsuario(string idUsuario, CancellationToken ct)
        {
            var uid = User.FindFirst("user_id")?.Value;
            var esAdmin = (await _authorizationService.AuthorizeAsync(User, "Admin")).Succeeded;

            if (uid != idUsuario && !esAdmin)
                return Forbid();

            var compras = await _service.ListarComprasPorUsuarioAsync(idUsuario, ct);
            return Ok(compras);
        }

        [HttpGet("{idCompra:int}")]
        [Authorize]
        public async Task<IActionResult> ObtenerPorId(int idCompra, CancellationToken ct)
        {
            var uid = User.FindFirst("user_id")?.Value;
            var esAdmin = (await _authorizationService.AuthorizeAsync(User, "Admin")).Succeeded;

            var compra = await _service.ObtenerDetalleCompraAsync(idCompra, ct);
            if (compra == null)
                return NotFound(new { mensaje = "Factura o comprobante no encontrado." });

            if (uid != compra.IdUsuario && !esAdmin)
                return Forbid();

            return Ok(compra);
        }

        [HttpGet("{numeroFactura:int}/qr")]
        public IActionResult GenerarQr(int numeroFactura, [FromServices] IConfiguration config)
        {
            var frontendUrl = Environment.GetEnvironmentVariable("FRONTEND_URL")
                ?? config["FrontendUrl"];

            var contenido = string.IsNullOrWhiteSpace(frontendUrl)
                ? numeroFactura.ToString()
                : $"{frontendUrl.TrimEnd('/')}/compras/{numeroFactura}";

            using var qrGenerator = new QRCodeGenerator();
            var qrData = qrGenerator.CreateQrCode(contenido, QRCodeGenerator.ECCLevel.M);
            using var qrCode = new PngByteQRCode(qrData);
            var pngBytes = qrCode.GetGraphic(10);

            return File(pngBytes, "image/png");
        }

        [HttpDelete("{numeroFactura:int}")]
        [Authorize]
        public async Task<IActionResult> Anular(int numeroFactura, CancellationToken ct)
        {
            var uid = User.FindFirst("user_id")?.Value;
            var esAdmin = (await _authorizationService.AuthorizeAsync(User, "Admin")).Succeeded;

            var compra = await _service.ObtenerDetalleCompraAsync(numeroFactura, ct);
            if (compra == null)
            {
                return NotFound(new { mensaje = $"La Factura #{numeroFactura} no existe o ya fue dada de baja." });
            }

            if (uid != compra.IdUsuario && !esAdmin)
                return Forbid();

            if (!esAdmin && compra.Estado != "PENDIENTE_PAGO")
            {
                return BadRequest(new { mensaje = "Solo puedes anular compras que estén pendientes de pago." });
            }

            var anulada = await _service.AnularCompraAsync(numeroFactura, ct);
            if (!anulada)
            {
                return NotFound(new { mensaje = $"La Factura #{numeroFactura} no pudo ser anulada." });
            }
            return Ok(new { mensaje = $"La Factura #{numeroFactura} ha sido anulada correctamente." });
        }

        [HttpPost("webhook-payphone")]
        [AllowAnonymous]
        public async Task<IActionResult> WebhookPayPhone([FromBody] System.Text.Json.JsonElement payload, CancellationToken ct)
        {
            try
            {
                if (!payload.TryGetProperty("clientTransactionId", out var transactionIdProp) ||
                    !payload.TryGetProperty("status", out var statusProp))
                {
                    return BadRequest(new { mensaje = "Payload de PayPhone inválido." });
                }

                int numeroFactura = int.Parse(transactionIdProp.GetString()!);
                string estadoPago = statusProp.GetString()!;

                if (estadoPago == "Approved")
                {
                    var actualizado = await _service.UpdateStatusAsync(numeroFactura, "ABIERTA", ct);

                    if (actualizado)
                    {
                        try
                        {
                            await _facturacionService.EnviarFacturaPorEmailAsync(numeroFactura, ct);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Error al enviar factura tras webhook PayPhone: {ex.Message}");
                        }
                    }
                }

                return Ok(new { mensaje = "Webhook procesado correctamente por TechStore360." });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error Webhook PayPhone: {ex.Message}");
                return StatusCode(500, new { error = "Error interno procesando la pasarela." });
            }
        }
    }
}