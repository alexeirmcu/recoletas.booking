namespace Recoletas.Booking.ApplicationServices.Errors;

public static class BookingErrorCodes
{
    public const string PatientNotFound = "patient_not_found";
    public const string PatientNhcAlreadyExists = "patient_nhc_already_exists";
    public const string SlotNotFound = "slot_not_found";
    public const string SlotUnavailable = "slot_unavailable";
    public const string HoldInvalidOrExpired = "hold_invalid_or_expired";
    public const string OfferInvalidOrExpired = "offer_invalid_or_expired";
    public const string AppointmentNotFound = "appointment_not_found";
    public const string AlreadyJoined = "already_joined";
    public const string UnsupportedTestType = "unsupported_test_type";
    public const string FromAndToRequired = "from_and_to_required";
    public const string InvalidPatientIdentity = "invalid_patient_identity";
    public const string PatientIdentityRequired = "patient_identity_required";
    public const string ValidationFailed = "validation_failed";
    public const string NotFound = "not_found";
    public const string Conflict = "conflict";
    public const string Unauthorized = "unauthorized";
    public const string InternalServerError = "internal_server_error";
}
