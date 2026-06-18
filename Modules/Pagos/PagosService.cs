using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Npgsql;
using TechStore360.Data;
using TechStore360.Core.Events;
using TechStore360.Core.Messaging;
using TechStore360.Modules.Compras;
using TechStore360.Modules.Productos;
using TechStore360.Modules.Usuarios;

namespace TechStore360.Modules.Pagos
{
    public interface IPagosService
    {
        Task<ConfirmarPagoResult> ConfirmarPagoManualAsync(int numeroFactura, CancellationToken ct);
        
        Task<CrearPagoPaypalResult> CrearPagoPaypalSinOrdenAsync(CrearCompraRequest request, string requestScheme, string requestHost, CancellationToken ct);
        
        Task<CrearPagoPaypalResult> CrearPagoPaypalConOrdenAsync(int numeroFactura, string requestScheme, string requestHost, CancellationToken ct);
        
        Task<PaypalSuccessResult> ProcesarExitoPaypalAsync(string token, string? payerId, int? numeroFactura, CancellationToken ct);
        
        string GetPaypalClientId();
    }

    public record ConfirmarPagoResult(
        bool Success,
        string? ErrorMessage,
        int NumeroFactura = 0,
        string EstadoAnterior = "",
        string EstadoActual = "",
        string Mensaje = ""
    );

    public record CrearPagoPaypalResult(
        bool Success,
        string? ErrorMessage,
        string ApprovalUrl = "",
        string OrderId = "",
        string ClientId = "",
        bool StockConflict = false
    );

    public record PaypalSuccessResult(
        bool Success,
        string? ErrorMessage,
        int NumeroFactura = 0,
        decimal Total = 0,
        string Mensaje = "",
        bool StockConflict = false,
        string? RawHtml = null
    );

    public class PagosService : IPagosService
    {
        private readonly IComprasService _comprasService;
        private readonly IUsuariosService _usuariosService;
        private readonly IProductosService _productosService;
        private readonly ResilientDbExecutor _dbExecutor;
        private readonly IKafkaProducer _kafkaProducer;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IConfiguration _configuration;

        private const string KafkaTopic = "facturas-pendientes";

        public PagosService(
            IComprasService comprasService,
            IUsuariosService usuariosService,
            IProductosService productosService,
            ResilientDbExecutor dbExecutor,
            IKafkaProducer kafkaProducer,
            IHttpClientFactory httpClientFactory,
            IConfiguration configuration)
        {
            _comprasService = comprasService;
            _usuariosService = usuariosService;
            _productosService = productosService;
            _dbExecutor = dbExecutor;
            _kafkaProducer = kafkaProducer;
            _httpClientFactory = httpClientFactory;
            _configuration = configuration;
        }

        public string GetPaypalClientId()
        {
            return Environment.GetEnvironmentVariable("PAYPAL_CLIENT_ID")
                ?? _configuration["PayPal:ClientId"]
                ?? throw new InvalidOperationException("PAYPAL_CLIENT_ID no configurado.");
        }

        private string GetPaypalClientSecret()
        {
            return Environment.GetEnvironmentVariable("PAYPAL_CLIENT_SECRET")
                ?? _configuration["PayPal:ClientSecret"]
                ?? throw new InvalidOperationException("PAYPAL_CLIENT_SECRET no configurado.");
        }

        public async Task<ConfirmarPagoResult> ConfirmarPagoManualAsync(int numeroFactura, CancellationToken ct)
        {
            var compra = await _comprasService.ObtenerDetalleCompraAsync(numeroFactura, ct);
            if (compra == null)
            {
                return new ConfirmarPagoResult(false, $"Compra #{numeroFactura} no encontrada.");
            }

            if (compra.Estado == "ABIERTA")
            {
                return new ConfirmarPagoResult(true, null, numeroFactura, "ABIERTA", "ABIERTA", $"La compra #{numeroFactura} ya estaba pagada.");
            }

            if (compra.Estado != "PENDIENTE_PAGO")
            {
                return new ConfirmarPagoResult(false, $"No se puede confirmar el pago. Estado actual: '{compra.Estado}'. Solo se puede confirmar en estado PENDIENTE_PAGO.");
            }

            var actualizado = await _comprasService.UpdateStatusAsync(numeroFactura, "ABIERTA", ct);
            if (!actualizado)
            {
                return new ConfirmarPagoResult(false, "No se pudo actualizar el estado de la compra en la base de datos.");
            }

            try
            {
                var pagoEvent = new PagoConfirmadoEvent(numeroFactura, DateTime.UtcNow);
                await _kafkaProducer.PublishAsync(KafkaTopic, numeroFactura.ToString(), pagoEvent, ct);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Kafka Error] No se pudo publicar confirmación para compra #{numeroFactura}: {ex.Message}");
            }

