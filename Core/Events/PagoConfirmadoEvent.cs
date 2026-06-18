using System;

namespace TechStore360.Core.Events
{
    public record PagoConfirmadoEvent(
        int NumeroFactura,
        DateTime Timestamp
    );
}
