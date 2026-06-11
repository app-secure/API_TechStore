using Microsoft.AspNetCore.Mvc;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using TechStore360.ExternalServices;
using TechStore360.Modules.Compras;
using TechStore360.Modules.Usuarios;

namespace TechStore360.Modules.Pagos
{
    [ApiController]
    [Route("api/[controller]")]
    public class PagosController : ControllerBase
    {
        private readonly IComprasService _comprasService;
        private readonly IUsuariosService _usuariosService;
        private readonly TechStore360.Modulos.Factura.IFacturacionService _facturacionService;

        public PagosController(
            IComprasService comprasService,
            IUsuariosService usuariosService,
            TechStore360.Modulos.Factura.IFacturacionService facturacionService,
            NotificationSmsService smsService) // mantenido en DI para compatibilidad, no se usa
        {
            _comprasService = comprasService;
            _usuariosService = usuariosService;
            _facturacionService = facturacionService;
        }

        // ──────────────────────────────────────────────────────────────────────
        // Confirmar pago manualmente (usado por PayPhone webhook interno)
        // ──────────────────────────────────────────────────────────────────────
        [HttpPost("{numeroFactura:int}/confirmar")]
        public async Task<IActionResult> ConfirmarPago(int numeroFactura, CancellationToken ct)
        {
            var compra = await _comprasService.ObtenerDetalleCompraAsync(numeroFactura, ct);
            if (compra == null)
                return NotFound(new { mensaje = $"Compra #{numeroFactura} no encontrada." });

            if (compra.Estado == "ABIERTA")
                return Ok(new ConfirmarPagoResponse(
                    numeroFactura,
                    "ABIERTA",
                    "ABIERTA",
                    $"La compra #{numeroFactura} ya estaba pagada.",
                    true));

            if (compra.Estado != "PENDIENTE_PAGO")
                return BadRequest(new
                {
                    mensaje = $"No se puede confirmar el pago. Estado actual: '{compra.Estado}'. Solo se puede confirmar en estado PENDIENTE_PAGO."
                });

            var actualizado = await _comprasService.UpdateStatusAsync(numeroFactura, "ABIERTA", ct);
            if (!actualizado)
                return StatusCode(500, new { mensaje = "No se pudo actualizar el estado de la compra." });

            try
            {
                await _facturacionService.EnviarFacturaPorEmailAsync(numeroFactura, ct);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Email] Error al enviar factura tras confirmación: {ex.Message}");
            }

            return Ok(new ConfirmarPagoResponse(
                numeroFactura,
                "PENDIENTE_PAGO",
                "ABIERTA",
                $"Pago confirmado. Compra #{numeroFactura} procesada correctamente.",
                true));
        }

        // ──────────────────────────────────────────────────────────────────────
        // Consultar estado de pago
        // ──────────────────────────────────────────────────────────────────────
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

        // ──────────────────────────────────────────────────────────────────────
        // PayPal Sandbox — Obtener Client ID de PayPal
        // ──────────────────────────────────────────────────────────────────────
        [HttpGet("paypal/client-id")]
        public IActionResult GetPaypalClientId()
        {
            string clientId = Environment.GetEnvironmentVariable("PAYPAL_CLIENT_ID")
                ?? throw new InvalidOperationException("PAYPAL_CLIENT_ID no configurado.");
            return Ok(new { clientId });
        }

