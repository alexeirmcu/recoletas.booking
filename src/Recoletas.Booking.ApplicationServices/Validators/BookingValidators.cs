using FluentValidation;
using Recoletas.Booking.ApplicationServices.Commands;
using Recoletas.Booking.ApplicationServices.Errors;
using Recoletas.Booking.Domain.ValueObjects;

namespace Recoletas.Booking.ApplicationServices.Validators;

public sealed class BlockSlotValidator : AbstractValidator<BlockSlot>
{
    public BlockSlotValidator()
    {
        RuleFor(request => request.SlotId).NotEmpty();
        RuleFor(request => request.PatientId).NotEmpty();
    }
}

public sealed class ConfirmHoldValidator : AbstractValidator<ConfirmHold>
{
    public ConfirmHoldValidator()
    {
        RuleFor(request => request.SlotId).NotEmpty();
        RuleFor(request => request.PatientId).NotEmpty();
        RuleFor(request => request.Request).NotNull();
        RuleFor(request => request.Request.Generation).GreaterThan(0);
    }
}

public sealed class CancelAppointmentValidator : AbstractValidator<CancelAppointment>
{
    public CancelAppointmentValidator()
    {
        RuleFor(request => request.ReservationId).NotEmpty();
        RuleFor(request => request.PatientId).NotEmpty();
    }
}

public sealed class JoinWaitlistValidator : AbstractValidator<JoinWaitlist>
{
    public JoinWaitlistValidator()
    {
        RuleFor(request => request.PatientId).NotEmpty();
        RuleFor(request => request.Request).NotNull();
        RuleFor(request => request.Request.TestType)
            .NotEmpty()
            .MaximumLength(100)
            .Must(TestType.IsSupported)
            .WithMessage("Test type must be one of: BloodTest, Ultrasound, MRI.")
            .WithErrorCode(BookingErrorCodes.UnsupportedTestType);
    }
}

public sealed class AcceptOfferValidator : AbstractValidator<AcceptOffer>
{
    public AcceptOfferValidator()
    {
        RuleFor(request => request.SlotId).NotEmpty();
        RuleFor(request => request.PatientId).NotEmpty();
        RuleFor(request => request.Request).NotNull();
        RuleFor(request => request.Request.Generation).GreaterThan(0);
    }
}

public sealed class DeclineOfferValidator : AbstractValidator<DeclineOffer>
{
    public DeclineOfferValidator()
    {
        RuleFor(request => request.SlotId).NotEmpty();
        RuleFor(request => request.PatientId).NotEmpty();
        RuleFor(request => request.Request).NotNull();
        RuleFor(request => request.Request.Generation).GreaterThan(0);
    }
}

public sealed class QueryAgendaValidator : AbstractValidator<QueryAgenda>
{
    public QueryAgendaValidator()
    {
        RuleFor(request => request.To).GreaterThan(request => request.From);
        RuleFor(request => request.TestType)
            .MaximumLength(100)
            .Must(testType => string.IsNullOrWhiteSpace(testType) || TestType.IsSupported(testType))
            .WithMessage("Test type must be one of: BloodTest, Ultrasound, MRI.")
            .WithErrorCode(BookingErrorCodes.UnsupportedTestType);
    }
}

public sealed class QueryAppointmentsValidator : AbstractValidator<QueryAppointments>
{
    public QueryAppointmentsValidator() => RuleFor(request => request.PatientId).NotEmpty();
}

public sealed class QueryPendingAppointmentsValidator : AbstractValidator<QueryPendingAppointments>
{
    public QueryPendingAppointmentsValidator() => RuleFor(request => request.PatientId).NotEmpty();
}

public sealed class QueryAdminAppointmentsValidator : AbstractValidator<QueryAdminAppointments>
{
    public QueryAdminAppointmentsValidator() =>
        RuleFor(request => request.To).GreaterThan(request => request.From);
}

public sealed class RegisterPatientValidator : AbstractValidator<RegisterPatient>
{
    public RegisterPatientValidator()
    {
        RuleFor(request => request.Request).NotNull();
        RuleFor(request => request.Request.Name).NotEmpty().MaximumLength(200);
        RuleFor(request => request.Request.Nhc).MaximumLength(100);
    }
}
