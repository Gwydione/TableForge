using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using TableForge.Dice;
using TableForge.Domain;
using TableForge.ViewModels;

namespace TableForge.Tests;

/// <summary>
/// A scripted stand-in for the dddice network/WebView boundary. It lets a test decide when preparation finishes, when the dice
/// "settle" and when things fail, so nothing in the automated suite talks to the live dddice service.
/// </summary>
internal sealed class FakeDddiceRoller : IDddiceRoomRoller
{
    private TaskCompletionSource<IReadOnlyList<DddiceFace>>? _pending;

    public int PrepareCalls { get; private set; }
    public List<IReadOnlyList<string>> Requests { get; } = [];
    public CancellationToken LastRollToken { get; private set; }

    /// <summary>When set, preparation waits for this to be completed.</summary>
    public TaskCompletionSource? PrepareGate { get; set; }
    public Exception? PrepareFailure { get; set; }

    public async Task PrepareAsync(CancellationToken cancellationToken)
    {
        PrepareCalls++;
        if (PrepareFailure is { } failure) throw failure;
        if (PrepareGate is { } gate) await gate.Task.WaitAsync(cancellationToken);
    }

    public Task<IReadOnlyList<DddiceFace>> RollAsync(IReadOnlyList<string> diceTypes, CancellationToken cancellationToken)
    {
        Requests.Add(diceTypes);
        LastRollToken = cancellationToken;
        _pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        return _pending.Task.WaitAsync(cancellationToken);
    }

    public bool IsRollPending => _pending is { Task.IsCompleted: false };

    /// <summary>The dice settle: dddice's "roll:finished" arrives with these faces.</summary>
    public void Settle(params (string Type, int Value)[] faces) =>
        _pending!.SetResult(faces.Select(f => new DddiceFace(f.Type, f.Value)).ToList());

    public void FailRoll(string message) => _pending!.SetException(new DddiceException(message));

    public static (string, int)[] Twod6(int a, int b) => [("d6", a), ("d6", b)];
}

internal static class Wait
{
    public static async Task Until(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Timed out waiting for: " + what);
            await Task.Delay(5);
        }
    }
}

/// <summary>The abstraction change, the Built-in provider under it, and the dddice dice mapping.</summary>
public class DiceProviderContractTests
{
    [Fact]
    public async Task Built_in_async_provider_returns_the_same_final_number_as_before_and_is_already_complete()
    {
        var provider = new BuiltInDiceProvider(new Random(42));
        var expected = new BuiltInDiceProvider(new Random(42)).Roll(DiceExpression.Parse("2d6+1"));

        var task = provider.RollAsync(DiceExpression.Parse("2d6+1"), CancellationToken.None);

        Assert.True(task.IsCompletedSuccessfully); // Built-in rolls immediately: no waiting, so nothing on screen changes for Built-in users
        Assert.Equal(expected, await task);
    }

