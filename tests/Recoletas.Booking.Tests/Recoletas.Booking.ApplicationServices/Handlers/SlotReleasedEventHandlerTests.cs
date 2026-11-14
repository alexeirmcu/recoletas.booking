using MediatR;
using Moq;
using Recoletas.Booking.ApplicationServices.Abstractions;
using Recoletas.Booking.ApplicationServices.Commands;
using Recoletas.Booking.ApplicationServices.Handlers;
using Recoletas.Booking.Contracts.Events;
using Recoletas.Booking.Domain.Entities;
using Recoletas.Booking.Domain.ValueObjects;

namespace Recoletas.Booking.Tests.Recoletas.Booking.ApplicationServices.Handlers;

[TestClass]
[TestCategory("Unit")]
public sealed class SlotReleasedEventHandlerTests
{
    [TestMethod]
    public async Task Handle_DuplicateRelease_DoesNotCreateSecondOffer()
    {
        var now = DateTimeOffset.UtcNow;
        var slot = new Slot(SlotId.New(), new TestType("BloodTest"), now.AddDays(1), now.AddDays(1).AddMinutes(30));
        var released = new SlotReleased(Guid.NewGuid(), slot.Id.Value, slot.TestType.Value, slot.Generation, now);
        var slots = new Mock<ISlotRepository>();
        slots.Setup(s => s.GetByIdAsync(slot.Id, It.IsAny<CancellationToken>())).ReturnsAsync(slot);
        var mediator = new Mock<IMediator>();
        mediator.Setup(m => m.Send(It.IsAny<OfferNextWaiter>(), It.IsAny<CancellationToken>()))
            .Callback(() => Assert.IsTrue(slot.TryOffer(PatientId.New(), now, now.AddMinutes(5))))
            .ReturnsAsync(true);
        var handler = new SlotReleasedEventHandler(slots.Object, mediator.Object);

        await handler.HandleAsync(released, CancellationToken.None);
        await handler.HandleAsync(released, CancellationToken.None);

        mediator.Verify(m => m.Send(It.Is<OfferNextWaiter>(c =>
                c.SlotId == slot.Id.Value && c.ExpectedGeneration == released.Generation),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Handle_StaleGenerationOrMissingSlot_DoesNotPromote(bool missing)
    {
        var now = DateTimeOffset.UtcNow;
        var slot = new Slot(SlotId.New(), new TestType("BloodTest"), now.AddDays(1), now.AddDays(1).AddMinutes(30));
        var released = new SlotReleased(Guid.NewGuid(), slot.Id.Value, slot.TestType.Value, -1, now);
        var slots = new Mock<ISlotRepository>();
        slots.Setup(s => s.GetByIdAsync(slot.Id, It.IsAny<CancellationToken>())).ReturnsAsync(missing ? null : slot);
        var mediator = new Mock<IMediator>(MockBehavior.Strict);
        var handler = new SlotReleasedEventHandler(slots.Object, mediator.Object);

        await handler.HandleAsync(released, CancellationToken.None);

        mediator.VerifyNoOtherCalls();
    }
}
