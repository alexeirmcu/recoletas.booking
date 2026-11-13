using MediatR;
using Recoletas.Booking.ApplicationServices.Commands;
using Recoletas.Booking.ApplicationServices.Errors;
using Recoletas.Booking.Contracts.Models;
using Recoletas.Booking.Domain.Entities;
using Recoletas.Booking.Domain.Repository;
using Recoletas.Booking.Domain.ValueObjects;

namespace Recoletas.Booking.ApplicationServices.Handlers;

public sealed class JoinWaitlistHandler(IWaitlistRepository waitlist, IPatientRepository patients, IClock clock)
    : IRequestHandler<JoinWaitlist, WaitlistResult>
{
    public async Task<WaitlistResult> Handle(JoinWaitlist request, CancellationToken cancellationToken)
    {
        var patientId = new PatientId(request.PatientId);
        if (await patients.GetByIdAsync(patientId, cancellationToken) is null)
        {
            return new(false, false, BookingErrorCodes.PatientNotFound);
        }

        var entry = new WaitlistEntry(Guid.NewGuid(), patientId, new TestType(request.Request.TestType),
            clock.UtcNow.ToUniversalTime());
        var joined = await waitlist.TryAddAsync(entry, cancellationToken);
        return joined ? new(true, false) : new(false, true, BookingErrorCodes.AlreadyJoined);
    }
}
