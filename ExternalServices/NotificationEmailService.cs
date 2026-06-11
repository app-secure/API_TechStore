using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;
using System.Linq;

namespace TechStore360.ExternalServices
{
    public class NotificationEmailService
    {
        private readonly IConfiguration _configuration;
        private readonly HttpClient _httpClient;

        public NotificationEmailService(IConfiguration configuration, HttpClient httpClient)
        {
            _configuration = configuration;
            _httpClient = httpClient;
        }

        public async Task<bool> EnviarCorreoConFacturaXmlAsync(
            string emailDestino,
            string nombreDestino,
            int templateId,
            Dictionary<string, string> parametros,
            string contenidoXmlFactura = "",
            string contenidoPdfBase64 = "")
        {
            var apiKey = _configuration["ExternalServices:Brevo:ApiKey"];
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                apiKey = Environment.GetEnvironmentVariable("ExternalServices__Brevo__ApiKey");
            }

            var apiUrl = _configuration["ExternalServices:Brevo:ApiUrl"];
            if (string.IsNullOrWhiteSpace(apiUrl))
            {
                apiUrl = Environment.GetEnvironmentVariable("ExternalServices__Brevo__ApiUrl") ?? "https://api.brevo.com/v3/smtp/email";
            }

            var payload = new Dictionary<string, object>
            {
                { "to", new[] { new { email = emailDestino, name = nombreDestino } } },
                { "templateId", templateId },
                { "params", parametros }
            };

            var adjuntos = new List<object>();
            string idCompra = parametros.GetValueOrDefault("ID_COMPRA", "001");

            if (!string.IsNullOrWhiteSpace(contenidoXmlFactura))
            {
                byte[] xmlBytes = Encoding.UTF8.GetBytes(contenidoXmlFactura);
                string xmlBase64Standard = Convert.ToBase64String(xmlBytes);

                adjuntos.Add(new
                {
                    content = xmlBase64Standard,
                    name = $"Factura_Electronica_{idCompra}.xml"
                });
            }

            if (!string.IsNullOrWhiteSpace(contenidoPdfBase64))
            {
                adjuntos.Add(new
                {
                    content = contenidoPdfBase64,
                    name = $"Factura_Digital_{idCompra}.pdf"
                });
            }

            if (adjuntos.Count > 0)
            {
                payload.Add("attachment", adjuntos.ToArray());
            }

            var requestMessage = new HttpRequestMessage(HttpMethod.Post, apiUrl);
            requestMessage.Headers.Add("api-key", apiKey);
            requestMessage.Content = new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json");

            var response = await _httpClient.SendAsync(requestMessage);
            return response.IsSuccessStatusCode;
        }

        public async Task<bool> EnviarEmailDirectoAsync(
            string emailDestino,
            string nombreDestino,
            string asunto,
            string htmlBody,
            List<(string Nombre, string ContentBase64, string MimeType)>? adjuntos = null)
        {
            var apiKey = _configuration["ExternalServices:Brevo:ApiKey"];
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                apiKey = Environment.GetEnvironmentVariable("ExternalServices__Brevo__ApiKey");
            }

            if (string.IsNullOrWhiteSpace(apiKey))
                throw new InvalidOperationException("Clave API de Brevo no configurada.");

            var senderEmail = _configuration["ExternalServices:Brevo:SenderEmail"];
            if (string.IsNullOrWhiteSpace(senderEmail))
            {
                senderEmail = Environment.GetEnvironmentVariable("ExternalServices__Brevo__SenderEmail")
                    ?? Environment.GetEnvironmentVariable("BREVO_SENDER_EMAIL")
                    ?? Environment.GetEnvironmentVariable("SMTP_USER")
                    ?? "noreply@techstore360.com";
            }

            var senderName = _configuration["ExternalServices:Brevo:SenderName"];
            if (string.IsNullOrWhiteSpace(senderName))
            {
                senderName = Environment.GetEnvironmentVariable("ExternalServices__Brevo__SenderName")
                    ?? "TechStore360";
            }

            var payload = new Dictionary<string, object>
            {
                { "sender", new { name = senderName, email = senderEmail } },
                { "to", new[] { new { email = emailDestino, name = nombreDestino } } },
                { "subject", asunto },
                { "htmlContent", htmlBody }
            };

            if (adjuntos != null && adjuntos.Count > 0)
            {
                payload["attachment"] = adjuntos.Select(a => new
                {
                    content = a.ContentBase64,
                    name = a.Nombre
                }).ToArray();
            }

            var requestMessage = new HttpRequestMessage(HttpMethod.Post, "https://api.brevo.com/v3/smtp/email");
            requestMessage.Headers.Add("api-key", apiKey);
            requestMessage.Content = new StringContent(
                JsonConvert.SerializeObject(payload),
                Encoding.UTF8,
                "application/json");

            var response = await _httpClient.SendAsync(requestMessage);
            if (!response.IsSuccessStatusCode)
            {
                string body = await response.Content.ReadAsStringAsync();
                Console.WriteLine($"[Brevo] Error HTTP {(int)response.StatusCode}: {body}");
            }
            return response.IsSuccessStatusCode;
        }
    }
}