            return new ConfirmarPagoResult(true, null, numeroFactura, "PENDIENTE_PAGO", "ABIERTA", $"Pago confirmado. Compra #{numeroFactura} procesada correctamente.");
        }

        public async Task<CrearPagoPaypalResult> CrearPagoPaypalSinOrdenAsync(CrearCompraRequest request, string requestScheme, string requestHost, CancellationToken ct)
        {
            var detallesAgrupados = request.Detalles
                .GroupBy(d => d.IdProducto)
                .Select(g => new { IdProducto = g.Key, Cantidad = g.Sum(x => x.Cantidad) });

            var ids = detallesAgrupados.Select(d => d.IdProducto).ToList();
            var productos = await _productosService.GetByIdsAsync(ids, ct);

            if (productos.Count != ids.Count)
            {
                var existentes = productos.Select(p => p.IdProducto).ToList();
                var faltantes = ids.Except(existentes).ToArray();
                return new CrearPagoPaypalResult(false, $"Productos no existentes: {string.Join(", ", faltantes)}");
            }

            decimal totalCompra = 0m;
            foreach (var detalle in detallesAgrupados)
            {
                var prod = productos.First(p => p.IdProducto == detalle.IdProducto);
                if (prod.Stock < detalle.Cantidad)
                {
                    return new CrearPagoPaypalResult(false, $"Stock insuficiente para el producto {prod.Nombre} (ID: {detalle.IdProducto}).", StockConflict: true);
                }

                totalCompra += prod.Precio * detalle.Cantidad;
            }
            totalCompra = totalCompra * 1.15m;

            string accessToken;
            try
            {
                accessToken = await GetPaypalAccessTokenAsync(ct);
            }
            catch (Exception ex)
            {
                return new CrearPagoPaypalResult(false, "No se pudo autenticar con PayPal Sandbox: " + ex.Message);
            }

            var returnBase = $"{requestScheme}://{requestHost}";
            var orderBody = new
            {
                intent = "CAPTURE",
                purchase_units = new[]
                {
                    new
                    {
                        reference_id = Guid.NewGuid().ToString(),
                        description = "TechStore360 - Compra",
                        amount = new
                        {
                            currency_code = "USD",
                            value = totalCompra.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)
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
                    return_url = $"{returnBase}/api/pagos/paypal/success",
                    cancel_url = $"{returnBase}/api/pagos/paypal/cancel"
                }
            };

            using var client = _httpClientFactory.CreateClient();
            var orderRequest = new HttpRequestMessage(HttpMethod.Post, "https://api-m.sandbox.paypal.com/v2/checkout/orders");
            orderRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
            orderRequest.Content = JsonContent.Create(orderBody);

            var orderResponse = await client.SendAsync(orderRequest, ct);
            if (!orderResponse.IsSuccessStatusCode)
            {
                string errBody = await orderResponse.Content.ReadAsStringAsync(ct);
                Console.WriteLine($"[PayPal] Error creando orden: {errBody}");
                return new CrearPagoPaypalResult(false, "No se pudo crear la orden en PayPal Sandbox.");
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
                return new CrearPagoPaypalResult(false, "PayPal no devolvió la URL de aprobación.");
            }

            var orderId = orderData?["id"]?.ToString() ?? "";

            try
            {
                using var conn = await _dbExecutor.GetPostgresConnectionAsync();
                const string sql = "INSERT INTO public.pedidos_temporales (id, datos) VALUES ($1, $2);";
                using var cmdInsert = new NpgsqlCommand(sql, conn);
                cmdInsert.Parameters.AddWithValue(orderId);
                cmdInsert.Parameters.AddWithValue(JsonSerializer.Serialize(request));
                await cmdInsert.ExecuteNonQueryAsync(ct);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DB] Error al guardar pedido temporal: {ex.Message}");
                return new CrearPagoPaypalResult(false, "No se pudo registrar el pedido temporal en la base de datos.");
            }

            return new CrearPagoPaypalResult(true, null, approvalUrl, orderId, GetPaypalClientId());
        }