    [Fact]
    public void Built_in_provider_honours_an_already_cancelled_token()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.True(new BuiltInDiceProvider().RollAsync(DiceExpression.Parse("d20"), cancelled.Token).IsCanceled);
    }

    [Theory]
    [InlineData("d4", "d4")]
    [InlineData("d6", "d6")]
    [InlineData("d8", "d8")]
    [InlineData("d10", "d10")]
    [InlineData("d12", "d12")]
    [InlineData("d20", "d20")]
    [InlineData("2d6", "d6,d6")]
    [InlineData("3d8", "d8,d8,d8")]
    [InlineData("d100", "d10x,d10")]
    [InlineData("2d100", "d10x,d10,d10x,d10")]
    public void Each_supported_TableForge_die_maps_to_the_dddice_dice_proven_in_the_spike(string expression, string expected) =>
        Assert.Equal(expected, string.Join(",", DddiceDiceMapping.ToDddice(DiceExpression.Parse(expression))));

    [Theory]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(66)]
    public void A_die_dddice_does_not_have_is_refused_before_anything_is_sent(int sides)
    {
        var ex = Assert.Throws<DddiceUnsupportedDiceException>(() => DddiceDiceMapping.ToDddice(new DiceExpression(1, sides)));
        Assert.Contains("Built-in", ex.Message);
    }

    [Fact]
    public void Faces_are_totalled_and_the_TableForge_modifier_is_applied_exactly_once()
    {
        var result = DddiceDiceMapping.Interpret(DiceExpression.Parse("2d6+1"), [new("d6", 2), new("d6", 5)]);

        Assert.Equal(7, result.BaseSubtotal);
        Assert.Equal(8, result.Final);
    }

    [Fact]
    public void A_negative_modifier_is_applied_by_TableForge_too()
    {
        Assert.Equal(-1, DddiceDiceMapping.Interpret(DiceExpression.Parse("d20-2"), [new("d20", 1)]).Final);
    }

    [Theory]
    [InlineData(8, 4, 84)]      // tens die 8 (=80) + ones die 4
    [InlineData(10, 3, 3)]      // tens die 10 means "00"
    [InlineData(9, 10, 100)]    // 90 + 10
    [InlineData(10, 10, 10)]    // 00 + 10 (the ones die's 10 counts as ten, so 100 comes only from 90 + 10)
    [InlineData(1, 1, 11)]
    public void A_d100_is_a_tens_die_plus_a_ones_die_and_covers_1_to_100(int tens, int ones, int expected)
    {
        var result = DddiceDiceMapping.Interpret(DiceExpression.Parse("d100"), [new("d10x", tens), new("d10", ones)]);

        Assert.Equal(expected, result.Final);
        Assert.True(DiceExpression.Parse("d100").IsLegal(result.Final));
    }

    [Fact]
    public void A_modified_d100_keeps_the_existing_semantics_the_modifier_is_added_to_the_1_to_100_value()
    {
        var dice = DiceExpression.Parse("d100+5");
        var result = DddiceDiceMapping.Interpret(dice, [new("d10x", 8), new("d10", 4)]);

        Assert.Equal(89, result.Final);
        Assert.Equal("89", dice.FormatValue(result.Final));      // a modified d100 never shows "00"
        Assert.Equal("00", DiceExpression.Parse("d100").FormatValue(100));   // an unmodified one still does
    }

    [Fact]
    public void The_individual_faces_of_a_2d6_are_kept_in_order_so_a_future_d66_can_read_3_5_as_35()
    {
        var result = DddiceDiceMapping.Interpret(DiceExpression.Parse("2d6"), [new("d6", 3), new("d6", 5)]);

        Assert.Equal([3, 5], result.Faces.Select(f => f.Value));
        Assert.Equal(8, result.Final);                                   // today's TableForge meaning: 3 + 5
        Assert.Equal(35, result.Faces[0].Value * 10 + result.Faces[1].Value); // what d66 will read from the same faces
    }

    [Fact]
    public void Faces_that_do_not_match_the_request_or_are_out_of_range_are_rejected_not_guessed_at()
    {
        var d6 = DiceExpression.Parse("2d6");
        Assert.Throws<DddiceException>(() => DddiceDiceMapping.Interpret(d6, [new("d6", 3)]));                    // a die is missing
        Assert.Throws<DddiceException>(() => DddiceDiceMapping.Interpret(d6, [new("d8", 3), new("d6", 5)]));      // the wrong die
        Assert.Throws<DddiceException>(() => DddiceDiceMapping.Interpret(d6, [new("d6", 7), new("d6", 5)]));      // a face a d6 cannot show
        Assert.Throws<DddiceException>(() => DddiceDiceMapping.Interpret(DiceExpression.Parse("d100"), [new("d10x", 11), new("d10", 1)]));
    }
}

/// <summary>What the page's events mean: roll:started reveals nothing; only roll:finished for our own roll completes it.</summary>
public class DddiceRollTrackerTests
{
    private const string Started = """{"kind":"roll:started","roll":{"uuid":"u1","externalId":"mine","equation":"2d6","total":7,"values":[{"type":"d6","value":2},{"type":"d6","value":5}]}}""";
    private const string Finished = """{"kind":"roll:finished","roll":{"uuid":"u1","externalId":"mine","equation":"2d6","total":7,"values":[{"type":"d6","value":2},{"type":"d6","value":5}]}}""";

    [Fact]
    public void Roll_started_does_not_complete_the_roll_even_though_the_faces_are_already_known()
    {
        var tracker = new DddiceRollTracker();
        var settled = tracker.Begin("mine");

        tracker.OnPageMessage(Started);

        Assert.False(settled.IsCompleted);   // no result is available to show yet
        Assert.True(tracker.HasStarted);
        Assert.True(tracker.IsRolling);
    }

