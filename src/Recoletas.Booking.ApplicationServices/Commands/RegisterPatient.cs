using MediatR;
using Recoletas.Booking.Contracts.Models;

namespace Recoletas.Booking.ApplicationServices.Commands;

public sealed record RegisterPatient(RegisterPatientRequest Request) : IRequest<PatientSummary>;
