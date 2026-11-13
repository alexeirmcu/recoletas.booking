using MediatR;
using Recoletas.Booking.Contracts.Models;

namespace Recoletas.Booking.ApplicationServices.Commands;

public sealed record JoinWaitlist(Guid PatientId, JoinWaitlistRequest Request) : IRequest<WaitlistResult>;