    [Fact]
    public async Task Roll_finished_completes_the_roll_with_the_faces_in_order()
    {
        var tracker = new DddiceRollTracker();
        var settled = tracker.Begin("mine");
        tracker.OnPageMessage(Started);

        tracker.OnPageMessage(Finished);

        Assert.True(settled.IsCompletedSuccessfully);
        Assert.Equal([2, 5], (await settled).Select(f => f.Value));
        Assert.False(tracker.IsRolling);
    }

    [Fact]
    public void Events_for_somebody_elses_roll_are_ignored()
    {
        var tracker = new DddiceRollTracker();
        var settled = tracker.Begin("someone-else");

        tracker.OnPageMessage(Started);
        tracker.OnPageMessage(Finished);

        Assert.False(settled.IsCompleted);
    }

    [Fact]
    public void A_roll_error_from_the_page_fails_the_roll_with_a_readable_message()
    {
        var tracker = new DddiceRollTracker();
        var settled = tracker.Begin("mine");

        tracker.OnPageMessage("""{"kind":"rollError","message":"HTTP 403 forbidden"}""");

        var ex = Assert.IsType<DddiceException>(settled.Exception!.InnerException);
        Assert.Contains("403", ex.Message);
    }

    [Fact]
    public async Task A_timed_out_roll_is_abandoned_cleanly_and_a_late_finish_changes_nothing()
    {
        var tracker = new DddiceRollTracker();
        var settled = tracker.Begin("mine");

        await Assert.ThrowsAsync<TimeoutException>(() => settled.WaitAsync(TimeSpan.FromMilliseconds(30)));
        tracker.Abandon();
        tracker.OnPageMessage(Finished);   // the animation eventually ends: nobody is waiting any more

        Assert.False(tracker.IsRolling);
        Assert.NotNull(tracker.Begin("mine2")); // and the next roll can start straight away
    }

    [Fact]
    public void Only_one_roll_can_be_tracked_at_a_time_and_messages_when_idle_are_ignored()
    {
        var tracker = new DddiceRollTracker();
        tracker.OnPageMessage(Finished);   // nothing is rolling: harmless
        tracker.Begin("mine");
        Assert.Throws<InvalidOperationException>(() => { _ = tracker.Begin("again"); });
    }
}

public class DddiceDiceProviderTests
{
    [Fact]
    public async Task The_dddice_provider_waits_until_the_dice_settle_then_returns_the_final_number()
    {
        var roller = new FakeDddiceRoller();
        var provider = new DddiceDiceProvider(roller);

        var roll = provider.RollAsync(DiceExpression.Parse("2d6+1"), CancellationToken.None);

        Assert.False(roll.IsCompleted);                              // dice are in the air
        Assert.Equal(["d6,d6"], roller.Requests.Select(r => string.Join(",", r)));   // ONLY the base dice went to dddice
        roller.Settle(FakeDddiceRoller.Twod6(2, 5));
        Assert.Equal(8, await roll);                                 // 2 + 5, then TableForge's +1, once
    }

    [Fact]
    public async Task An_unsupported_die_fails_the_roll_without_contacting_dddice()
    {
        var roller = new FakeDddiceRoller();
        var provider = new DddiceDiceProvider(roller);

        await Assert.ThrowsAsync<DddiceUnsupportedDiceException>(() => provider.RollAsync(new DiceExpression(1, 7), CancellationToken.None));
        Assert.Empty(roller.Requests);
    }

    [Fact]
    public async Task The_detailed_roll_keeps_every_face()
    {
        var roller = new FakeDddiceRoller();
        var pending = new DddiceDiceProvider(roller).RollDetailedAsync(DiceExpression.Parse("2d6"), CancellationToken.None);
        roller.Settle(FakeDddiceRoller.Twod6(3, 5));

        var result = await pending;
        Assert.Equal([3, 5], result.Faces.Select(f => f.Value));
    }

    [Fact]
    public async Task Cancelling_a_roll_abandons_it_cleanly()
    {
        var roller = new FakeDddiceRoller();
        using var cancel = new CancellationTokenSource();
        var roll = new DddiceDiceProvider(roller).RollAsync(DiceExpression.Parse("d20"), cancel.Token);

        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => roll);
    }
}

