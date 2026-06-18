using Microsoft.AspNetCore.Mvc;
using System.Threading;
using System.Threading.Tasks;
using TechStore360.Modules.Compras;

namespace TechStore360.Modules.Pagos
{
    [ApiController]
    [Route("api/[controller]")]
    public class PagosController : ControllerBase
    {
        private readonly IPagosService _pagosService;
        private readonly IComprasService _comprasService;

        public PagosController(IPagosService pagosService, IComprasService comprasService)
        {
            _pagosService = pagosService;
            _comprasService = comprasService;
        }

        [HttpPost("{numeroFactura:int}/confirmar")]
        public async Task<IActionResult> ConfirmarPago(int numeroFactura, CancellationToken ct)
        {
            var result = await _pagosService.ConfirmarPagoManualAsync(numeroFactura, ct);
            if (!result.Success)
            {
                if (result.ErrorMessage!.Contains("no encontrada"))
                    return NotFound(new { mensaje = result.ErrorMessage });
                
                return BadRequest(new { mensaje = result.ErrorMessage });
            }

            return Ok(new ConfirmarPagoResponse(
                result.NumeroFactura,
                result.EstadoAnterior,
                result.EstadoActual,
                result.Mensaje,
                true));
        }

        [HttpGet("{numeroFactura:int}/estado")]
        public async Task<IActionResult> ConsultarEstado(int numeroFactura, CancellationToken ct)
        {
            var compra = await _comprasService.ObtenerDetalleCompraAsync(numeroFactura, ct);
            if (compra == null)
                return NotFound(new { mensaje = $"Compra #{numeroFactura} no encontrada." });

            return Ok(new EstadoPagoResponse(
                compra.NumeroFactura,
                compra.Estado,
                compra.MetodoPago,
                compra.TotalCompra,
                PendientePago: compra.Estado == "PENDIENTE_PAGO",
                PuedeFacturar: true));
        }

        [HttpGet("paypal/client-id")]
        public IActionResult GetPaypalClientId()
        {
            try
            {
                var clientId = _pagosService.GetPaypalClientId();
                return Ok(new { clientId });
            }
            catch (System.InvalidOperationException ex)
            {
                return BadRequest(new { mensaje = ex.Message });
            }
        }

        [HttpPost("paypal/crear")]
        public async Task<IActionResult> CrearPagoPaypalSinOrden([FromBody] CrearCompraRequest request, CancellationToken ct)
        {
            try { request.Validar(); }
            catch (System.ArgumentException ex) { return BadRequest(new { mensaje = ex.Message }); }

            var result = await _pagosService.CrearPagoPaypalSinOrdenAsync(request, Request.Scheme, Request.Host.Value, ct);
            if (!result.Success)
            {
                if (result.StockConflict)
                    return Conflict(new { mensaje = result.ErrorMessage });

                return StatusCode(502, new { mensaje = result.ErrorMessage });
            }

            return Ok(new { approvalUrl = result.ApprovalUrl, orderId = result.OrderId, clientId = result.ClientId });
        }

        [HttpPost("paypal/crear/{numeroFactura:int}")]
        public async Task<IActionResult> CrearPagoPaypalConOrden(int numeroFactura, CancellationToken ct)
        {
            var result = await _pagosService.CrearPagoPaypalConOrdenAsync(numeroFactura, Request.Scheme, Request.Host.Value, ct);
            if (!result.Success)
            {
                if (result.ErrorMessage!.Contains("no encontrada"))
                    return NotFound(new { mensaje = result.ErrorMessage });

                return BadRequest(new { mensaje = result.ErrorMessage });
            }

            return Ok(new { approvalUrl = result.ApprovalUrl, orderId = result.OrderId, clientId = result.ClientId });
        }

        [HttpGet("paypal/success")]
        public async Task<IActionResult> PaypalSuccess(
            [FromQuery] string? token,
            [FromQuery] string? PayerID,
            [FromQuery] int? numeroFactura,
            [FromQuery] bool json = false,
            CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(token))
            {
                if (json) return BadRequest(new { mensaje = "Token de PayPal no proporcionado." });
                return BadRequest("Token de PayPal no proporcionado.");
            }

