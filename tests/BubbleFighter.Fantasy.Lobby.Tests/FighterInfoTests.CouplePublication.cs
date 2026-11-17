using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Couples;
using Xunit;

namespace BubbleFighter.Fantasy.Lobby.Tests;

public sealed partial class FighterInfoTests
{
    /// <summary>A failing recipient cannot prevent the committed pair or the healthy proposer from receiving the acceptance.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AcceptedRelationshipInstallsBothSnapshotsBeforeAFailedParticipantPublication(bool failAnswer)
    {
        using var c = await CouplePublicationRig.CreateAsync();
        await c.Flow.ProposeAsync(c["Ann"], new(true, "Bob", Ring, ""), default);
        c.Clear();
        c.BeforeSend = (_, _) =>
        {
            Assert.NotNull(c["Ann"].Feature<ICoupleProfile>().Current);
            Assert.NotNull(c["Bob"].Feature<ICoupleProfile>().Current);
        };
        c.Fail = (session, message) => ReferenceEquals(session, c["Bob"])
            && (failAnswer ? message is CoupleAnswerPublished : message is CoupleProfilePublished);

        await Assert.ThrowsAsync<IOException>(() => c.Flow.AnswerAsync(c["Bob"], new(true, "Ann", "Bob"), default).AsTask());

        Assert.NotNull(await c.Store.FindRelationshipAsync(c.Account("Ann"), default));
        Assert.True(Assert.Single(c.Messages<CoupleAnswerPublished>("Ann")).Accepted);
        Assert.Equal(new[] { typeof(CoupleProfilePublished), typeof(CouplePartnerPublished), typeof(CoupleAnswerPublished) }, c.Order("Ann"));
    }

    /// <summary>A failed breakup reply cannot retain either cached relationship or block the other fighter from clearing it.</summary>
    [Fact]
    public async Task FailedBreakupReplyStillClearsBothSnapshotsAndNotifiesTheHealthyPartner()
    {
        using var c = await CouplePublicationRig.CreateAsync();
        await c.FormAsync();
        c.Clock.Advance(TimeSpan.FromHours(24));
        c.Clear();
        c.BeforeSend = (_, _) =>
        {
            Assert.Null(c["Ann"].Feature<ICoupleProfile>().Current);
            Assert.Null(c["Bob"].Feature<ICoupleProfile>().Current);
            Assert.Null(c["Ann"].Feature<ICoupleProfile>().Scene);
            Assert.Null(c["Bob"].Feature<ICoupleProfile>().Scene);
        };
        c.Fail = (session, message) => ReferenceEquals(session, c["Ann"]) && message is CoupleBreakUpReplied;

        await Assert.ThrowsAsync<IOException>(() => c.Flow.BreakUpAsync(c["Ann"], new("Bob"), default).AsTask());

        Assert.Null(await c.Store.FindRelationshipAsync(c.Account("Ann"), default));
        Assert.Equal(CoupleBreakUpStatus.Left, Assert.Single(c.Messages<CoupleBreakUpReplied>("Bob")).Status);
        Assert.Equal(new[] { typeof(CoupleBreakUpReplied), typeof(CoupleProfilePublished), typeof(CouplePartnerPublished) }, c.Order("Bob"));
    }

    /// <summary>Both committed ring snapshots update before publication and a healthy partner still receives the change.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RingChangeInstallsBothNewRingsWhenTheRequestersPublicationFails(bool failReply)
    {
        using var c = await CouplePublicationRig.CreateAsync();
        await c.FormAsync();
        c.Clear();
        c.BeforeSend = (_, _) =>
        {
            Assert.Equal(Ring + 21, c["Ann"].Feature<ICoupleProfile>().Current!.DisplayRingItemId);
            Assert.Equal(Ring + 21, c["Bob"].Feature<ICoupleProfile>().Current!.DisplayRingItemId);
        };
        c.Fail = (session, message) => ReferenceEquals(session, c["Ann"])
            && (failReply ? message is CoupleRingChangeReplied : message is CoupleProfilePublished);

        await Assert.ThrowsAsync<IOException>(() => c.Flow.ChangeRingAsync(c["Ann"], new(Ring + 1), default).AsTask());

        Assert.Equal(Ring + 1, (await c.Store.FindRelationshipAsync(c.Account("Ann"), default))!.RingItemId);
        Assert.True(Assert.Single(c.Messages<CoupleRingChangeReplied>("Bob")).Changed);
        Assert.Equal(new[] { typeof(CoupleProfilePublished), typeof(CouplePartnerPublished), typeof(CoupleRingChangeReplied) }, c.Order("Bob"));
    }

    /// <summary>Both transport failures remain observable after installing the committed relationship in both sessions.</summary>
    [Fact]
    public async Task BothPublicationFailuresAreReportedAfterBothSnapshotsAreInstalled()
    {
        using var c = await CouplePublicationRig.CreateAsync();
        await c.Flow.ProposeAsync(c["Ann"], new(true, "Bob", Ring, ""), default);
        c.Fail = (_, message) => message is CoupleProfilePublished;

        var failure = await Assert.ThrowsAsync<AggregateException>(() =>
            c.Flow.AnswerAsync(c["Bob"], new(true, "Ann", "Bob"), default).AsTask());

        Assert.Equal(2, failure.InnerExceptions.Count);
        Assert.All(failure.InnerExceptions, error => Assert.IsType<IOException>(error));
        Assert.NotNull(c["Ann"].Feature<ICoupleProfile>().Current);
        Assert.NotNull(c["Bob"].Feature<ICoupleProfile>().Current);
    }