/// <summary>Choosing, preparing, failing and falling back: the state behind the "Dice" selector.</summary>
public class DiceProviderViewModelTests
{
    private static (DiceProviderViewModel Vm, FakeDddiceRoller Roller, FixedDice BuiltIn, List<string> Saved) Make(string? saved = null)
    {
        var roller = new FakeDddiceRoller();
        var builtIn = new FixedDice(4);
        var savedValues = new List<string>();
        var vm = new DiceProviderViewModel(builtIn, new DddiceDiceProvider(roller), () => saved, savedValues.Add);
        return (vm, roller, builtIn, savedValues);
    }

    [Fact]
    public void Built_in_is_the_default_ready_immediately_and_does_no_dddice_work()
    {
        var (vm, roller, _, saved) = Make();

        Assert.Equal(DiceProviderKind.BuiltIn, vm.Selected);
        Assert.True(vm.CanRoll);
        Assert.False(vm.ShowDicePanel);
        Assert.Equal(0, roller.PrepareCalls);
        Assert.Empty(saved);
    }

    [Fact]
    public async Task Choosing_dddice_shows_Preparing_and_keeps_Roll_off_until_the_environment_is_ready()
    {
        var (vm, roller, _, saved) = Make();
        roller.PrepareGate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        vm.Select(DiceProviderKind.Dddice);

        Assert.Equal(DiceProviderState.Preparing, vm.State);
        Assert.Equal("Preparing dddice…", vm.Message);
        Assert.False(vm.CanRoll);
        Assert.True(vm.ShowDicePanel);
        Assert.Equal(["dddice"], saved);

        roller.PrepareGate.SetResult();
        await Wait.Until(() => vm.State == DiceProviderState.Ready, "dddice ready");
        Assert.True(vm.CanRoll);
        Assert.Contains("ready", vm.Message);
        Assert.Equal(1, roller.PrepareCalls);
    }

    [Fact]
    public async Task A_preparation_failure_is_reported_and_Use_Built_in_Dice_recovers_without_rolling_anything()
    {
        var (vm, roller, builtIn, saved) = Make();
        roller.PrepareFailure = new DddiceException("TableForge could not reach dddice. Check the internet connection.");

        vm.Select(DiceProviderKind.Dddice);
        await Wait.Until(() => vm.State == DiceProviderState.Failed, "failure");

        Assert.Equal("TableForge could not reach dddice. Check the internet connection.", vm.Message);
        Assert.True(vm.ShowFailure);
        Assert.False(vm.CanRoll);

        vm.UseBuiltInCommand.Execute(null);

        Assert.Equal(DiceProviderKind.BuiltIn, vm.Selected);
        Assert.True(vm.CanRoll);
        Assert.False(vm.ShowFailure);
        Assert.Equal(0, builtIn.Calls);                       // nothing was rerolled behind the person's back
        Assert.Equal(["dddice", "builtin"], saved);
    }

    [Fact]
    public async Task An_unexpected_exception_from_preparation_does_not_escape_and_becomes_a_message()
    {
        var (vm, roller, _, _) = Make();
        roller.PrepareFailure = new InvalidOperationException("boom");

        vm.Select(DiceProviderKind.Dddice);
        await Wait.Until(() => vm.State == DiceProviderState.Failed, "failure");

        Assert.Contains("boom", vm.Message);
    }

    [Fact]
    public async Task Try_again_prepares_once_more_and_can_succeed()
    {
        var (vm, roller, _, _) = Make();
        roller.PrepareFailure = new DddiceException("dddice is limiting new guest sessions right now. Wait a minute and try again.");
        vm.Select(DiceProviderKind.Dddice);
        await Wait.Until(() => vm.State == DiceProviderState.Failed, "failure");
        Assert.True(vm.RetryCommand.CanExecute(null));

        roller.PrepareFailure = null;
        vm.RetryCommand.Execute(null);

        await Wait.Until(() => vm.State == DiceProviderState.Ready, "ready after retry");
        Assert.Equal(2, roller.PrepareCalls);
    }

    [Fact]
    public async Task Switching_back_to_Built_in_while_preparing_cancels_it_and_Built_in_works()
    {
        var (vm, roller, _, _) = Make();
        roller.PrepareGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.Select(DiceProviderKind.Dddice);

        vm.Select(DiceProviderKind.BuiltIn);

        Assert.True(vm.CanRoll);
        Assert.Equal(DiceProviderKind.BuiltIn, vm.Selected);
        Assert.Equal(4, await vm.RollAsync(DiceExpression.Parse("d6"), CancellationToken.None));
        await Task.Delay(30);
        Assert.Equal(DiceProviderState.Ready, vm.State);      // the abandoned preparation did not flip the state afterwards
        Assert.Equal("", vm.Message);
    }

