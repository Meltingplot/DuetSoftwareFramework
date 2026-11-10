using DuetAPI;
using DuetAPI.Commands;
using DuetAPI.ObjectModel;
using DuetControlServer.Codes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Nito.AsyncEx;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DuetControlServer.Model;

/// <summary>
/// Singleton hosted service that tracks and evaluates M581.1 expression-based triggers
/// whose expressions reference SBC object model fields (which RRF cannot evaluate)
/// </summary>
/// <param name="codeFactory">Factory to create code instances</param>
/// <param name="expressions">Expression evaluator</param>
/// <param name="model">Object model</param>
/// <param name="observer">Object model observer</param>
/// <param name="logger">Logger</param>
public sealed class SbcTriggerService(
    CodeFactory codeFactory,
    Codes.Meta.Expressions expressions,
    ObjectModel model,
    Observer observer,
    ILogger<SbcTriggerService> logger) : BackgroundService
{
    private sealed class TriggerState
    {
        public required string Expression { get; init; }
        public required string[] WatchedPaths { get; init; }
        public int Condition { get; set; }
        public bool LastResult { get; set; }
    }

    /// <summary>
    /// Builds a dot-notation path string from an observer path array (uses property names and collection names).
    /// </summary>
    private static string BuildObservedPath(object[] path)
    {
        return string.Join('.', path.Select(static e => e switch
        {
            string s => s,
            ItemPathNode node => node.Name,
            _ => string.Empty
        }).Where(static s => s.Length > 0));
    }

    /// <summary>
    /// Returns true when the observer-reported path is related to at least one of the watched expression paths.
    /// </summary>
    private static bool IsPathRelevant(object[] changedPath, string[] watchedPaths)
    {
        if (watchedPaths.Length == 0)
        {
            return false;
        }

        string observed = BuildObservedPath(changedPath);
        if (observed.Length == 0)
        {
            return false;
        }

        foreach (string watched in watchedPaths)
        {
            if (string.Equals(observed, watched, StringComparison.OrdinalIgnoreCase) ||
                observed.StartsWith(watched + '.', StringComparison.OrdinalIgnoreCase) ||
                watched.StartsWith(observed + '.', StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private readonly Dictionary<int, TriggerState> _triggers = [];
    private readonly AsyncAutoResetEvent _signal = new(false);

    /// <summary>
    /// Subscribes to OM change notifications when started
    /// </summary>
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        observer.OnPropertyPathChanged += OnObservedPropertyChanged;
        return base.StartAsync(cancellationToken);
    }

    /// <summary>
    /// Unsubscribes from OM change notifications when stopped
    /// </summary>
    public override Task StopAsync(CancellationToken cancellationToken)
    {
        observer.OnPropertyPathChanged -= OnObservedPropertyChanged;
        return base.StopAsync(cancellationToken);
    }

    /// <summary>
    /// Handle M581 and M581.1 like RRF's TriggerItem::Configure does for both codes, because they share the trigger numbers.
    /// DSF evaluates a trigger only if its M581.1 expression contains SBC fields and leaves every other trigger to RRF
    /// </summary>
    /// <param name="code">M581 or M581.1 code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Result message, or null to pass the code on to RRF</returns>
    public async Task<Message?> ConfigureAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        if (!code.TryGetInt('T', out int triggerNumber) || triggerNumber < 0 || triggerNumber >= 32)
        {
            // Let RRF report the invalid trigger number
            return null;
        }

        // Without P-1, inputs, axis endstops or an expression the code reports the trigger or changes its enable condition
        if (code.Parameters.All(parameter => parameter.Letter is 'T' or 'R' or 'S'))
        {
            return await ReportOrChangeConditionAsync(triggerNumber, code, cancellationToken);
        }

        // Everything else creates or deletes a trigger that RRF evaluates, so RRF takes the slot back
        if (code.MinorNumber != 1 || !code.TryGetParameter('P', out CodeParameter? pParam) || pParam.Type != typeof(string) || !expressions.ContainsSbcFields((string)pParam))
        {
            Remove(triggerNumber);
            return null;
        }
        string triggerExpression = (string)pParam;

        int condition = code.TryGetInt('R', out int rParam) ? rParam : 0;

        // Clear any existing pin-based RRF trigger for this slot to avoid duplicate firing
        Commands.Code clearCode = codeFactory.Create();
        clearCode.Flags = CodeFlags.IsInternallyProcessed;
        clearCode.Channel = code.Channel;
        clearCode.Type = CodeType.MCode;
        clearCode.MajorNumber = 581;
        clearCode.Parameters = [new('T', triggerNumber), new('P', -1)];
        await clearCode.ExecuteAsync(cancellationToken);

        // Evaluate the expression immediately to seed LastResult, so that a true expression
        // at registration time does not cause a spurious trigger on the first OM change
        bool initialResult = await EvaluateInitialResultAsync(triggerExpression, cancellationToken);

        string[] watchedPaths = [.. expressions.ExtractFieldPaths(triggerExpression)];
        lock (_triggers)
        {
            _triggers[triggerNumber] = new TriggerState
            {
                Expression = triggerExpression,
                WatchedPaths = watchedPaths,
                Condition = condition,
                LastResult = initialResult
            };
        }

        logger.LogInformation("Registered SBC trigger {Number} with expression '{Expression}' (condition {Condition})", triggerNumber, triggerExpression, condition);
        return new Message();
    }

    /// <summary>
    /// Report an SBC trigger or change its enable condition (R parameter), like RRF does for its own triggers
    /// </summary>
    /// <param name="triggerNumber">Trigger number (0–31)</param>
    /// <param name="code">M581 or M581.1 code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Result message, or null to pass the code on to RRF because RRF evaluates this trigger</returns>
    private async Task<Message?> ReportOrChangeConditionAsync(int triggerNumber, Commands.Code code, CancellationToken cancellationToken)
    {
        TriggerState? state;
        lock (_triggers)
        {
            _triggers.TryGetValue(triggerNumber, out state);
        }
        if (state is null)
        {
            return null;
        }

        if (code.TryGetInt('R', out int condition))
        {
            // Determine the initial state again like RRF, so that enabling the trigger does not fire it for an expression that is already true
            bool initialResult = await EvaluateInitialResultAsync(state.Expression, cancellationToken);
            lock (_triggers)
            {
                state.Condition = condition;
                state.LastResult = initialResult;
            }
            return new Message();
        }

        string enableCondition = state.Condition switch
        {
            < 0 => "if enabled would fire",
            1 => "fires only when printing",
            2 => "fires only when not printing",
            _ => "fires"
        };
        return new Message(MessageType.Success, $"Trigger {triggerNumber} {enableCondition} {((state.Condition > 0) ? "and" : "when")} expression {{{state.Expression}}} becomes true");
    }

    /// <summary>
    /// Evaluate a trigger expression to get its state before the trigger is checked for the first time
    /// </summary>
    /// <param name="expression">Trigger expression</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Whether the expression is true</returns>
    private async Task<bool> EvaluateInitialResultAsync(string expression, CancellationToken cancellationToken)
    {
        try
        {
            Commands.Code evalCode = codeFactory.Create();
            evalCode.Channel = CodeChannel.Trigger;
            object? evalResult = await expressions.EvaluateExpressionToValueAsync(evalCode, expression, false, cancellationToken);
            return evalResult is bool b && b;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e, "Failed to evaluate initial state of M581.1 expression '{Expression}'", expression);
            return false;
        }
    }

    /// <summary>
    /// Remove a DSF-managed trigger, if one is registered for the given slot.
    /// Called when M581 or M581.1 (re)configures or deletes a trigger so RRF takes back ownership.
    /// </summary>
    /// <param name="triggerNumber">Trigger number (0–31)</param>
    private void Remove(int triggerNumber)
    {
        lock (_triggers)
        {
            if (_triggers.Remove(triggerNumber))
            {
                logger.LogInformation("Removed SBC trigger {Number} (superseded by M581 or M581.1)", triggerNumber);
            }
        }
    }

    /// <summary>
    /// Called by the OM observer whenever any property changes.
    /// Signals the evaluation loop only when the changed path is referenced by at least one registered trigger expression
    /// </summary>
    private void OnObservedPropertyChanged(object[] path, PropertyChangeType changeType, object? value)
    {
        lock (_triggers)
        {
            if (_triggers.Values.Any(state => IsPathRelevant(path, state.WatchedPaths)))
            {
                _signal.Set();
            }
        }
    }

    /// <summary>
    /// Background loop that re-evaluates all registered triggers after each batch of OM changes.
    /// Fires the corresponding trigger macro on a false-to-true transition.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            // Wait for a signal that an observed property has changed, or for cancellation
            try
            {
                await _signal.WaitAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // expected on shutdown
                break;
            }

            // Snapshot the current trigger map before evaluating
            (int number, TriggerState state)[] snapshot;
            lock (_triggers)
            {
                snapshot = [.. _triggers.Select(kvp => (kvp.Key, kvp.Value))];
            }

            foreach ((int triggerNum, TriggerState state) in snapshot)
            {
                try
                {
                    Commands.Code evalCode = codeFactory.Create();
                    evalCode.Channel = CodeChannel.Trigger;
                    object? result = await expressions.EvaluateExpressionToValueAsync(evalCode, state.Expression, false, stoppingToken);
                    bool newResult = result is bool b && b;

                    bool fire;
                    lock (_triggers)
                    {
                        fire = newResult && !state.LastResult;
                        state.LastResult = newResult;
                    }

                    if (fire && await IsTriggerConditionMetAsync(state.Condition, stoppingToken))
                    {
                        logger.LogInformation("SBC trigger {Number} fired (expression: '{Expression}')", triggerNum, state.Expression);
                        Commands.Code triggerCode = codeFactory.Create();
                        triggerCode.Flags = CodeFlags.IsInternallyProcessed | CodeFlags.Asynchronous;
                        triggerCode.Channel = CodeChannel.Trigger;
                        triggerCode.Type = CodeType.MCode;
                        triggerCode.MajorNumber = 98;
                        triggerCode.Parameters = [new('P', $"trigger{triggerNum}.g")];
                        _ = triggerCode.ExecuteAsync();
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    // expected on shutdown — just exit the loop without logging a warning
                    break;
                }
                catch (Exception e)
                {
                    logger.LogWarning(e, "Failed to evaluate SBC trigger expression '{Expression}'", state.Expression);
                }
            }
        }
    }

    /// <summary>
    /// Returns true if the trigger condition (R parameter) allows firing given the current print status.
    /// </summary>
    /// <param name="condition">-1 = disabled, 0 = always, 1 = only while printing, 2 = only while not printing</param>
    /// <param name="cancellationToken">Optional cancellation token</param>
    private async Task<bool> IsTriggerConditionMetAsync(int condition, CancellationToken cancellationToken)
    {
        if (condition < 0)
        {
            return false;
        }
        if (condition == 0)
        {
            return true;
        }

        using (await model.AccessReadOnlyAsync(cancellationToken))
        {
            // A feed hold hides the print state behind MachineStatus.Held, so a held machine counts as printing while a job file is loaded
            bool isPrinting = model.State.Status is MachineStatus.Processing
                or MachineStatus.Pausing
                or MachineStatus.Paused
                or MachineStatus.Resuming
                || (model.State.Status is MachineStatus.Held && !string.IsNullOrEmpty(model.Job.File.FileName));
            return condition == 1 ? isPrinting : !isPrinting;
        }
    }
}