        // ──────────────────────────────────────────────────────────────────────
        // PayPal Sandbox — Crear orden y obtener URL de aprobación real
        // ──────────────────────────────────────────────────────────────────────
        [HttpPost("paypal/crear/{numeroFactura:int}")]
        public async Task<IActionResult> CrearPagoPaypal(int numeroFactura, CancellationToken ct)
        {
            var compra = await _comprasService.ObtenerDetalleCompraAsync(numeroFactura, ct);
            if (compra == null)
                return NotFound(new { mensaje = $"Compra #{numeroFactura} no encontrada." });

            if (compra.Estado != "PENDIENTE_PAGO")
                return BadRequest(new { mensaje = $"La compra #{numeroFactura} no está en estado PENDIENTE_PAGO." });

            string clientId = Environment.GetEnvironmentVariable("PAYPAL_CLIENT_ID")
                ?? throw new InvalidOperationException("PAYPAL_CLIENT_ID no configurado.");
            string clientSecret = Environment.GetEnvironmentVariable("PAYPAL_CLIENT_SECRET")
                ?? throw new InvalidOperationException("PAYPAL_CLIENT_SECRET no configurado.");

            using var client = new HttpClient();

            // 1. Obtener Access Token
            var authBytes = System.Text.Encoding.UTF8.GetBytes($"{clientId}:{clientSecret}");
            var authHeader = Convert.ToBase64String(authBytes);

            var tokenRequest = new HttpRequestMessage(HttpMethod.Post, "https://api-m.sandbox.paypal.com/v1/oauth2/token");
            tokenRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", authHeader);
            tokenRequest.Content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("grant_type", "client_credentials")
            });

            var tokenResponse = await client.SendAsync(tokenRequest, ct);
            if (!tokenResponse.IsSuccessStatusCode)
            {
                string errBody = await tokenResponse.Content.ReadAsStringAsync(ct);
                Console.WriteLine($"[PayPal] Error obteniendo token: {errBody}");
                return StatusCode(502, new { mensaje = "No se pudo autenticar con PayPal Sandbox. Verifica las credenciales." });
            }

            var tokenData = await tokenResponse.Content.ReadFromJsonAsync<System.Text.Json.Nodes.JsonObject>(cancellationToken: ct);
            string accessToken = tokenData?["access_token"]?.ToString()
                ?? throw new Exception("PayPal no devolvió access_token");

            // 2. Crear la orden PayPal
            var returnBase = $"{Request.Scheme}://{Request.Host}";
            var orderBody = new
            {
                intent = "CAPTURE",
                purchase_units = new[]
                {
                    new
                    {
                        reference_id = numeroFactura.ToString(),
                        description = $"TechStore360 - Compra #{numeroFactura}",
                        amount = new
                        {
                            currency_code = "USD",
                            value = compra.TotalCompra.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)
                        }
                    }
                },
                application_context = new
                {
                    brand_name = "TechStore360",
                    locale = "es-EC",
                    landing_page = "LOGIN",
                    shipping_preference = "NO_SHIPPING",
                    user_action = "PAY_NOW",
                    return_url = $"{returnBase}/api/pagos/paypal/success?numeroFactura={numeroFactura}",
                    cancel_url = $"{returnBase}/api/pagos/paypal/cancel?numeroFactura={numeroFactura}"
                }
            };

            var orderRequest = new HttpRequestMessage(HttpMethod.Post, "https://api-m.sandbox.paypal.com/v2/checkout/orders");
            orderRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
            orderRequest.Content = JsonContent.Create(orderBody);

            var orderResponse = await client.SendAsync(orderRequest, ct);
            if (!orderResponse.IsSuccessStatusCode)
            {
                string errBody = await orderResponse.Content.ReadAsStringAsync(ct);
                Console.WriteLine($"[PayPal] Error creando orden: {errBody}");
                return StatusCode(502, new { mensaje = "No se pudo crear la orden en PayPal Sandbox." });
            }

            var orderData = await orderResponse.Content.ReadFromJsonAsync<System.Text.Json.Nodes.JsonObject>(cancellationToken: ct);
            var links = orderData?["links"]?.AsArray();
            string approvalUrl = "";

            if (links != null)
            {
                foreach (var link in links)
                {
                    if (link?["rel"]?.ToString() == "approve")
                    {
                        approvalUrl = link?["href"]?.ToString() ?? "";
                        break;
                    }
                }
            }

            if (string.IsNullOrEmpty(approvalUrl))
            {
                Console.WriteLine($"[PayPal] Respuesta sin approval URL: {orderData}");
                return StatusCode(502, new { mensaje = "PayPal no devolvió la URL de aprobación." });
            }

            var orderId = orderData?["id"]?.ToString() ?? "";
            Console.WriteLine($"[PayPal] Orden creada. OrderID: {orderId} ApprovalURL: {approvalUrl}");
            return Ok(new { approvalUrl, orderId, clientId });
        }

        // ──────────────────────────────────────────────────────────────────────
        // PayPal — Callback de éxito (redirect desde sandbox.paypal.com)
        // ──────────────────────────────────────────────────────────────────────
        [HttpGet("paypal/success")]
        public async Task<IActionResult> PaypalSuccess(
            [FromQuery] int numeroFactura,
            [FromQuery] string? token,
            [FromQuery] string? PayerID,
            CancellationToken ct)
        {
            Console.WriteLine($"[PayPal] PaypalSuccess invocado. Factura: #{numeroFactura}, Token: '{token}', PayerID: '{PayerID}'");

            var compra = await _comprasService.ObtenerDetalleCompraAsync(numeroFactura, ct);
            if (compra == null)
            {
                Console.WriteLine($"[PayPal] Error: Compra #{numeroFactura} no encontrada en base de datos.");
                return NotFound("Compra no encontrada");
            }

            Console.WriteLine($"[PayPal] Compra #{numeroFactura} encontrada. Estado actual en base de datos: '{compra.Estado}'");

            if (compra.Estado == "PENDIENTE_PAGO" && !string.IsNullOrEmpty(token))
            {
                // Capturar el pago en PayPal
                try
                {
                    string clientId = Environment.GetEnvironmentVariable("PAYPAL_CLIENT_ID") ?? "";
                    string clientSecret = Environment.GetEnvironmentVariable("PAYPAL_CLIENT_SECRET") ?? "";

                    if (!string.IsNullOrEmpty(clientId) && !string.IsNullOrEmpty(clientSecret))
                    {
                        using var client = new HttpClient();
                        var authBytes = System.Text.Encoding.UTF8.GetBytes($"{clientId}:{clientSecret}");
                        var tokenRequest = new HttpRequestMessage(HttpMethod.Post, "https://api-m.sandbox.paypal.com/v1/oauth2/token");
                        tokenRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", Convert.ToBase64String(authBytes));
                        tokenRequest.Content = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("grant_type", "client_credentials") });
                        var tokenResponse = await client.SendAsync(tokenRequest, ct);

                        if (tokenResponse.IsSuccessStatusCode)
                        {
                            var tokenData = await tokenResponse.Content.ReadFromJsonAsync<System.Text.Json.Nodes.JsonObject>(cancellationToken: ct);
                            string accessToken = tokenData?["access_token"]?.ToString() ?? "";

                            var captureRequest = new HttpRequestMessage(HttpMethod.Post, $"https://api-m.sandbox.paypal.com/v2/checkout/orders/{token}/capture");
                            captureRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
                            captureRequest.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
                            var captureResponse = await client.SendAsync(captureRequest, ct);
                            string captureBody = await captureResponse.Content.ReadAsStringAsync(ct);
                            Console.WriteLine($"[PayPal] Capture resultado: {captureResponse.StatusCode} - {captureBody}");
                        }
                        else
                        {
                            string tokenErr = await tokenResponse.Content.ReadAsStringAsync(ct);
                            Console.WriteLine($"[PayPal] Error al obtener Token Sandbox: {tokenResponse.StatusCode} - {tokenErr}");
                        }
                    }
                    else
                    {
                        Console.WriteLine("[PayPal] Error: Credenciales PAYPAL_CLIENT_ID o PAYPAL_CLIENT_SECRET no configuradas en el entorno.");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[PayPal] Error al capturar pago: {ex.Message}");
                }

                var actualizado = await _comprasService.UpdateStatusAsync(numeroFactura, "ABIERTA", ct);
                Console.WriteLine($"[PayPal] Actualización de estado a ABIERTA: {(actualizado ? "EXITOSO" : "FALLIDO")}");

                if (actualizado)
                {
                    try 
                    { 
                        Console.WriteLine($"[PayPal] Iniciando envío de factura por email para Compra #{numeroFactura}...");
                        await _facturacionService.EnviarFacturaPorEmailAsync(numeroFactura, ct); 
                        Console.WriteLine($"[PayPal] Factura enviada correctamente por correo.");
                    }
                    catch (Exception ex) 
                    { 
                        Console.WriteLine($"[Email] Error al enviar factura tras PayPal: {ex.Message}"); 
                    }
                }
            }
            else
            {
                Console.WriteLine($"[PayPal] Captura omitida. El estado de la compra es '{compra.Estado}' (se requiere 'PENDIENTE_PAGO') o el token está vacío.");
            }

            string html = $@"<!DOCTYPE html>