    /// <summary>A transient read after acceptance leaves the failed participant eligible for a bounded publication retry.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedPostCommitReadIsRetriedForNewAndPreviouslyCleanRegistrations(bool restoreBeforeAcceptance)
    {
        using var c = await CouplePublicationRig.CreateAsync();
        if (restoreBeforeAcceptance)
        {
            await c.Flow.RestoreAsync(c["Ann"], c.User("Ann"), default);
            await c.Flow.RestoreAsync(c["Bob"], c.User("Bob"), default);
        }
        await c.Flow.ProposeAsync(c["Ann"], new(true, "Bob", Ring, ""), default);
        c.Clear();
        var failOnce = 1;
        c.FailRelationshipRead = member => member == c.Account("Bob") && Interlocked.Exchange(ref failOnce, 0) == 1;

        await Assert.ThrowsAsync<IOException>(() => c.Flow.AnswerAsync(c["Bob"], new(true, "Ann", "Bob"), default).AsTask());

        Assert.NotNull(await c.Store.FindRelationshipAsync(c.Account("Bob"), default));
        Assert.True(Assert.Single(c.Messages<CoupleAnswerPublished>("Ann")).Accepted);
        Assert.Empty(c.Messages<CouplePartnerPublished>("Bob"));

        await c.Flow.SweepPresenceAsync(default);

        Assert.NotNull(c["Bob"].Feature<ICoupleProfile>().Current);
        Assert.Equal(new CoupleSceneState(c.User("Ann"), "Ann"), c["Bob"].Feature<ICoupleProfile>().Scene);
        Assert.NotNull(Assert.Single(c.Messages<CoupleProfilePublished>("Bob")).Profile);
        Assert.Equal(c.User("Ann"), Assert.Single(c.Messages<CouplePartnerPublished>("Bob")).PartnerUserNum);
        Assert.Single(c.Messages<CoupleAnswerPublished>("Ann"));
        await c.Flow.SweepPresenceAsync(default);
        Assert.Single(c.Messages<CouplePartnerPublished>("Bob"));
    }

    /// <summary>Cancelling the completed request cannot abandon the other participant after the relationship has committed.</summary>
    [Fact]
    public async Task RequestCancellationAfterCommitDoesNotCancelTheHealthyPartnersPublication()
    {
        using var c = await CouplePublicationRig.CreateAsync();
        using var request = new CancellationTokenSource();
        await c.Flow.ProposeAsync(c["Ann"], new(true, "Bob", Ring, ""), request.Token);
        c.Clear();
        c.BeforeSend = (_, _) => request.Cancel();

        await c.Flow.AnswerAsync(c["Bob"], new(true, "Ann", "Bob"), request.Token);

        Assert.True(request.IsCancellationRequested);
        Assert.True(Assert.Single(c.Messages<CoupleAnswerPublished>("Ann")).Accepted);
        Assert.True(Assert.Single(c.Messages<CoupleAnswerPublished>("Bob")).Accepted);
        Assert.NotNull(await c.Store.FindRelationshipAsync(c.Account("Ann"), default));
    }

    /// <summary>A cancelled request must not enter the persistent relationship transaction.</summary>
    [Fact]
    public async Task RequestCancellationBeforeCommitStillPreventsRelationshipCreation()
    {
        using var c = await CouplePublicationRig.CreateAsync();
        await c.Flow.ProposeAsync(c["Ann"], new(true, "Bob", Ring, ""), default);
        using var request = new CancellationTokenSource();
        request.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            c.Flow.AnswerAsync(c["Bob"], new(true, "Ann", "Bob"), request.Token).AsTask());

        Assert.Null(await c.Store.FindRelationshipAsync(c.Account("Ann"), default));
        Assert.Empty(c.Messages<CoupleAnswerPublished>("Ann"));
        Assert.Empty(c.Messages<CoupleAnswerPublished>("Bob"));
    }

    /// <summary>One failed presence notification is logged without killing the worker or blocking a later bounded retry.</summary>
    [Fact]
    public async Task PresenceWorkerLogsAFailedParticipantContinuesAndRetriesOnAnotherWakeup()
    {
        using var c = await CouplePublicationRig.CreateAsync();
        await c.FormAsync();
        await c.FormAsync("Cat", "Dan");
        c.Clear();
        var healthyDelivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var retryDelivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failOnce = 1;
        c.Fail = (session, message) => ReferenceEquals(session, c["Bob"]) && message is CoupleProfilePublished
            && Interlocked.Exchange(ref failOnce, 0) == 1;
        c.AfterSend = (session, message) =>
        {
            if (message is not CouplePartnerPublished) return;
            if (ReferenceEquals(session, c["Dan"])) healthyDelivered.TrySetResult();
            if (ReferenceEquals(session, c["Bob"])) retryDelivered.TrySetResult();
        };
        c.Online.Remove(c["Ann"].Connection.Id);
        c.Flow.ConnectionClosed(c["Ann"].Connection.Id);
        c.Online.Remove(c["Cat"].Connection.Id);
        c.Flow.ConnectionClosed(c["Cat"].Connection.Id);
        using var stopping = new CancellationTokenSource();
        var worker = c.Flow.RunPresenceAsync(stopping.Token).AsTask();
        try
        {
            await healthyDelivered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Single(c.Logger.Errors);
            Assert.False(worker.IsCompleted);
            Assert.False(c["Bob"].Feature<ICoupleProfile>().Current!.PartnerOnline);
            Assert.False(c["Dan"].Feature<ICoupleProfile>().Current!.PartnerOnline);
            c.Flow.ConnectionClosed(-1);
            await retryDelivered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(Assert.Single(c.Messages<CouplePartnerPublished>("Bob")).PartnerOnline);
        }
        finally
        {
            stopping.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker);
        }
    }
}