        public async Task<CrearPagoPaypalResult> CrearPagoPaypalConOrdenAsync(int numeroFactura, string requestScheme, string requestHost, CancellationToken ct)
        {
            var compra = await _comprasService.ObtenerDetalleCompraAsync(numeroFactura, ct);
            if (compra == null)
            {
                return new CrearPagoPaypalResult(false, $"Compra #{numeroFactura} no encontrada.");
            }

            if (compra.Estado != "PENDIENTE_PAGO")
            {
                return new CrearPagoPaypalResult(false, $"La compra #{numeroFactura} no está pendiente de pago. Estado actual: '{compra.Estado}'.");
            }

            decimal totalCompra = compra.TotalCompra;

            string accessToken;
            try
            {
                accessToken = await GetPaypalAccessTokenAsync(ct);
            }
            catch (Exception ex)
            {
                return new CrearPagoPaypalResult(false, "No se pudo autenticar con PayPal Sandbox: " + ex.Message);
            }

            var returnBase = $"{requestScheme}://{requestHost}";
            var orderBody = new
            {
                intent = "CAPTURE",
                purchase_units = new[]
                {
                    new
                    {
                        reference_id = Guid.NewGuid().ToString(),
                        description = $"TechStore360 - Compra #{numeroFactura}",
                        amount = new
                        {
                            currency_code = "USD",
                            value = totalCompra.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)
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

            using var client = _httpClientFactory.CreateClient();
            var orderRequest = new HttpRequestMessage(HttpMethod.Post, "https://api-m.sandbox.paypal.com/v2/checkout/orders");
            orderRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
            orderRequest.Content = JsonContent.Create(orderBody);

            var orderResponse = await client.SendAsync(orderRequest, ct);
            if (!orderResponse.IsSuccessStatusCode)
            {
                string errBody = await orderResponse.Content.ReadAsStringAsync(ct);
                Console.WriteLine($"[PayPal] Error creando orden para factura #{numeroFactura}: {errBody}");
                return new CrearPagoPaypalResult(false, "No se pudo crear la orden en PayPal Sandbox.");
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
                return new CrearPagoPaypalResult(false, "PayPal no devolvió la URL de aprobación.");
            }

            var orderId = orderData?["id"]?.ToString() ?? "";

            return new CrearPagoPaypalResult(true, null, approvalUrl, orderId, GetPaypalClientId());
        }

        public async Task<PaypalSuccessResult> ProcesarExitoPaypalAsync(string token, string? payerId, int? numeroFactura, CancellationToken ct)
        {
            string tempJson = "";
            bool esNuevoPedido = false;
            try
            {
                using var conn = await _dbExecutor.GetPostgresConnectionAsync();
                const string sqlSelect = "SELECT datos FROM public.pedidos_temporales WHERE id = $1;";
                using var cmdSelect = new NpgsqlCommand(sqlSelect, conn);
                cmdSelect.Parameters.AddWithValue(token);
                var result = await cmdSelect.ExecuteScalarAsync(ct);
                if (result != null)
                {
                    tempJson = result.ToString()!;
                    esNuevoPedido = true;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DB] Error al recuperar pedido temporal: {ex.Message}");
            }

            int numFacturaId = 0;
            CompraCreada? compraNueva = null;
            CompraCompletaDto? compraExistente = null;

            if (esNuevoPedido)
            {
                try
                {
                    using var conn = await _dbExecutor.GetPostgresConnectionAsync();
                    const string sqlDelete = "DELETE FROM public.pedidos_temporales WHERE id = $1;";
                    using var cmdDelete = new NpgsqlCommand(sqlDelete, conn);
                    cmdDelete.Parameters.AddWithValue(token);
                    await cmdDelete.ExecuteNonQueryAsync(ct);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[DB] Error al eliminar pedido temporal: {ex.Message}");
                }

                CrearCompraRequest? request = null;
                try
                {
                    request = JsonSerializer.Deserialize<CrearCompraRequest>(tempJson);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[JSON] Error deserializando pedido temporal: {ex.Message}");
                }

                if (request == null)
                {
                    return new PaypalSuccessResult(false, "No se pudieron deserializar los datos del pedido temporal.");
                }

                try
                {
                    compraNueva = await _comprasService.RegistrarCompraAsync(request, ct);
                }
                catch (InvalidOperationException ex)
                {
                    Console.WriteLine($"[Stock] Error de stock al registrar compra: {ex.Message}");
                    return new PaypalSuccessResult(false, ex.Message, StockConflict: true, RawHtml: GetErrorHtml("Sin stock disponible", ex.Message));
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Registro] Error al registrar compra: {ex.Message}");
                    return new PaypalSuccessResult(false, "Error al registrar la compra: " + ex.Message);
                }

                numFacturaId = compraNueva.NumeroFactura;
            }
            else
            {
                if (!numeroFactura.HasValue || numeroFactura.Value <= 0)
                {
                    return new PaypalSuccessResult(false, "Pedido temporal no encontrado y no se proporcionó un número de factura válido.");
                }

                numFacturaId = numeroFactura.Value;

                compraExistente = await _comprasService.ObtenerDetalleCompraAsync(numFacturaId, ct);
                if (compraExistente == null)
                {
                    return new PaypalSuccessResult(false, $"Compra #{numFacturaId} no encontrada.");
                }

                if (compraExistente.Estado == "ABIERTA")
                {
                    return new PaypalSuccessResult(true, null, numFacturaId, compraExistente.TotalCompra, "La compra ya estaba pagada.", RawHtml: GetSuccessHtml(numFacturaId));
                }
            }

            bool captureExitoso = false;

            try
            {
                string accessToken = await GetPaypalAccessTokenAsync(ct);
                using var client = _httpClientFactory.CreateClient();
                var captureRequest = new HttpRequestMessage(HttpMethod.Post, $"https://api-m.sandbox.paypal.com/v2/checkout/orders/{token}/capture");
                captureRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
                captureRequest.Content = new StringContent("{}", Encoding.UTF8, "application/json");

                var captureResponse = await client.SendAsync(captureRequest, ct);
                string captureBody = await captureResponse.Content.ReadAsStringAsync(ct);
                Console.WriteLine($"[PayPal] Capture resultado: {captureResponse.StatusCode} - {captureBody}");

                if (captureResponse.IsSuccessStatusCode)
                {
                    captureExitoso = true;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PayPal] Error al capturar pago: {ex.Message}");
            }

            if (captureExitoso)
            {
                var actualizado = await _comprasService.UpdateStatusAsync(numFacturaId, "ABIERTA", ct);
                Console.WriteLine($"[PayPal] Actualización de estado a ABIERTA: {(actualizado ? "EXITOSO" : "FALLIDO")}");

                if (actualizado)
                {
                    try
                    {
                        var pagoEvent = new PagoConfirmadoEvent(numFacturaId, DateTime.UtcNow);
                        await _kafkaProducer.PublishAsync(KafkaTopic, numFacturaId.ToString(), pagoEvent, ct);
                        Console.WriteLine($"[Kafka] Publicado PagoConfirmadoEvent para Compra #{numFacturaId}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[Kafka Error] No se pudo publicar confirmación para compra #{numFacturaId}: {ex.Message}");
                    }
                }

                decimal total = esNuevoPedido ? compraNueva!.TotalCompra : compraExistente!.TotalCompra;
                return new PaypalSuccessResult(true, null, numFacturaId, total, "Pago confirmado.", RawHtml: GetSuccessHtml(numFacturaId));
            }
            else
            {
                if (esNuevoPedido)
                {
                    Console.WriteLine($"[PayPal] Error de captura. Anulando compra #{numFacturaId} para retornar stock...");
                    await _comprasService.AnularCompraAsync(numFacturaId, ct);
                }

                return new PaypalSuccessResult(false, "No se pudo realizar la captura de fondos con PayPal.", RawHtml: GetErrorHtml("Pago no procesado", "No pudimos capturar tus fondos desde PayPal. Por favor, intenta de nuevo."));
            }
        }

        private async Task<string> GetPaypalAccessTokenAsync(CancellationToken ct)
        {
            string clientId = GetPaypalClientId();
            string clientSecret = GetPaypalClientSecret();

            using var client = _httpClientFactory.CreateClient();
            var authBytes = Encoding.UTF8.GetBytes($"{clientId}:{clientSecret}");
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
                throw new Exception($"Error obteniendo token de PayPal: {errBody}");
            }

            var tokenData = await tokenResponse.Content.ReadFromJsonAsync<System.Text.Json.Nodes.JsonObject>(cancellationToken: ct);
            return tokenData?["access_token"]?.ToString() ?? throw new Exception("PayPal no devolvió access_token");
        }

        private string GetSuccessHtml(int numeroFactura)
        {
            return $@"<!DOCTYPE html>
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
        }

        private string GetErrorHtml(string titulo, string mensaje)
        {
            return $@"<!DOCTYPE html>
<html lang='es'>
<head>
    <meta charset='utf-8'>
    <meta name='viewport' content='width=device-width, initial-scale=1'>
    <title>{titulo} — TechStore360</title>
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
            <h1>{titulo}</h1>
        </div>
        <div class='body'>
            <div class='message'>{mensaje}</div>
            <p class='sub'>Puedes intentar la compra nuevamente desde la app.</p>
        </div>
        <div class='footer'>TechStore360 &mdash; FISEI UTA &mdash; Aplicaciones Distribuidas</div>
    </div>
    <script>setTimeout(() => window.close(), 6000);</script>
</body>
</html>";
        }
    }
}
