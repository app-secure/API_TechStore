namespace TechStore360.ExternalServices
{
    /// <summary>
    /// Stub de notificaciones móviles (SMS/WhatsApp).
    /// Twilio fue removido del proyecto — las notificaciones se realizan
    /// exclusivamente por correo electrónico a través de Brevo.
    /// </summary>
    public class NotificationSmsService
    {
        public NotificationSmsService(IConfiguration configuration)
        {
            // Sin configuración requerida — servicio deshabilitado.
        }

        public void EnviarSmsTransaccional(string telefonoDestino, string mensaje)
        {
            Console.WriteLine($"[SMS Stub] Notificación omitida (Twilio eliminado). Destino: {telefonoDestino}");
        }

        public void EnviarWhatsAppTransaccional(string telefonoDestino, string mensaje)
        {
            Console.WriteLine($"[WhatsApp Stub] Notificación omitida (Twilio eliminado). Destino: {telefonoDestino}");
        }
    }
}