<html lang='es'>
<head>
    <meta charset='utf-8'>
    <meta name='viewport' content='width=device-width, initial-scale=1'>
    <title>Pago Confirmado — TechStore360</title>
    <style>
        * {{ margin: 0; padding: 0; box-sizing: border-box; }}
        body {{ font-family: 'Segoe UI', sans-serif; background: #f0f7f4; display: flex; align-items: center; justify-content: center; min-height: 100vh; padding: 20px; }}
        .card {{ max-width: 420px; width: 100%; background: white; border-radius: 24px; overflow: hidden; box-shadow: 0 20px 60px rgba(0,0,0,0.10); }}
        .header {{ background: linear-gradient(135deg, #1B5E20, #2E7D32); padding: 36px 28px; text-align: center; }}
        .check {{ width: 72px; height: 72px; background: rgba(255,255,255,0.2); border-radius: 50%; display: flex; align-items: center; justify-content: center; margin: 0 auto 16px; }}
        .check-icon {{ font-size: 36px; }}
        .header h1 {{ color: white; font-size: 22px; font-weight: 800; margin-bottom: 6px; }}
        .header p {{ color: rgba(255,255,255,0.8); font-size: 13px; }}
        .body {{ padding: 28px; }}
        .info-box {{ background: #f8fffe; border: 1px solid #C8E6C9; border-radius: 12px; padding: 18px; margin-bottom: 20px; }}
        .info-row {{ display: flex; justify-content: space-between; padding: 6px 0; font-size: 14px; }}
        .info-row .label {{ color: #78909C; }}
        .info-row .value {{ font-weight: 700; color: #1A2F40; }}
        .info-row .amount {{ color: #2E7D32; font-size: 18px; font-weight: 800; }}
        .message {{ background: #E8F5E9; border-radius: 10px; padding: 14px 16px; font-size: 13px; color: #2E7D32; line-height: 1.6; margin-bottom: 20px; text-align: center; }}
        .paypal-badge {{ display: flex; align-items: center; justify-content: center; gap: 6px; color: #9E9E9E; font-size: 11px; }}
        .paypal-text {{ font-style: italic; font-weight: 900; font-size: 14px; }}
        .paypal-text .pay {{ color: #003087; }}
        .paypal-text .pal {{ color: #009CDE; }}
        .footer {{ background: #f8f9fa; padding: 14px; text-align: center; font-size: 10px; color: #bbb; }}
    </style>
</head>
<body>
    <div class='card'>
        <div class='header'>
            <div class='check'><span class='check-icon'>✓</span></div>
            <h1>¡Pago Confirmado!</h1>
            <p>Tu compra ha sido procesada exitosamente</p>
        </div>
        <div class='body'>
            <div class='info-box'>
                <div class='info-row'>
                    <span class='label'># Compra</span>
                    <span class='value'>#{numeroFactura}</span>
                </div>
                <div class='info-row'>
                    <span class='label'>Estado</span>
                    <span class='value' style='color:#2E7D32'>✓ PAGADO</span>
                </div>
            </div>
            <div class='message'>
                La factura ha sido enviada automáticamente a tu correo electrónico registrado. Puedes descargarla desde la app en <strong>Mis Compras → Ver Factura</strong>.
            </div>
            <div class='paypal-badge'>
                <span>Pago procesado por</span>
                <span class='paypal-text'><span class='pay'>Pay</span><span class='pal'>Pal</span></span>
                <span>Sandbox</span>
            </div>
        </div>
        <div class='footer'>TechStore360 &mdash; FISEI UTA &mdash; Aplicaciones Distribuidas</div>
    </div>
    <script>setTimeout(() => window.close(), 8000);</script>
</body>
</html>";
            return Content(html, "text/html", System.Text.Encoding.UTF8);
        }

        // ──────────────────────────────────────────────────────────────────────
        // PayPal — Callback de cancelación
        // ──────────────────────────────────────────────────────────────────────
        [HttpGet("paypal/cancel")]
        public async Task<IActionResult> PaypalCancel([FromQuery] int numeroFactura, CancellationToken ct)
        {
            await _comprasService.AnularCompraAsync(numeroFactura, ct);

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
        .icon {{ width: 72px; height: 72px; background: rgba(255,255,255,0.2); border-radius: 50%; display: flex; align-items: center; justify-content: center; margin: 0 auto 16px; font-size: 36px; }}
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
            <p>Compra #{numeroFactura}</p>
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