            var result = await _pagosService.ProcesarExitoPaypalAsync(token, PayerID, numeroFactura, ct);
            if (!result.Success)
            {
                if (json)
                {
                    if (result.StockConflict) return Conflict(new { mensaje = result.ErrorMessage });
                    if (result.ErrorMessage!.Contains("no encontrada")) return NotFound(new { mensaje = result.ErrorMessage });
                    return BadRequest(new { mensaje = result.ErrorMessage });
                }

                return Content(result.RawHtml ?? result.ErrorMessage!, "text/html", System.Text.Encoding.UTF8);
            }

            if (json)
            {
                return Ok(new { success = true, numeroFactura = result.NumeroFactura, total = result.Total, mensaje = result.Mensaje });
            }

            return Content(result.RawHtml!, "text/html", System.Text.Encoding.UTF8);
        }

        [HttpGet("paypal/cancel")]
        public IActionResult PaypalCancel()
        {
            string html = $@"<!DOCTYPE html>
<html lang='es'>
<head>
    <meta charset='utf-8'>
    <meta name='viewport' content='width=device-width, initial-scale=1'>
    <title>Pago Cancelado — TechStore360</title>
    <style>
        * {{ margin: 0; padding: 0; box-sizing: border-box; }}
        body {{ font-family: 'Segoe UI', sans-serif; background: #fdf2f2; display: flex; align-items: center; justify-content: center; min-height: 100vh; padding: 20px; }}
        .card {{ max-width: 420px; width: 100%; background: white; border-radius: 24px; overflow: hidden; box-shadow: 0 20px 60px rgba(0,0,0,0.10); }}
        .header {{ background: linear-gradient(135deg, #B71C1C, #C62828); padding: 36px 28px; text-align: center; }}
        .icon {{ width: 72px; height: 72px; background: rgba(255,255,255,0.2); border-radius: 50%; display: flex; align-items: center; justify-content: center; margin: 0 auto 16px; font-size: 36px; color: white; }}
        .header h1 {{ color: white; font-size: 22px; font-weight: 800; margin-bottom: 6px; }}
        .header p {{ color: rgba(255,255,255,0.8); font-size: 13px; }}
        .body {{ padding: 28px; }}
        .message {{ background: #FFF3F3; border: 1px solid #FFCDD2; border-radius: 12px; padding: 16px; font-size: 13px; color: #C62828; line-height: 1.6; text-align: center; margin-bottom: 16px; }}
        .sub {{ font-size: 12px; color: #9E9E9E; text-align: center; }}
        .footer {{ background: #f8f9fa; padding: 14px; text-align: center; font-size: 10px; color: #bbb; }}
    </style>
</head>
<body>
    <div class='card'>
        <div class='header'>
            <div class='icon'>✕</div>
            <h1>Pago Cancelado</h1>
        </div>
        <div class='body'>
            <div class='message'>Cancelaste el proceso de pago. No se realizó ningún cobro.</div>
            <p class='sub'>Puedes intentar el pago nuevamente desde la app en cualquier momento.</p>
        </div>
        <div class='footer'>TechStore360 &mdash; FISEI UTA &mdash; Aplicaciones Distribuidas</div>
    </div>
    <script>setTimeout(() => window.close(), 6000);</script>
</body>
</html>";
            return Content(html, "text/html", System.Text.Encoding.UTF8);
        }
    }

    public record ConfirmarPagoResponse(
        int NumeroFactura,
        string EstadoAnterior,
        string EstadoActual,
        string Mensaje,
        bool PuedeFacturar
    );

    public record EstadoPagoResponse(
        int NumeroFactura,
        string Estado,
        string MetodoPago,
        decimal TotalCompra,
        bool PendientePago,
        bool PuedeFacturar
    );
}