    [Fact]
    public async Task Rolling_while_dddice_is_still_preparing_is_refused()
    {
        var (vm, roller, _, _) = Make();
        roller.PrepareGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.Select(DiceProviderKind.Dddice);

        await Assert.ThrowsAsync<DddiceException>(() => vm.RollAsync(DiceExpression.Parse("d20"), CancellationToken.None));
        Assert.Empty(roller.Requests);
    }

    [Fact]
    public async Task A_failed_dddice_roll_marks_dddice_failed_and_offers_Built_in_but_does_not_reroll()
    {
        var (vm, roller, builtIn, _) = Make();
        vm.Select(DiceProviderKind.Dddice);
        await Wait.Until(() => vm.State == DiceProviderState.Ready, "ready");

        var roll = vm.RollAsync(DiceExpression.Parse("d20"), CancellationToken.None);
        roller.FailRoll("The dddice roll did not finish within 30 seconds.");
        await Assert.ThrowsAsync<DddiceException>(() => roll);

        Assert.Equal(DiceProviderState.Failed, vm.State);
        Assert.True(vm.ShowFailure);
        Assert.Contains("did not finish", vm.Message);
        Assert.Equal(0, builtIn.Calls);
    }

    [Fact]
    public async Task A_die_dddice_cannot_show_leaves_dddice_ready_rather_than_failed()
    {
        var (vm, _, _, _) = Make();
        vm.Select(DiceProviderKind.Dddice);
        await Wait.Until(() => vm.State == DiceProviderState.Ready, "ready");

        await Assert.ThrowsAsync<DddiceUnsupportedDiceException>(() => vm.RollAsync(new DiceExpression(1, 7), CancellationToken.None));

        Assert.Equal(DiceProviderState.Ready, vm.State);
        Assert.False(vm.ShowFailure);
    }

    [Fact]
    public async Task Restoring_a_saved_dddice_choice_prepares_it_but_a_saved_Built_in_or_nothing_does_no_dddice_work()
    {
        var (dddice, roller1, _, _) = Make("dddice");
        dddice.RestorePreference();
        await Wait.Until(() => dddice.State == DiceProviderState.Ready, "ready");
        Assert.Equal(1, roller1.PrepareCalls);

        foreach (var saved in new string?[] { "builtin", null, "garbage" })
        {
            var (vm, roller, _, _) = Make(saved);
            vm.RestorePreference();
            Assert.Equal(DiceProviderKind.BuiltIn, vm.Selected);
            Assert.Equal(0, roller.PrepareCalls);
        }
    }

    [Fact]
    public async Task A_saved_dddice_choice_that_cannot_start_leaves_TableForge_usable_with_Built_in_on_offer()
    {
        var (vm, roller, builtIn, _) = Make("dddice");
        roller.PrepareFailure = new DddiceException("TableForge could not reach dddice. Check the internet connection.");

        vm.RestorePreference();
        await Wait.Until(() => vm.State == DiceProviderState.Failed, "failure");
        vm.UseBuiltInCommand.Execute(null);

        Assert.Equal(4, await vm.RollAsync(DiceExpression.Parse("d6"), CancellationToken.None));
        Assert.Equal(1, builtIn.Calls);
    }

    [Fact]
    public void An_unreadable_or_unwritable_preference_never_gets_in_the_way()
    {
        var vm = new DiceProviderViewModel(new FixedDice(4), new DddiceDiceProvider(new FakeDddiceRoller()),
            () => throw new IOException("locked"), _ => throw new IOException("read-only"));

        vm.RestorePreference();
        vm.Select(DiceProviderKind.Dddice);
        vm.Select(DiceProviderKind.BuiltIn);

        Assert.Equal(DiceProviderKind.BuiltIn, vm.Selected);
    }

    [Fact]
    public void Without_a_dddice_provider_the_option_is_unavailable_and_cannot_be_selected()
    {
        var vm = new DiceProviderViewModel(new FixedDice(4));

        vm.Select(DiceProviderKind.Dddice);

        Assert.False(vm.IsDddiceAvailable);
        Assert.Equal(DiceProviderKind.BuiltIn, vm.Selected);
    }
}

/// <summary>The roll screen with the dddice provider: waits for the dice, protects against double clicks, changes nothing on failure.</summary>
public class DddiceRollSessionTests
{
    private static readonly RollableTable Table = Fixtures.Table(DiceExpression.Parse("2d6+1"),
        (3, 6, "Low"), (7, 9, "Middle"), (10, 13, "High"));

