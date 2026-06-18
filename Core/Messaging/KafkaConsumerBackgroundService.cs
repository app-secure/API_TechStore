using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Confluent.Kafka;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TechStore360.Core.Events;
using TechStore360.Modulos.Factura;

namespace TechStore360.Core.Messaging
{
    public class KafkaConsumerBackgroundService : BackgroundService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly string _bootstrapServers;
        private const string TopicName = "facturas-pendientes";
        private const string GroupId = "techstore-billing-group";

        public KafkaConsumerBackgroundService(IServiceProvider serviceProvider, IConfiguration configuration)
        {
            _serviceProvider = serviceProvider;
            
            var servers = configuration["Kafka:BootstrapServers"];
            if (string.IsNullOrWhiteSpace(servers))
            {
                servers = Environment.GetEnvironmentVariable("KAFKA_BOOTSTRAP_SERVERS") ?? "localhost:9092";
            }
            _bootstrapServers = servers;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await Task.Yield();

            var config = new ConsumerConfig
            {
                BootstrapServers = _bootstrapServers,
                GroupId = GroupId,
                AutoOffsetReset = AutoOffsetReset.Earliest,
                EnableAutoCommit = false,
                SessionTimeoutMs = 6000,
                MaxPollIntervalMs = 300000
            };

            using var consumer = new ConsumerBuilder<string, string>(config).Build();
            consumer.Subscribe(TopicName);
            Console.WriteLine($"[Kafka Consumer] Iniciado. Escuchando en el tópico '{TopicName}'...");

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var consumeResult = consumer.Consume(stoppingToken);
                    if (consumeResult == null) continue;

                    Console.WriteLine($"[Kafka Consumer] Mensaje recibido de {consumeResult.Topic}. Procesando...");

                    PagoConfirmadoEvent? pagoEvent = null;
                    try
                    {
                        pagoEvent = JsonSerializer.Deserialize<PagoConfirmadoEvent>(consumeResult.Message.Value);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[Kafka Consumer Error] Error deserializando mensaje: {ex.Message}. Contenido: {consumeResult.Message.Value}");
                        consumer.Commit(consumeResult);
                        continue;
                    }

                    if (pagoEvent != null)
                    {
                        bool procesadoExitosamente = await ProcesarFacturaConRetriesAsync(pagoEvent.NumeroFactura, stoppingToken);

                        if (procesadoExitosamente)
                        {
                            consumer.Commit(consumeResult);
                            Console.WriteLine($"[Kafka Consumer] Confirmación de factura procesada para compra #{pagoEvent.NumeroFactura}. Offset confirmado.");
                        }
                        else
                        {
                            Console.WriteLine($"[Kafka Consumer Warning] No se pudo procesar la factura para compra #{pagoEvent.NumeroFactura}. Se reintentará en el próximo ciclo.");
                            await Task.Delay(5000, stoppingToken);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Kafka Consumer Error] Error en el ciclo de consumo: {ex.Message}");
                    await Task.Delay(5000, stoppingToken);
                }
            }

            consumer.Close();
        }

        private async Task<bool> ProcesarFacturaConRetriesAsync(int numeroFactura, CancellationToken stoppingToken)
        {
            int maxAttempts = 3;
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    using var scope = _serviceProvider.CreateScope();
                    var facturacionService = scope.ServiceProvider.GetRequiredService<IFacturacionService>();

                    Console.WriteLine($"[Facturacion Asincrona] Intento {attempt}/{maxAttempts} para generar y enviar factura de Compra #{numeroFactura}");
                    await facturacionService.EnviarFacturaPorEmailAsync(numeroFactura, stoppingToken);
                    
                    return true;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Facturacion Asincrona Error] Intento {attempt} fallido: {ex.Message}");
                    if (attempt < maxAttempts)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(2 * attempt), stoppingToken);
                    }
                }
            }
            return false;
        }
    }
}
