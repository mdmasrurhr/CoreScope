using System;
using System.Threading;
using System.Threading.Tasks;
using CoreScope.Core.ViewModels;

namespace CoreScope.Core.Fixes;

/// <summary>
/// Runs fix buttons for the UI: marks the finding busy, runs the fix, shows the outcome on the card, and re-checks the PC
/// afterwards so a fixed problem disappears (or turns into a pass) by itself.
/// </summary>
public sealed class FixCoordinator
{
    private readonly IFixHost _host;
    private readonly SynchronizationContext _ui;
    private readonly Action _recheck;
    private readonly Action<string, bool> _announce;

    /// <param name="host">Dialogs, navigation and unlocking.</param>
    /// <param name="ui">UI thread context; results are posted back to it.</param>
    /// <param name="recheck">Re-collects health data and re-evaluates findings.</param>
    /// <param name="announce">Shows a one-line message at the top of the Insights page (message, success).</param>
    public FixCoordinator(IFixHost host, SynchronizationContext ui, Action recheck, Action<string, bool> announce)
    {
        _host = host;
        _ui = ui;
        _recheck = recheck;
        _announce = announce;
    }

    /// <summary>Runs the button's fix and reports on the finding's card. Safe to call on the UI thread.</summary>
    public async void Run(FixButton button)
    {
        var state = button.Owner.State;
        if (state.IsBusy) return;
        state.IsBusy = true;
        state.Result = "";
        try
        {
            var result = await RunAction(button.Action).ConfigureAwait(true);
            state.IsBusy = false;
            if (result.Message.Length > 0)
            {
                state.Result = result.Message;
                state.ResultOk = result.Ok;
                _announce(result.Message, result.Ok);
            }
            InsightActions.Report(button.Owner, result.Ok, result.Message);
        }
        catch (Exception ex)
        {
            Log.Error($"Fix {button.Action.Id}", ex);
            state.IsBusy = false;
            state.Result = "Something went wrong running that fix. See the log for details.";
            state.ResultOk = false;
        }
    }

    /// <summary>Runs an action from outside a finding (for example a button in a guide).</summary>
    public async Task<FixResult> RunAction(FixAction action)
    {
        var result = await FixRunner.RunAsync(action, _host).ConfigureAwait(true);
        if (result.Ok && action.Kind == FixKind.Command) Recheck();
        return result;
    }

    private void Recheck()
    {
        // Give Windows a moment to apply the change, then look again.
        _ = Task.Delay(1500).ContinueWith(_ => _ui.Post(__ => _recheck(), null), TaskScheduler.Default);
    }
}