    private sealed class Rig
    {
        public FakeDddiceRoller Roller { get; } = new();
        public FixedDice BuiltIn { get; } = new(4);
        public DiceProviderViewModel Providers { get; }
        public RollViewModel Session { get; }
        public List<RollSnapshot> History { get; } = [];

        public Rig()
        {
            Providers = new DiceProviderViewModel(BuiltIn, new DddiceDiceProvider(Roller));
            Providers.Select(DiceProviderKind.Dddice);
            Wait.Until(() => Providers.State == DiceProviderState.Ready, "ready").GetAwaiter().GetResult();
            Session = new RollViewModel(Table, Providers, rolled: History.Add, diceReady: () => Providers.CanRoll);
        }
    }

    [Fact]
    public async Task No_table_result_is_shown_until_the_dice_settle_and_then_the_modifier_is_applied_once()
    {
        var rig = new Rig();

        rig.Session.RollCommand.Execute(null);

        Assert.True(rig.Session.IsRolling);
        Assert.Equal("Rolling…", rig.Session.RollStatus);
        Assert.Empty(rig.Session.Results);                       // the dice are in the air: no result yet
        Assert.Equal("", rig.Session.RollDisplay);
        Assert.Empty(rig.History);                               // and nothing is written to Recent Rolls yet
        Assert.False(rig.Session.RollCommand.CanExecute(null));

        rig.Roller.Settle(FakeDddiceRoller.Twod6(2, 5));         // dddice's roll:finished
        await rig.Session.RollTask;

        Assert.False(rig.Session.IsRolling);
        Assert.Equal("Rolled 8 (2d6+1)", rig.Session.RollDisplay);
        Assert.Equal("Middle", Assert.Single(rig.Session.Results).Text);
        var snapshot = Assert.Single(rig.History);               // the same history record a Built-in roll makes: the FINAL value
        Assert.Equal(8, snapshot.RollValue);
        Assert.Equal("2d6+1", snapshot.DiceText);
        Assert.True(rig.Session.RollCommand.CanExecute(null));
    }

    [Fact]
    public async Task Pressing_Roll_again_while_the_dice_are_still_rolling_does_nothing()
    {
        var rig = new Rig();
        rig.Session.RollCommand.Execute(null);

        rig.Session.RollCommand.Execute(null);
        rig.Session.RollCommand.Execute(null);
        var joined = rig.Session.RollAsync();                    // even calling it directly only joins the roll under way

        Assert.Single(rig.Roller.Requests);
        rig.Roller.Settle(FakeDddiceRoller.Twod6(1, 1));
        await joined;
        Assert.Single(rig.History);
    }

