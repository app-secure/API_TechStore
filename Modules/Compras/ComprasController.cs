using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Text;
using TechStore360.ExternalServices;
using TechStore360.Modules.Usuarios;
using WkHtmlToPdfDotNet;
using WkHtmlToPdfDotNet.Contracts;
using QRCoder;
using TechStore360.Core.Events;
using TechStore360.Core.Messaging;

namespace TechStore360.Modules.Compras
{
    [ApiController]
    [Route("api/[controller]")]
    public class ComprasController : ControllerBase
    {
        private readonly IComprasService _service;
        private readonly IAuthorizationService _authorizationService;
        private readonly IUsuariosService _usuariosService;
        private readonly IKafkaProducer _kafkaProducer;

        public ComprasController(
                        IComprasService service,
                        IAuthorizationService authorizationService,
                        IUsuariosService usuariosService,
                        NotificationSmsService smsService, 
                        IKafkaProducer kafkaProducer)
        {
            _service = service;
            _authorizationService = authorizationService;
            _usuariosService = usuariosService;
            _kafkaProducer = kafkaProducer;
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

                if (request.MetodoPago != "PayPhone" && request.MetodoPago != "PayPal")
                {
                    try
                    {
                        var pagoEvent = new PagoConfirmadoEvent(compra.NumeroFactura, DateTime.UtcNow);
                        await _kafkaProducer.PublishAsync("facturas-pendientes", compra.NumeroFactura.ToString(), pagoEvent, ct);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error al publicar evento de confirmación de pago en Kafka: {ex.Message}");
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
                            var pagoEvent = new PagoConfirmadoEvent(numeroFactura, DateTime.UtcNow);
                            await _kafkaProducer.PublishAsync("facturas-pendientes", numeroFactura.ToString(), pagoEvent, ct);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Error al publicar evento webhook PayPhone en Kafka: {ex.Message}");
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