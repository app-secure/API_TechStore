using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Confluent.Kafka;
using Microsoft.Extensions.Configuration;

namespace TechStore360.Core.Messaging
{
    public interface IKafkaProducer
    {
        Task PublishAsync<T>(string topic, string key, T message, CancellationToken ct = default);
    }

    public class KafkaProducer : IKafkaProducer, IDisposable
    {
        private readonly IProducer<string, string> _producer;

        public KafkaProducer(IConfiguration configuration)
        {
            var bootstrapServers = configuration["Kafka:BootstrapServers"];
            if (string.IsNullOrWhiteSpace(bootstrapServers))
            {
                bootstrapServers = Environment.GetEnvironmentVariable("KAFKA_BOOTSTRAP_SERVERS") ?? "localhost:9092";
            }

            var config = new ProducerConfig
            {
                BootstrapServers = bootstrapServers,
                Acks = Acks.All,
                MessageSendMaxRetries = 3,
                RetryBackoffMs = 500
            };

            var saslUsername = Environment.GetEnvironmentVariable("KAFKA_SASL_USERNAME") ?? configuration["Kafka:SaslUsername"];
            var saslPassword = Environment.GetEnvironmentVariable("KAFKA_SASL_PASSWORD") ?? configuration["Kafka:SaslPassword"];
            var securityProtocolStr = Environment.GetEnvironmentVariable("KAFKA_SECURITY_PROTOCOL") ?? configuration["Kafka:SecurityProtocol"];
            var saslMechanismStr = Environment.GetEnvironmentVariable("KAFKA_SASL_MECHANISM") ?? configuration["Kafka:SaslMechanism"];

            if (!string.IsNullOrWhiteSpace(saslUsername))
            {
                config.SaslUsername = saslUsername;
                config.SaslPassword = saslPassword;
                config.EnableSslCertificateVerification = false;

                if (Enum.TryParse<SecurityProtocol>(securityProtocolStr, true, out var securityProtocol))
                {
                    config.SecurityProtocol = securityProtocol;
                }
                else
                {
                    config.SecurityProtocol = SecurityProtocol.SaslSsl;
                }

                if (Enum.TryParse<SaslMechanism>(saslMechanismStr, true, out var saslMechanism))
                {
                    config.SaslMechanism = saslMechanism;
                }
                else
                {
                    config.SaslMechanism = SaslMechanism.ScramSha256;
                }
            }

            _producer = new ProducerBuilder<string, string>(config).Build();
        }

        public async Task PublishAsync<T>(string topic, string key, T message, CancellationToken ct = default)
        {
            try
            {
                var valJson = JsonSerializer.Serialize(message);
                var kafkaMessage = new Message<string, string>
                {
                    Key = key,
                    Value = valJson
                };

                var deliveryResult = await _producer.ProduceAsync(topic, kafkaMessage, ct);
                Console.WriteLine($"[Kafka Producer] Mensaje enviado a {topic} (Partición: {deliveryResult.Partition}, Offset: {deliveryResult.Offset})");
            }
            catch (ProduceException<string, string> ex)
            {
                Console.WriteLine($"[Kafka Producer Error] Error al publicar en {topic}: {ex.Error.Reason}");
                throw;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Kafka Producer Error] Error general al publicar en {topic}: {ex.Message}");
                throw;
            }
        }

        public void Dispose()
        {
            try
            {
                _producer.Flush(TimeSpan.FromSeconds(5));
                _producer.Dispose();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Kafka Producer Dispose Error] {ex.Message}");
            }
        }
    }
}