    [Fact]
    public async Task A_manual_roll_never_contacts_dddice_and_resolves_the_entered_final_value_as_before()
    {
        var rig = new Rig();
        rig.Session.ManualRollText = "9";

        rig.Session.ResolveManualCommand.Execute(null);

        Assert.Empty(rig.Roller.Requests);                       // no dice, no request
        Assert.Equal(0, rig.BuiltIn.Calls);
        Assert.Equal("Middle", Assert.Single(rig.Session.Results).Text);
        Assert.Equal(9, Assert.Single(rig.History).RollValue);   // 9 is final: the +1 is not added again
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Manual_entry_still_works_while_dddice_is_not_ready()
    {
        var roller = new FakeDddiceRoller { PrepareGate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var providers = new DiceProviderViewModel(new FixedDice(4), new DddiceDiceProvider(roller));
        providers.Select(DiceProviderKind.Dddice);
        var session = new RollViewModel(Table, providers, diceReady: () => providers.CanRoll);

        Assert.False(session.RollCommand.CanExecute(null));      // Preparing: Roll is off
        session.ManualRollText = "5";
        session.ResolveManualCommand.Execute(null);

        Assert.Equal("Low", Assert.Single(session.Results).Text);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task A_failed_dddice_roll_leaves_the_previous_result_untouched_and_explains()
    {
        var rig = new Rig();
        rig.Session.RollCommand.Execute(null);
        rig.Roller.Settle(FakeDddiceRoller.Twod6(6, 6));          // 13
        await rig.Session.RollTask;
        Assert.Equal("High", rig.Session.Results.Single().Text);

        rig.Session.RollCommand.Execute(null);
        rig.Roller.FailRoll("The dddice roll did not finish within 30 seconds.");
        await rig.Session.RollTask;

        Assert.False(rig.Session.IsRolling);
        Assert.Equal("High", rig.Session.Results.Single().Text);  // still the last successful roll
        Assert.Equal("Rolled 13 (2d6+1)", rig.Session.RollDisplay);
        Assert.Single(rig.History);                               // nothing recorded for the failure
        Assert.Contains("did not finish", rig.Session.Message);
        Assert.Equal(DiceProviderState.Failed, rig.Providers.State);
        Assert.False(rig.Session.RollCommand.CanExecute(null));   // Roll waits for Try again / Use Built-in Dice
        Assert.Equal(0, rig.BuiltIn.Calls);                       // and Built-in was never used automatically
    }

    [Fact]
    public async Task After_a_failure_Use_Built_in_Dice_lets_the_same_screen_roll_immediately()
    {
        var rig = new Rig();
        rig.Session.RollCommand.Execute(null);
        rig.Roller.FailRoll("The connection to dddice was lost during the roll.");
        await rig.Session.RollTask;

        rig.Providers.UseBuiltInCommand.Execute(null);
        rig.Session.RollCommand.Execute(null);                    // Built-in is instant: no waiting

        Assert.False(rig.Session.IsRolling);
        Assert.Equal(1, rig.BuiltIn.Calls);
        Assert.Equal("Rolled 4 (2d6+1)", rig.Session.RollDisplay);
    }

    [Fact]
    public async Task Cancelling_a_roll_in_progress_is_clean_no_result_no_history_and_Roll_works_again()
    {
        var rig = new Rig();
        rig.Session.RollCommand.Execute(null);

        rig.Session.CancelRoll();
        await rig.Session.RollTask;

        Assert.True(rig.Roller.LastRollToken.IsCancellationRequested);
        Assert.False(rig.Session.IsRolling);
        Assert.Empty(rig.Session.Results);
        Assert.Empty(rig.History);
        Assert.Equal(DiceProviderState.Ready, rig.Providers.State);   // a cancelled roll is not a dddice failure
        Assert.True(rig.Session.RollCommand.CanExecute(null));
    }

    [Fact]
    public async Task Switching_to_Built_in_during_a_roll_abandons_the_roll_and_Built_in_takes_over()
    {
        var rig = new Rig();
        rig.Session.RollCommand.Execute(null);

        rig.Providers.Select(DiceProviderKind.BuiltIn);
        await rig.Session.RollTask;

        Assert.Empty(rig.Session.Results);
        Assert.Empty(rig.History);
        rig.Session.RollCommand.Execute(null);
        Assert.Equal("Rolled 4 (2d6+1)", rig.Session.RollDisplay);
    }

    [Fact]
    public async Task A_table_with_dice_dddice_cannot_show_says_so_and_keeps_dddice_ready()
    {
        var rig = new Rig();
        var session = new RollViewModel(Fixtures.Table(new DiceExpression(1, 7), (1, 7, "Any")), rig.Providers, diceReady: () => rig.Providers.CanRoll);

        session.RollCommand.Execute(null);
        await session.RollTask;

        Assert.Contains("d7", session.Message);
        Assert.Empty(session.Results);
        Assert.Equal(DiceProviderState.Ready, rig.Providers.State);
    }

    [Fact]
    public async Task Built_in_rolls_still_apply_immediately_through_the_provider_selector()
    {
        var providers = new DiceProviderViewModel(new FixedDice(7), new DddiceDiceProvider(new FakeDddiceRoller()));
        var session = new RollViewModel(Table, providers, diceReady: () => providers.CanRoll);

        session.RollCommand.Execute(null);   // no awaiting: Built-in completes synchronously

        Assert.Equal("Rolled 7 (2d6+1)", session.RollDisplay);
        Assert.False(session.IsRolling);
        await Task.CompletedTask;
    }
}

/// <summary>The shell: history is written after the dice settle, and leaving a table abandons its roll.</summary>
public class DddiceShellTests
{
    private static (TempDatabase Temp, TableForge.Data.AppDatabase Db, MainViewModel Main, FakeDddiceRoller Roller, DiceProviderViewModel Providers, RollableTable Table) Start()
    {
        var temp = new TempDatabase();
        var db = temp.Open();
        var collection = db.CreateCollection("Solo");
        var table = Fixtures.Table(DiceExpression.Parse("2d6+1"), (3, 6, "Low"), (7, 9, "Middle"), (10, 13, "High"));
        table.CollectionId = collection.Id;
        table.Name = "Patrols";
        table = db.SaveTable(table);
        var roller = new FakeDddiceRoller();
        var providers = new DiceProviderViewModel(new FixedDice(4), new DddiceDiceProvider(roller));
        providers.Select(DiceProviderKind.Dddice);
        Wait.Until(() => providers.State == DiceProviderState.Ready, "ready").GetAwaiter().GetResult();
        var main = new MainViewModel(db, providers);
        return (temp, db, main, roller, providers, table);
    }

    [Fact]
    public async Task A_dddice_roll_creates_the_normal_Recent_Roll_only_after_the_dice_settle()
    {
        var (temp, db, main, roller, _, table) = Start();
        using (temp)
        using (db)
        {
            main.OpenRecentTableCommand.Execute(new TableSummary(table.Id, table.Name, table.Dice));
            var session = (RollViewModel)main.Current!;

            session.RollCommand.Execute(null);
            Assert.Empty(main.RecentRolls);                       // rolling: nothing recorded

            roller.Settle(FakeDddiceRoller.Twod6(3, 4));
            await session.RollTask;

            var recent = Assert.Single(main.RecentRolls);
            Assert.Equal("Patrols", recent.TableName);
            Assert.Equal("8", recent.RollDisplay);                // the final result after the modifier, as a Built-in roll would store it
            Assert.Equal("Middle", recent.Item.ResultText);
        }
    }

    [Fact]
    public async Task Opening_another_table_abandons_a_roll_still_in_the_air_and_it_never_writes_history()
    {
        var (temp, db, main, roller, _, table) = Start();
        using (temp)
        using (db)
        {
            var summary = new TableSummary(table.Id, table.Name, table.Dice);
            main.OpenRecentTableCommand.Execute(summary);
            var first = (RollViewModel)main.Current!;
            first.RollCommand.Execute(null);

            main.OpenRecentTableCommand.Execute(summary);          // the person opens the table again: a fresh screen

            await first.RollTask;
            Assert.True(roller.LastRollToken.IsCancellationRequested);
            Assert.Empty(main.RecentRolls);
        }
    }
}

/// <summary>The window: Built-in shows no dice panel; dddice shows it with its preparation message.</summary>
[Collection("UI")]
public class DiceSelectorViewTests
{
    [Fact]
    public void The_dice_selector_and_panel_follow_the_chosen_provider()
    {
        Sta.Run(() =>
        {
            using var temp = new TempDatabase();
            using var db = temp.Open();
            var roller = new FakeDddiceRoller { PrepareGate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
            var providers = new DiceProviderViewModel(new FixedDice(4), new DddiceDiceProvider(roller));
            var window = new MainWindow
            {
                DataContext = new MainViewModel(db, providers),
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -20000, Top = -20000, ShowInTaskbar = false, ShowActivated = false,
            };
            window.Show();
            try
            {
                void Layout() { window.Dispatcher.Invoke(DispatcherPriority.ContextIdle, () => { }); window.UpdateLayout(); }
                Layout();

                var builtIn = (RadioButton)window.FindName("BuiltInRadio");
                var dddice = (RadioButton)window.FindName("DddiceRadio");
                var panel = (FrameworkElement)window.FindName("DicePanel");
                var status = (TextBlock)window.FindName("DiceStatusText");
                Assert.True(builtIn.IsChecked);
                Assert.False(dddice.IsChecked);
                Assert.Equal(Visibility.Collapsed, panel.Visibility);           // Built-in: no panel at all

                dddice.IsChecked = true;
                Layout();

                Assert.Equal(DiceProviderKind.Dddice, providers.Selected);
                Assert.Equal(Visibility.Visible, panel.Visibility);
                Assert.Equal("Preparing dddice…", status.Text);

                var host = (Border)window.FindName("DiceHost");
                var stand = new Border();
                window.AttachDiceView(stand);                                     // where the real WebView2 is placed
                Assert.Same(stand, host.Child);

                builtIn.IsChecked = true;
                Layout();
                Assert.Equal(DiceProviderKind.BuiltIn, providers.Selected);
                Assert.Equal(Visibility.Collapsed, panel.Visibility);
            }
            finally { window.Close(); }
        });
    }
}
